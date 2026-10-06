// shared-check.mjs - the shared converter's mid-session failure paths, on a real install (fresh profile).
// Needs the test page server on :8765 (launch-chrome.ps1 starts it, or: python -m http.server 8765 --bind 127.0.0.1 --directory _tools/testpage).
// usage: node _tools/shared-check.mjs <extDir> <outDir> <port> <scenario: kill|broken>
//   kill   - the offscreen document is closed under a page that uses it (Target.closeTarget)
//   broken - every GPU submit in the converter throws (stands in for a lost device)
import { spawn, execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const [extDir, outDir, portStr, scenario] = process.argv.slice(2);
const port = +portStr;
// a throwaway profile (a fresh profile = a real first install), deleted after the run
const profile = path.join(os.tmpdir(), `anaglyphohol-check-profile-${port}`);
fs.rmSync(profile, { recursive: true, force: true });
const child = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new', `--remote-debugging-port=${port}`,
    `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check', 'about:blank'], { detached: true, stdio: 'ignore', windowsHide: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws, next = 0;
const pending = new Map();
const logs = [];
const call = (method, params = {}, sessionId) => new Promise(r => { const id = ++next; pending.set(id, r); ws.send(JSON.stringify({ id, method, params, sessionId })); });
const results = [];
const check = (name, ok, detail) => { results.push(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' - ' + detail : ''}`); console.log(results.at(-1)); };
const t0 = Date.now();
const ts = () => `+${((Date.now() - t0) / 1000).toFixed(1)}s`;
try {
    let ver;
    for (let i = 0; i < 60 && !ver; i++) { try { ver = await (await fetch(`http://localhost:${port}/json/version`)).json(); } catch { await sleep(250); } }
    ws = new WebSocket(ver.webSocketDebuggerUrl);
    await new Promise(r => ws.onopen = r);
    ws.onmessage = e => {
        const m = JSON.parse(e.data);
        if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); return; }
        if (m.method === 'Runtime.consoleAPICalled') {
            const text = m.params.args.map(a => a.value ?? a.description ?? '').join(' ');
            if (/Anaglyphohol/.test(text)) { logs.push(`${ts()} [${m.sessionId?.slice(0, 4)}] ${text}`); console.log('  log', logs.at(-1)); }
        }
    };
    const lu = await call('Extensions.loadUnpacked', { path: path.resolve(extDir) });
    if (lu.error) throw new Error(lu.error.message);
    const extId = lu.result.id;
    const attach = async (targetId) => {
        const { result: { sessionId } } = await call('Target.attachToTarget', { targetId, flatten: true });
        await call('Runtime.enable', {}, sessionId);
        return sessionId;
    };
    const ev = async (s, expr) => (await call('Runtime.evaluate', { expression: expr, returnByValue: true, awaitPromise: true }, s)).result?.result?.value;
    // settings: shared converter on; localhost images on, toolbar minimized
    const { result: { targetId: optT } } = await call('Target.createTarget', { url: `chrome-extension://${extId}/app/index.html?$=options` });
    const opt = await attach(optT);
    await sleep(3000);
    await ev(opt, `new Promise(r => chrome.storage.local.set({ sharedConverter: true }, () => chrome.storage.sync.set({ localhost_AnaglyphImagesEnabledSiteKey: 1 }, r)))`);
    console.log(ts(), 'settings', await ev(opt, `new Promise(r => chrome.storage.local.get('sharedConverter', v => r(JSON.stringify(v))))`));
    const openPage = async () => {
        const { result: { targetId } } = await call('Target.createTarget', { url: 'about:blank' });
        const s = await attach(targetId);
        await call('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false }, s);
        await call('Page.enable', {}, s);
        await call('Page.navigate', { url: 'http://localhost:8765/' }, s);
        return { targetId, s };
    };
    const states = s => ev(s, `[...document.querySelectorAll('img')].map(i => i.id + '=' + (i.getAttribute('anaglyphohol-state') || '-')).join(' ')`);
    const waitConverted = async (s, label, maxS = 90) => {
        for (let i = 0; i < maxS; i++) { const st = await states(s); if (/img-wide=anaglyph/.test(st) && !/=(queued|active)/.test(st)) { console.log(ts(), label, st); return st; } await sleep(1000); }
        const st = await states(s); console.log(ts(), label, 'TIMEOUT', st); return st;
    };
    const converterTarget = async () => {
        const { result: { targetInfos } } = await call('Target.getTargets');
        return targetInfos.find(t => t.url.includes('$=converter'));
    };
    const pg = await openPage();
    const st1 = await waitConverted(pg.s, 'page 1 converted');
    let conv = await converterTarget();
    check('page 1 converts through the shared converter', /img-wide=anaglyph/.test(st1) && !!conv, `converter target ${conv?.type} ${conv?.targetId?.slice(0, 6)}`);
    const ir = await ev(pg.s, `(() => { const r = document.getElementById('img-wide').getBoundingClientRect(); return { x: r.x, y: r.y, width: r.width, height: r.height }; })()`);
    const grab = async () => (await call('Page.captureScreenshot', { format: 'png', clip: { ...ir, scale: 1 } }, pg.s)).result.data;
    const before = await grab();
    // break it
    if (scenario === 'kill') {
        await call('Target.closeTarget', { targetId: conv.targetId });
        await sleep(1000);
        check('converter document gone', !(await converterTarget()));
    } else {
        const cs = await attach(conv.targetId);
        console.log(ts(), 'patch', await ev(cs, `(() => { GPUQueue.prototype.submit = function () { throw new Error('TEST: device lost'); }; return 'GPUQueue.submit now throws'; })()`));
    }
    // ask for a redraw: 3D level change (input only) -> every image redraws
    const root = `[...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-bar')).shadowRoot`;
    await ev(pg.s, `(() => { const s = ${root}.querySelector('.ao-sliders input'); s.value = '1'; s.dispatchEvent(new Event('input', { bubbles: true })); return 1; })()`);
    const tBreak = Date.now();
    let failedLog;
    for (let i = 0; i < 60 && !failedLog; i++) { failedLog = logs.find(l => /shared converter failed/.test(l)); if (!failedLog) await sleep(1000); }
    check('page notices the failure', !!failedLog, failedLog ? `${((Date.now() - tBreak) / 1000).toFixed(0)} s after the break: ${failedLog}` : 'no log in 60 s');
    // the images must come back in 3D, drawn locally, with the NEW level
    let after = before, st2 = '';
    for (let i = 0; i < 90; i++) {
        st2 = await states(pg.s);
        if (/img-wide=anaglyph/.test(st2) && !/=(queued|active)/.test(st2)) { after = await grab(); if (after !== before) break; }
        await sleep(1000);
    }
    fs.writeFileSync(path.join(outDir, `${scenario}_before.png`), Buffer.from(before, 'base64'));
    fs.writeFileSync(path.join(outDir, `${scenario}_after.png`), Buffer.from(after, 'base64'));
    check('page renders itself after the failure (all images 3D, new level drawn)', /img-wide=anaglyph/.test(st2) && !/failed/.test(st2) && after !== before, st2);
    if (scenario === 'broken') {
        let gone = false;
        for (let i = 0; i < 20 && !gone; i++) { gone = !(await converterTarget()); if (!gone) await sleep(500); }
        check('broken converter document is closed by the worker', gone);
    }
    // a NEW page gets a working shared converter again
    const pg2 = await openPage();
    const st3 = await waitConverted(pg2.s, 'page 2 converted');
    conv = await converterTarget();
    const p2Failed = logs.filter(l => /shared converter failed/.test(l)).length;
    check('a new page gets a fresh shared converter', /img-wide=anaglyph/.test(st3) && !!conv && p2Failed === 1, `converter ${conv?.targetId?.slice(0, 6)}, failure logs ${p2Failed}`);
} catch (e) {
    console.error('FAILED', e);
} finally {
    fs.writeFileSync(path.join(outDir, `${scenario}_results.txt`), results.join('\n') + '\n\n' + logs.join('\n'));
    try { ws?.close(); } catch { }
    try { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: 'ignore' }); } catch { }
    await sleep(1500);
    fs.rmSync(profile, { recursive: true, force: true });
}
