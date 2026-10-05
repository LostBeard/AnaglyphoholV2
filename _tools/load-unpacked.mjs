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
ws.close();
if (reply.error) { console.error(`loadUnpacked failed: ${reply.error.message}`); process.exit(1); }
console.log(`loaded ${dir} as ${reply.result.id}`);
