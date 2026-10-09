// CPU profile ACROSS a navigation: browser-level Tracing (v8 cpu_profiler category) survives the reload that a
// page-level Profiler session does not. Records, navigates the matching tab, waits, then rebuilds one .cpuprofile per
// profiled thread from the ProfileChunk events and saves the busiest one.
// usage: node trace-profile.mjs <urlSubstr> <navUrl> <seconds> <out.cpuprofile>
//   Needs a build with function names: dotnet publish ... -p:WasmNativeStrip=false (same speed, bigger wasm).
//   Analyse: python _tools/cpuprof.py <out> 60   |   python _tools/cpuprof-callers.py <out> <name> 2
const [, , sub, navUrl, secs, out] = process.argv;
const port = process.env.CDP_PORT || 9224;
const fs = await import('node:fs');
const ver = await (await fetch(`http://localhost:${port}/json/version`)).json();
const list = await (await fetch(`http://localhost:${port}/json`)).json();
const page = list.find(x => x.type === 'page' && x.url.includes(sub));
if (!page) { console.error('no target'); process.exit(1); }

function open(url) {
  const ws = new WebSocket(url);
  let id = 0; const pending = new Map(); const handlers = [];
  ws.onmessage = e => {
    const m = JSON.parse(e.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); }
    else for (const h of handlers) h(m);
  };
  const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
  return { ws, send, on: h => handlers.push(h), ready: new Promise(r => ws.onopen = r) };
}

const browser = open(ver.webSocketDebuggerUrl);
await browser.ready;
const events = [];
let done;
const complete = new Promise(r => done = r);
browser.on(m => {
  if (m.method === 'Tracing.dataCollected') events.push(...m.params.value);
  if (m.method === 'Tracing.tracingComplete') done();
});
const st = await browser.send('Tracing.start', {
  traceConfig: { includedCategories: ['disabled-by-default-v8.cpu_profiler', 'v8'], recordMode: 'recordContinuously' },
  transferMode: 'ReportEvents',
});
if (st.error) { console.error(JSON.stringify(st.error)); process.exit(1); }
const pg = open(page.webSocketDebuggerUrl);
await pg.ready;
await pg.send('Page.navigate', { url: navUrl });
console.log('navigated', navUrl);
await new Promise(r => setTimeout(r, Number(secs) * 1000));
await browser.send('Tracing.end');
await complete;

// Rebuild profiles: a "Profile" event (id) starts one, "ProfileChunk" events (same id) add nodes / samples / deltas.
const profiles = new Map();
for (const e of events) {
  if (e.name === 'Profile') profiles.set(`${e.pid}:${e.id}`, { pid: e.pid, tid: e.tid, startTime: e.args.data.startTime, nodes: [], samples: [], timeDeltas: [] });
  if (e.name === 'ProfileChunk') {
    const p = profiles.get(`${e.pid}:${e.id}`);
    if (!p) continue;
    const d = e.args.data;
    if (d.cpuProfile?.nodes) for (const n of d.cpuProfile.nodes) p.nodes.push({ id: n.id, callFrame: n.callFrame, children: [], parent: n.parent });
    if (d.cpuProfile?.samples) p.samples.push(...d.cpuProfile.samples);
    if (d.timeDeltas) p.timeDeltas.push(...d.timeDeltas);
  }
}
let best = null;
for (const p of profiles.values()) {
  const byId = new Map(p.nodes.map(n => [n.id, n]));
  for (const n of p.nodes) if (n.parent != null && byId.has(n.parent)) byId.get(n.parent).children.push(n.id);
  for (const n of p.nodes) delete n.parent;
  p.endTime = p.startTime + p.timeDeltas.reduce((a, b) => a + b, 0);
  console.log(`profile pid ${p.pid} tid ${p.tid}: ${p.nodes.length} nodes, ${p.samples.length} samples`);
  if (!best || p.samples.length > best.samples.length) best = p;
}
if (!best) { console.error('no profile chunks'); process.exit(1); }
fs.writeFileSync(out, JSON.stringify({ nodes: best.nodes, startTime: best.startTime, endTime: best.endTime, samples: best.samples, timeDeltas: best.timeDeltas }));
console.log('saved', out);
browser.ws.close(); pg.ws.close();
