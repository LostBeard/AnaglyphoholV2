// Summarizes window.__costSamples (see sample-cost.js): frames, FPS, and per-input-size depth / 3D / recompile ms
// (median, p90), skipping the first 3 s of warm-up. Also returns the last 5 raw samples.
(() => {
  const s = window.__costSamples;
  if (!s || !s.length) return 'no samples (run sample-cost.js first, with Stats producing anaglyphohol-cost)';
  const t0 = s[0].t, warm = t0 + 3000;
  const parse = v => {
    const m = /depth=([\d.]+)ms recompile=([\d.]+)ms 3d=([\d.]+)ms input=(\d+x\d+)/.exec(v || '');
    return m ? { depth: +m[1], recompile: +m[2], r3d: +m[3], input: m[4] } : null;
  };
  const q = (a, p) => { const b = [...a].sort((x, y) => x - y); return b.length ? b[Math.min(b.length - 1, Math.floor(p * b.length))] : null; };
  const by = {};
  let recompiles = 0;
  for (const x of s) {
    const p = parse(x.v);
    if (!p) continue;
    if (p.recompile > 0) { recompiles++; continue; }
    if (x.t < warm) continue;
    (by[p.input] ??= { n: 0, depth: [], r3d: [] });
    by[p.input].n++; by[p.input].depth.push(p.depth); by[p.input].r3d.push(p.r3d);
  }
  const span = (s[s.length - 1].t - Math.max(t0, warm)) / 1000;
  const sizes = {};
  for (const [k, v] of Object.entries(by))
    sizes[k] = { frames: v.n, depthMedian: q(v.depth, 0.5), depthP90: q(v.depth, 0.9), r3dMedian: q(v.r3d, 0.5) };
  const steady = s.filter(x => x.t >= warm).length;
  return JSON.stringify({ samples: s.length, recompileFrames: recompiles, steadyFps: +(steady / Math.max(span, 0.001)).toFixed(1), sizes,
    last: s.slice(-5).map(x => x.v) }, null, 1);
})()
