// Summarizes window.__costSamples (see sample-cost.js): frames, FPS, and per-input-size depth / 3D / recompile ms
// (median, p90), skipping the first 3 s of warm-up. Each rendered frame carries seq=N; a repeated seq is the same frame
// seen twice and is counted once.
// ⚠️ Off recommended sites a free-tier video gets 30 s of 3D playtime (TrackedMediaElement.Limit), then stops
// rendering: reload the page before each window and keep it under ~25 s. "lastFrameAgeMs" > ~200 means it stopped.
(() => {
  const s = window.__costSamples;
  if (!s || !s.length) return 'no samples (run sample-cost.js first, with Stats producing anaglyphohol-cost)';
  const parse = v => {
    const m = /seq=(\d+) depth=([\d.]+)ms recompile=([\d.]+)ms 3d=([\d.]+)ms input=(\d+x\d+)/.exec(v || '');
    return m ? { seq: +m[1], depth: +m[2], recompile: +m[3], r3d: +m[4], input: m[5] } : null;
  };
  const frames = [];
  const seen = new Set();
  for (const x of s) {
    const p = parse(x.v);
    if (!p || seen.has(p.seq)) continue;
    seen.add(p.seq);
    frames.push({ t: x.t, ...p });
  }
  if (!frames.length) return 'no parsable frames (old build without seq=?)';
  const t0 = frames[0].t, warm = t0 + 3000;
  const q = (a, p) => { const b = [...a].sort((x, y) => x - y); return b.length ? b[Math.min(b.length - 1, Math.floor(p * b.length))] : null; };
  const by = {};
  let recompiles = 0;
  for (const f of frames) {
    if (f.recompile > 0) { recompiles++; continue; }
    if (f.t < warm) continue;
    (by[f.input] ??= { depth: [], r3d: [] });
    by[f.input].depth.push(f.depth); by[f.input].r3d.push(f.r3d);
  }
  const steady = frames.filter(f => f.t >= warm);
  const span = steady.length > 1 ? (steady[steady.length - 1].t - steady[0].t) / 1000 : 0;
  const sizes = {};
  for (const [k, v] of Object.entries(by))
    sizes[k] = { frames: v.depth.length, depthMedian: q(v.depth, 0.5), depthP10: q(v.depth, 0.1), depthP90: q(v.depth, 0.9), r3dMedian: q(v.r3d, 0.5) };
  return JSON.stringify({ frames: frames.length, duplicatesIgnored: s.length - frames.length, recompileFrames: recompiles,
    steadyFps: span > 0 ? +((steady.length - 1) / span).toFixed(1) : null, lastFrameAgeMs: Math.round(performance.now() - frames[frames.length - 1].t),
    sizes }, null, 1);
})()
