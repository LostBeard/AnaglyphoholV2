// Renderer cost of a page load: navigates the matching tab, waits, then reports CDP Performance metrics (main-thread task
// time, script time, JS heap) and how many of the page's same-origin frames booted the Anaglyphohol content script.
// Same-origin iframes share the page's renderer, so the numbers include them - compare a page with and without iframes
// (testpage/iframes.html?n=5 vs ?n=0) to get the per-frame cost of all_frames.
// usage: node perf-metrics.mjs <urlSubstr> <navUrl> <seconds>     (CDP_PORT env, default 9224)
const [, , sub, navUrl, secs] = process.argv;
const port = process.env.CDP_PORT || 9224;
const list = await (await fetch(`http://localhost:${port}/json`)).json();
const t = list.find(x => x.type === 'page' && x.url.includes(sub));
if (!t) { console.error('no target'); process.exit(1); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
let id = 0; const pending = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
await new Promise(r => ws.onopen = r);
await send('Performance.enable', { timeDomain: 'threadTicks' });
await send('Page.enable');
await send('Page.navigate', { url: navUrl });
await new Promise(r => setTimeout(r, Number(secs) * 1000));
await send('HeapProfiler.collectGarbage');
const m = await send('Performance.getMetrics');
const get = n => m.result.metrics.find(x => x.name === n)?.value ?? 0;
const frames = await send('Runtime.evaluate', {
    returnByValue: true,
    expression: `(() => {
        const booted = d => [...d.querySelectorAll('*')].some(e => e.shadowRoot && e.shadowRoot.querySelector('.extension-content'));
        const docs = [document, ...[...document.querySelectorAll('iframe')].map(f => { try { return f.contentDocument; } catch { return null; } }).filter(Boolean)];
        return { frames: docs.length, booted: docs.filter(booted).length, bootedFrames: docs.filter(booted).map(d => d.location.search || '(top)') };
    })()`,
});
console.log(JSON.stringify({
    url: navUrl,
    taskMs: Math.round(get('TaskDuration') * 1000),
    scriptMs: Math.round(get('ScriptDuration') * 1000),
    jsHeapUsedMB: +(get('JSHeapUsedSize') / 1048576).toFixed(1),
    jsHeapTotalMB: +(get('JSHeapTotalSize') / 1048576).toFixed(1),
    documents: get('Documents'),
    ...frames.result?.result?.value,
}));
ws.close();
