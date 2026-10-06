// ui-check.mjs - v4 toolbar + pages check in a throwaway Chrome (fresh profile = a real first install).
// Needs the test page server on :8765 (launch-chrome.ps1 starts it, or: python -m http.server 8765 --bind 127.0.0.1 --directory _tools/testpage).
// usage: node _tools/ui-check.mjs <extDir> <outPrefix> <port> [--headful]
// Steps: install -> watch the auto-opened get-started tab (warm-up progress) -> test page: minimized by default, click
// shows, drag moves, reload keeps "shown" (per site) and the moved position resets, slider value persists across reload.
import { spawn, execSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';

const [extDir, outPrefix, portStr] = process.argv.slice(2);
const headful = process.argv.includes('--headful');
const port = +portStr;
// a throwaway profile (a fresh profile = a real first install), deleted after the run
const profile = path.join(os.tmpdir(), `anaglyphohol-check-profile-${port}`);
// REFUSE a busy port: Chrome cannot bind it, so this script would drive WHOEVER is on it. MEASURED 2026-10-06: a
// SpawnScene harness Chrome on 9243 got this extension installed and test tabs opened next to a training run.
try { await fetch(`http://localhost:${port}/json/version`); console.error(`port ${port} is already in use - pick another`); process.exit(2); } catch { }
fs.rmSync(profile, { recursive: true, force: true });
const chrome = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const args = [`--remote-debugging-port=${port}`, `--user-data-dir=${profile}`, '--no-first-run', '--no-default-browser-check',
    '--window-size=1300,900', '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding',
    '--disable-background-timer-throttling', 'about:blank'];
if (!headful) args.unshift('--headless=new');
const child = spawn(chrome, args, { detached: true, stdio: 'ignore', windowsHide: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
let ws, next = 0;
const pending = new Map();
const call = (method, params = {}, sessionId) => new Promise(r => {
    const id = ++next; pending.set(id, r);
    ws.send(JSON.stringify({ id, method, params, sessionId }));
});
const results = [];
const check = (name, ok, detail) => { results.push(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' - ' + detail : ''}`); console.log(results.at(-1)); };
try {
    let ver;
    for (let i = 0; i < 60 && !ver; i++) { try { ver = await (await fetch(`http://localhost:${port}/json/version`)).json(); } catch { await sleep(250); } }
    ws = new WebSocket(ver.webSocketDebuggerUrl);
    await new Promise(r => ws.onopen = r);
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
    const lu = await call('Extensions.loadUnpacked', { path: path.resolve(extDir) });
    if (lu.error) throw new Error(lu.error.message);
    const extId = lu.result.id;
    const attach = async targetId => {
        const { result: { sessionId } } = await call('Target.attachToTarget', { targetId, flatten: true });
        await call('Emulation.setDeviceMetricsOverride', { width: 1280, height: 800, deviceScaleFactor: 1, mobile: false }, sessionId);
        await call('Emulation.setFocusEmulationEnabled', { enabled: true }, sessionId);
        await call('Page.enable', {}, sessionId);
        await call('Runtime.enable', {}, sessionId);
        return sessionId;
    };
    const shot = async (sessionId, name, clip) => {
        const r = await call('Page.captureScreenshot', { format: 'png', ...(clip ? { clip: { ...clip, scale: 2 } } : {}) }, sessionId);
        fs.writeFileSync(`${outPrefix}_${name}.png`, Buffer.from(r.result.data, 'base64'));
    };
    const evalIn = async (sessionId, expression) => (await call('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }, sessionId)).result?.result?.value;
    // 1. the get-started tab the install opened
    let gs;
    for (let i = 0; i < 40 && !gs; i++) {
        const { result: { targetInfos } } = await call('Target.getTargets');
        gs = targetInfos.find(t => t.type === 'page' && t.url.includes(extId) && t.url.includes('installed'));
        if (!gs) await sleep(250);
    }
    check('install opens get-started', !!gs, gs?.url);
    if (gs) {
        const s = await attach(gs.targetId);
        const t0 = Date.now();
        const seen = new Set();
        for (let i = 0; i < 120; i++) {
            const txt = await evalIn(s, `(document.querySelector('.gs-warmup')?.innerText || '').replace(/\\s+/g,' ')`);
            const key = (txt || '(none)').replace(/\d+ s\b/, 'N s');
            if (!seen.has(key)) { seen.add(key); console.log(`  +${((Date.now() - t0) / 1000).toFixed(1)}s ${txt || '(no warm-up panel)'}`); await shot(s, `gs_${seen.size}`); }
            if (/ready/i.test(txt || '') || /could not run/i.test(txt || '')) break;
            await sleep(1000);
        }
        check('warm-up panel reached a final state', [...seen].some(k => /ready|could not run/i.test(k)), [...seen].join(' | '));
    }
    // 2. test page
    const { result: { targetId } } = await call('Target.createTarget', { url: 'about:blank' });
    const p = await attach(targetId);
    await call('Page.navigate', { url: 'http://localhost:8765/' }, p);
    await sleep(8000);
    const barRect = `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-bar')); const b = h?.shadowRoot.querySelector('.ao-bar'); if (!b) return null; const r = b.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height, cls: b.className }; })()`;
    let r = await evalIn(p, barRect);
    check('toolbar starts minimized', r && r.cls.includes('ao-min'), JSON.stringify(r));
    await shot(p, 'page_min'); await shot(p, 'bar_min', { x: 340, y: 0, width: 600, height: 110 });
    const cx = r.x + r.w / 2, cy = r.y + r.h / 2;
    const mouse = async (type, x, y, buttons) => call('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons, clickCount: 1 }, p);
    await mouse('mouseMoved', cx, cy, 0); await mouse('mousePressed', cx, cy, 1); await mouse('mouseReleased', cx, cy, 0);
    await sleep(800);
    r = await evalIn(p, barRect);
    check('click shows toolbar', r && r.cls.includes('ao-exp'), JSON.stringify(r));
    await shot(p, 'page_exp'); await shot(p, 'bar_exp', { x: 200, y: 0, width: 880, height: 110 });
    // drag the arrows (first button in the bar) 250 px left
    const arrows = await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-toggle')); const r = h.shadowRoot.querySelector('.ao-toggle').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
    const before = r.x;
    await mouse('mouseMoved', arrows.x, arrows.y, 0); await mouse('mousePressed', arrows.x, arrows.y, 1);
    for (let k = 1; k <= 10; k++) { await mouse('mouseMoved', arrows.x - 25 * k, arrows.y, 1); await sleep(30); }
    await mouse('mouseReleased', arrows.x - 250, arrows.y, 0);
    await sleep(600);
    r = await evalIn(p, barRect);
    check('drag moves the whole bar and keeps it shown', r && Math.abs((r.x - before) + 250) < 6 && r.cls.includes('ao-exp'), `moved ${(r.x - before).toFixed(0)} px, ${r.cls}`);
    await shot(p, 'page_dragged');
    // @onmousedown:preventDefault on the arrows (RazorRenderer 2.2.1): a cancelable mousedown comes back defaultPrevented,
    // and no "__internal_preventDefault_onmousedown" attribute is left in the DOM
    const pd = await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-toggle')); const t = h.shadowRoot.querySelector('.ao-toggle'); const ev = new MouseEvent('mousedown', { bubbles: true, cancelable: true, button: 0 }); t.dispatchEvent(ev); t.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, button: 0 })); return JSON.stringify({ prevented: ev.defaultPrevented, junk: t.getAttributeNames().filter(a => a.startsWith('__internal')) }); })()`);
    check('arrows mousedown is preventDefault-ed, no junk attribute', /"prevented":true,"junk":\[\]/.test(pd || ''), pd);
    await sleep(500);
    // slider: set 3D Level via input events in the shadow root, then a change
    await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-sliders')); const s = h.shadowRoot.querySelector('.ao-sliders input'); s.value = '0.8'; s.dispatchEvent(new Event('input', {bubbles:true})); s.dispatchEvent(new Event('change', {bubbles:true})); return s.value; })()`);
    await sleep(1500);
    await call('Page.reload', {}, p);
    await sleep(8000);
    r = await evalIn(p, barRect);
    check('reload keeps toolbar shown (per site)', r && r.cls.includes('ao-exp'), r?.cls);
    check('reload re-centers the bar', r && Math.abs(r.x + r.w / 2 - 640) < 12, `center ${(r.x + r.w / 2).toFixed(0)}`);
    // a SLOW click (held 300 ms: released on the capture layer) hides, a quick one shows again
    {
        const a = await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-toggle')); const r = h.shadowRoot.querySelector('.ao-toggle').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
        await mouse('mouseMoved', a.x, a.y, 0); await mouse('mousePressed', a.x, a.y, 1); await sleep(300); await mouse('mouseReleased', a.x, a.y, 0);
        await sleep(600);
        r = await evalIn(p, barRect);
        check('slow click (layer) hides', r && r.cls.includes('ao-min'), r?.cls);
        const b = await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-toggle')); const r = h.shadowRoot.querySelector('.ao-toggle').getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
        await mouse('mouseMoved', b.x, b.y, 0); await mouse('mousePressed', b.x, b.y, 1); await mouse('mouseReleased', b.x, b.y, 0);
        await sleep(600);
        r = await evalIn(p, barRect);
        check('quick click shows again', r && r.cls.includes('ao-exp'), r?.cls);
        const layer = await evalIn(p, `!![...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-drag-layer'))`);
        check('no capture layer left behind', layer === false, String(layer));
    }
    const lvl = await evalIn(p, `(() => { const h = [...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-sliders')); return h.shadowRoot.querySelector('.ao-sliders input').value; })()`);
    check('3D Level persists across reload', lvl === '0.8', `value ${lvl}`);
    for (const pg of ['app/index.html', 'app/index.html?$=options', 'app/index.html?$=SystemInfo']) {
        await call('Page.navigate', { url: `chrome-extension://${extId}/${pg}` }, p);
        await sleep(4000);
        await shot(p, 'ext_' + pg.replace(/[^\w]+/g, '_'));
    }
} catch (e) {
    console.error('FAILED', e);
} finally {
    fs.writeFileSync(`${outPrefix}_results.txt`, results.join('\n'));
    try { ws?.close(); } catch { }
    try { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: 'ignore' }); } catch { }
    await sleep(1500);
    fs.rmSync(profile, { recursive: true, force: true });
}
