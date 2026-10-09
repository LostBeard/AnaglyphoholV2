// probe-v3-overlay-cleanup.mjs - the 3.x -> 4.x UPDATE in Firefox with 3.x overlays on an open page.
// A throwaway Firefox (temp profile, hidden window, BiDi on <port>) resolves EVERY host to 127.0.0.1
// (network.dns.forceResolve), so http://www.bing.com:8765/ is our test page while 3.x sees one of its recommended
// sites and turns 3D on by itself. 3.x is installed, converts the page, then the 4.x build is installed over it (same
// add-on id) with the page left open. Counts 3.x / 4.x toolbars and overlays over time.
// Needs the test page server on :8765. usage: node _tools/probe-v3-overlay-cleanup.mjs <3.x firefox dir | v4only> <4.x firefox dir> [port=9231]
// v4only: the control - the 4.x build alone on the same page and host.
import { execSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const [v3Dir, v4Dir, portArg] = process.argv.slice(2);
const port = +(portArg || 9231);
const sleep = ms => new Promise(r => setTimeout(r, ms));
try { const t = new WebSocket(`ws://127.0.0.1:${port}/session`); await new Promise((res, rej) => { t.onopen = res; t.onerror = rej; }); t.close(); console.error(`port ${port} is already in use`); process.exit(2); } catch { }
const profile = path.join(os.tmpdir(), `v3-overlay-probe-${port}`);
fs.rmSync(profile, { recursive: true, force: true }); fs.mkdirSync(profile);
fs.writeFileSync(path.join(profile, 'user.js'), [
    'user_pref("network.dns.forceResolve", "127.0.0.1");',
    'user_pref("dom.webgpu.enabled", true);',
    'user_pref("media.autoplay.default", 0);',
    'user_pref("browser.shell.checkDefaultBrowser", false);',
    'user_pref("datareporting.policy.dataSubmissionEnabled", false);',
    'user_pref("network.proxy.type", 0);',
    // no HSTS preload / https-first: http://www.bing.com:8765/ must stay plain http to reach the local server
    'user_pref("network.stricttransportsecurity.preloadlist", false);',
    'user_pref("dom.security.https_first", false);',
    'user_pref("dom.security.https_first_schemeless", false);',
].join('\n'));
// hidden window (WMI ShowWindow=0), PID recorded, killed with its children at the end
const ffPid = +execSync(`powershell -NoProfile -Command "$s=New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ShowWindow=[uint16]0}; (Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{CommandLine='\\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\\" -no-remote -new-instance -profile \\"${profile}\\" --remote-debugging-port ${port} about:blank'; ProcessStartupInformation=$s}).ProcessId"`).toString().trim();
let ws;
try {
    for (let i = 0; i < 80 && !ws; i++) { try { const w = new WebSocket(`ws://127.0.0.1:${port}/session`); await new Promise((res, rej) => { w.onopen = res; w.onerror = rej; }); ws = w; } catch { await sleep(250); } }
    let next = 0; const pending = new Map();
    const logs = [];
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id != null && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }
        if (m.method === 'log.entryAdded' && /Anaglyphohol/.test(m.params?.text || '')) { logs.push(m.params.text); console.log('  log:', m.params.text.slice(0, 220)); } };
    const call = (method, params = {}) => new Promise(r => { const id = ++next; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
    await call('session.new', { capabilities: {} });
    await call('session.subscribe', { events: ['log.entryAdded'] });
    const { result: { context } } = await call('browsingContext.create', { type: 'tab' });
    await call('browsingContext.navigate', { context, url: 'http://www.bing.com:8765/', wait: 'complete' });
    const ev = async expr => (await call('script.evaluate', { expression: expr, target: { context }, awaitPromise: true })).result?.result?.value;
    console.log('page:', await ev('location.href + " | " + document.title'));
    const counts = () => ev(`JSON.stringify({
        v3toolbars: document.querySelectorAll('body > div[style="position: fixed; top: 0; left: 0; right: 0; height: 0; overflow: visible; display: flex; flex-direction: row; justify-content:center; z-index: 65536;"]').length,
        v3overlays: [...document.querySelectorAll('div[style^="position: absolute; pointer-events: none;"]')].filter(d => d.shadowRoot && d.shadowRoot.querySelector('canvas')).length,
        v4toolbars: document.querySelectorAll('[anaglyphohol-ui]').length,
        v4overlays: document.querySelectorAll('.custom-media-overlay-canvas').length,
        v4states: [...document.querySelectorAll('[anaglyphohol-state]')].map(e => e.getAttribute('anaglyphohol-state')).join(',') })`);
    let r;
    if (v3Dir !== 'v4only') {
        r = await call('webExtension.install', { extensionData: { type: 'path', path: path.resolve(v3Dir) } });
        console.log('3.x install:', r.error ? `${r.error} ${r.message}` : r.result.extension);
        for (let i = 0; i < 6; i++) { await sleep(5000); console.log(`  3.x +${(i + 1) * 5}s`, await counts()); }
        console.log('  3.x canvas styles:', await ev(`JSON.stringify([...document.querySelectorAll('div[style^="position: absolute; pointer-events: none;"]')].map(d => d.shadowRoot?.querySelector('canvas')?.getAttribute('style')))`));
    }
    r = await call('webExtension.install', { extensionData: { type: 'path', path: path.resolve(v4Dir) } });
    console.log('4.x install (update, page NOT reloaded):', r.error ? `${r.error} ${r.message}` : r.result.extension);
    for (let i = 0; i < 6; i++) { await sleep(5000); console.log(`  4.x +${(i + 1) * 5}s`, await counts()); }
    await call('session.end', {});
} finally {
    try { ws?.close(); } catch { }
    try { execSync(`taskkill /PID ${ffPid} /T /F`, { stdio: 'ignore' }); } catch { }
    await sleep(2000);
    fs.rmSync(profile, { recursive: true, force: true });
}
