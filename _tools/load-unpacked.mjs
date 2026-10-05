// load-unpacked.mjs - installs an unpacked extension into the debug Chrome over CDP (Extensions.loadUnpacked).
// Branded Chrome ignores --load-extension (MEASURED 2026-10-05, Chrome 151: a FRESH profile launched by launch-chrome.ps1
// had no Anaglyphohol at all; older profiles kept the extension only because it was already installed). The CDP method
// worked on the remote-debugging PORT with no extra switch (Chrome 151). A fresh install fires
// runtime.onInstalled(install), so this also tests the first-install path (get-started page, shader warm-up).
// usage: node _tools/load-unpacked.mjs <extension dir>      (CDP_PORT env, default 9224)
import path from 'node:path';
const dir = path.resolve(process.argv[2] || '');
const port = process.env.CDP_PORT || 9224;
const ver = await (await fetch(`http://localhost:${port}/json/version`)).json();
const ws = new WebSocket(ver.webSocketDebuggerUrl);
await new Promise(r => ws.onopen = r);
const reply = await new Promise(r => {
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id === 1) r(m); };
    ws.send(JSON.stringify({ id: 1, method: 'Extensions.loadUnpacked', params: { path: dir } }));
});
if (reply.error) { ws.close(); console.error(`loadUnpacked failed: ${reply.error.message}`); process.exit(1); }
console.log(`loaded ${dir} as ${reply.result.id}`);
// loadUnpacked on an ALREADY-installed path does not restart the service worker: Chrome kept the previous build's
// importScripts'd main.classic.js, whose boot config named _framework files the new build no longer has
// (MEASURED 2026-10-05: ERR_FILE_NOT_FOUND for every *.wasm, .NET never started, every runtime message held forever).
// So also RELOAD it, from a chrome://extensions page (developerPrivate), as reload-ext.js does.
let next = 1;
const call = (method, params = {}, sessionId) => new Promise(r => {
    const i = ++next;
    const prev = ws.onmessage;
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id === i) { ws.onmessage = prev; r(m); } };
    ws.send(JSON.stringify({ id: i, method, params, sessionId }));
});
const { result: { targetId } } = await call('Target.createTarget', { url: 'chrome://extensions' });
const { result: { sessionId } } = await call('Target.attachToTarget', { targetId, flatten: true });
await new Promise(r => setTimeout(r, 800));
const rr = await call('Runtime.evaluate', {
    expression: `new Promise(res => chrome.developerPrivate.reload(${JSON.stringify(reply.result.id)}, { failQuietly: true }, () => res('reloaded')))`,
    awaitPromise: true, returnByValue: true }, sessionId);
await call('Target.closeTarget', { targetId });
ws.close();
console.log(rr.result?.result?.value ?? `reload failed: ${JSON.stringify(rr.result ?? rr.error)}`);
