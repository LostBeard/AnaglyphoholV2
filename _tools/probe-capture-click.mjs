// probe-capture-click.mjs - MEASURES where 'click' lands when an ancestor takes pointer capture on pointerdown, in real
// Chrome (CDP Input.dispatchMouseEvent) and real Firefox (WebDriver BiDi input.performActions). Decides how the
// toolbar's drag can use pointer capture without double- or never-toggling its UIToggle button.
// Needs the test page server on :8765. usage: node _tools/probe-capture-click.mjs [chromePort=9230] [firefoxPort=9231]
import { spawn, execSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const chromePort = +(process.argv[2] || 9230), firefoxPort = +(process.argv[3] || 9231);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const busy = async port => { try { await fetch(`http://localhost:${port}/json/version`); return true; } catch { return false; } };
const url = mode => `http://localhost:8765/capture-probe.html?capture=${mode}`;
// the button's center on the page (body margin 40 + wrap padding 4 + half the 36 px button)
const cx = 40 + 4 + 18, cy = 40 + 4 + 18;
const results = [];

function rpc(ws) {
    let next = 0; const pending = new Map();
    ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id != null && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
    return (method, params = {}, extra = {}) => new Promise(r => { const id = ++next; pending.set(id, r); ws.send(JSON.stringify({ id, method, params, ...extra })); });
}

async function chrome() {
    if (await busy(chromePort)) throw new Error(`port ${chromePort} busy`);
    const profile = path.join(os.tmpdir(), `capture-probe-chrome-${chromePort}`);
    fs.rmSync(profile, { recursive: true, force: true });
    const child = spawn('C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe', ['--headless=new', `--remote-debugging-port=${chromePort}`,
        `--user-data-dir=${profile}`, '--no-first-run', 'about:blank'], { detached: true, stdio: 'ignore', windowsHide: true });
    try {
        let ver; for (let i = 0; i < 60 && !ver; i++) { try { ver = await (await fetch(`http://localhost:${chromePort}/json/version`)).json(); } catch { await sleep(250); } }
        const ws = new WebSocket(ver.webSocketDebuggerUrl); await new Promise(r => ws.onopen = r);
        const call = rpc(ws);
        const { result: { targetId } } = await call('Target.createTarget', { url: 'about:blank' });
        const { result: { sessionId } } = await call('Target.attachToTarget', { targetId, flatten: true });
        const c = (m, p) => call(m, p, { sessionId });
        await c('Page.enable');
        const ver2 = ver.Browser;
        for (const mode of ['wrap', 'none']) {
            for (const gesture of ['click', 'drag', 'keyboard']) {
                await c('Page.navigate', { url: url(mode) }); await sleep(800);
                const mouse = (type, x, y, buttons) => c('Input.dispatchMouseEvent', { type, x, y, button: 'left', buttons, clickCount: 1 });
                if (gesture === 'click') { await mouse('mouseMoved', cx, cy, 0); await mouse('mousePressed', cx, cy, 1); await mouse('mouseReleased', cx, cy, 0); }
                if (gesture === 'drag') { await mouse('mouseMoved', cx, cy, 0); await mouse('mousePressed', cx, cy, 1); for (let k = 1; k <= 8; k++) await mouse('mouseMoved', cx + 25 * k, cy, 1); await mouse('mouseReleased', cx + 200, cy, 0); }
                if (gesture === 'keyboard') {
                    await c('Runtime.evaluate', { expression: `document.getElementById('btn').focus()` });
                    await c('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, text: '\r' });
                    await c('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13 });
                }
                await sleep(300);
                const log = (await c('Runtime.evaluate', { expression: `window.__log.join(' | ')`, returnByValue: true })).result.result.value;
                results.push(`Chrome ${ver2} capture=${mode} ${gesture}: ${log}`);
            }
        }
        ws.close();
    } finally {
        try { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: 'ignore' }); } catch { }
        await sleep(1000); fs.rmSync(profile, { recursive: true, force: true });
    }
}

async function firefox() {
    if (await busy(firefoxPort)) throw new Error(`port ${firefoxPort} busy`);
    const profile = path.join(os.tmpdir(), `capture-probe-firefox-${firefoxPort}`);
    fs.rmSync(profile, { recursive: true, force: true }); fs.mkdirSync(profile);
    fs.writeFileSync(path.join(profile, 'user.js'), 'user_pref("browser.shell.checkDefaultBrowser", false);\nuser_pref("datareporting.policy.dataSubmissionEnabled", false);\n');
    const child = spawn('C:\\Program Files\\Mozilla Firefox\\firefox.exe', ['--headless', '--no-remote', '--profile', profile,
        `--remote-debugging-port=${firefoxPort}`, 'about:blank'], { detached: true, stdio: 'ignore', windowsHide: true });
    try {
        let ws;
        for (let i = 0; i < 80 && !ws; i++) {
            try { const w = new WebSocket(`ws://127.0.0.1:${firefoxPort}/session`); await new Promise((res, rej) => { w.onopen = res; w.onerror = rej; }); ws = w; } catch { await sleep(250); }
        }
        const call = rpc(ws);
        const s = await call('session.new', { capabilities: {} });
        const browserVersion = s.result?.capabilities?.browserVersion;
        const tree = await call('browsingContext.getTree', {});
        const context = tree.result.contexts[0].context;
        for (const mode of ['wrap', 'none']) {
            for (const gesture of ['click', 'drag', 'keyboard']) {
                await call('browsingContext.navigate', { context, url: url(mode), wait: 'complete' });
                await sleep(300);
                let actions;
                const move = (x, y) => ({ type: 'pointerMove', x: Math.round(x), y: Math.round(y), duration: 0 });
                if (gesture === 'click') actions = [{ type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: [move(cx, cy), { type: 'pointerDown', button: 0 }, { type: 'pointerUp', button: 0 }] }];
                if (gesture === 'drag') {
                    const a = [move(cx, cy), { type: 'pointerDown', button: 0 }];
                    for (let k = 1; k <= 8; k++) a.push(move(cx + 25 * k, cy));
                    a.push({ type: 'pointerUp', button: 0 });
                    actions = [{ type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: a }];
                }
                if (gesture === 'keyboard') {
                    await call('script.evaluate', { expression: `document.getElementById('btn').focus()`, target: { context }, awaitPromise: false });
                    actions = [{ type: 'key', id: 'kbd', actions: [{ type: 'keyDown', value: '\uE007' }, { type: 'keyUp', value: '\uE007' }] }];
                }
                const r = await call('input.performActions', { context, actions });
                if (r.error) results.push(`Firefox actions error: ${r.error} ${r.message}`);
                await call('input.releaseActions', { context });
                await sleep(300);
                const v = await call('script.evaluate', { expression: `window.__log.join(' | ')`, target: { context }, awaitPromise: false });
                results.push(`Firefox ${browserVersion} capture=${mode} ${gesture}: ${v.result?.result?.value}`);
            }
        }
        await call('session.end', {});
        ws.close();
    } finally {
        try { execSync(`taskkill /PID ${child.pid} /T /F`, { stdio: 'ignore' }); } catch { }
        await sleep(1500); fs.rmSync(profile, { recursive: true, force: true });
    }
}

try { await chrome(); } catch (e) { results.push('Chrome FAILED: ' + e.message); }
try { await firefox(); } catch (e) { results.push('Firefox FAILED: ' + e.message); }
console.log(results.join('\n'));
