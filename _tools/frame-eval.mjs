// frame-eval.mjs - evaluates an expression in a CROSS-ORIGIN iframe target (an out-of-process iframe is its own CDP
// target of type "iframe"; cdp.cs attaches to pages only). Page realm, awaited, returned by value.
// usage: node _tools/frame-eval.mjs <urlSubstr> <js>      (CDP_PORT env, default 9224)
const [, , sub, expr] = process.argv;
const port = process.env.CDP_PORT || 9224;
const list = await (await fetch(`http://localhost:${port}/json`)).json();
const t = list.find(x => (x.type === 'iframe' || x.type === 'page') && x.url.includes(sub));
if (!t) { console.error(`no target matching ${sub}`); process.exit(1); }
const ws = new WebSocket(t.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
const reply = await new Promise(r => {
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id === 1) r(m); };
    ws.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression: expr, awaitPromise: true, returnByValue: true } }));
});
ws.close();
console.log(JSON.stringify(reply.result?.result?.value ?? reply.result?.exceptionDetails?.text ?? reply.error));
