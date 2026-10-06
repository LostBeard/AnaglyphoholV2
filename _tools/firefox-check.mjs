// firefox-check.mjs - the extension in REAL Firefox over WebDriver BiDi with real input (input.performActions):
// temporary add-on install, toolbar show / switches / drag, images + video in 3D, per-site state after reload.
// Start the browser first: powershell -ExecutionPolicy Bypass -File _tools\launch-firefox.ps1  (BiDi 9225, server 8765)
// usage: node _tools/firefox-check.mjs <firefox extension dir> <outPrefix>   (BIDI_PORT env overrides 9225)
import fs from 'node:fs';
import path from 'node:path';

const [extDir, outPrefix] = process.argv.slice(2);
const port = +(process.env.BIDI_PORT || 9225);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const results = [];
const check = (name, ok, detail) => { results.push(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' - ' + detail : ''}`); console.log(results.at(-1)); };

const ws = new WebSocket(`ws://127.0.0.1:${port}/session`);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error(`no BiDi on ${port} - run launch-firefox.ps1 first`)); });
let next = 0; const pending = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id != null && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const call = (method, params = {}) => new Promise(r => { const id = ++next; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
try {
    const s = await call('session.new', { capabilities: {} });
    const version = s.result?.capabilities?.browserVersion;
    const inst = await call('webExtension.install', { extensionData: { type: 'path', path: path.resolve(extDir) } });
    check(`add-on installs (Firefox ${version})`, !inst.error, inst.error ? `${inst.error} ${inst.message}` : inst.result.extension);
    await sleep(3000);
    const { result: { context } } = await call('browsingContext.create', { type: 'tab' });
    const ev = async expr => {
        const r = await call('script.evaluate', { expression: expr, target: { context }, awaitPromise: true });
        return r.result?.result?.value;
    };
    const root = `[...document.documentElement.querySelectorAll('*')].find(e => e.shadowRoot?.querySelector('.ao-bar'))?.shadowRoot`;
    const rectOf = sel => ev(`(() => { const r = ${root}?.querySelector(${JSON.stringify(sel)})?.getBoundingClientRect(); return r ? JSON.stringify({ x: r.x, y: r.y, w: r.width, h: r.height }) : null; })()`).then(v => v && JSON.parse(v));
    const barCls = () => ev(`${root}?.querySelector('.ao-bar')?.className || null`);
    const pointer = acts => call('input.performActions', { context, actions: [{ type: 'pointer', id: 'mouse', parameters: { pointerType: 'mouse' }, actions: acts }] }).then(() => call('input.releaseActions', { context }));
    const mv = (x, y) => ({ type: 'pointerMove', x: Math.round(x), y: Math.round(y), duration: 0 });
    const clickAt = async r => { await pointer([mv(r.x + r.w / 2, r.y + r.h / 2), { type: 'pointerDown', button: 0 }, { type: 'pointerUp', button: 0 }]); await sleep(700); };
    const states = () => ev(`[...document.querySelectorAll('img, video')].map(e => (e.id || e.tagName) + '=' + (e.getAttribute('anaglyphohol-state') || '-')).join(' ')`);
    const shot = async name => { const r = await call('browsingContext.captureScreenshot', { context }); if (r.result) fs.writeFileSync(`${outPrefix}_${name}.png`, Buffer.from(r.result.data, 'base64')); };

    await call('browsingContext.navigate', { context, url: 'http://localhost:8765/', wait: 'complete' });
    let cls; for (let i = 0; i < 40 && !cls; i++) { cls = await barCls(); if (!cls) await sleep(500); }
    // this profile is persistent (launch-firefox.ps1): the toolbar starts as this site was last left (per-site state)
    check('toolbar appears', /ao-(min|exp)/.test(cls || ''), cls);
    const startedMin = /ao-min/.test(cls);
    await clickAt(await rectOf('.ao-toggle'));
    check('a click toggles it', /ao-min/.test(await barCls() || '') !== startedMin, `${cls} -> ${await barCls()}`);
    if (/ao-min/.test(await barCls() || '')) await clickAt(await rectOf('.ao-toggle'));   // shown for the rest
    // images + videos on for this site (localhost is not a recommended site)
    // a switch that is OFF draws its icon at opacity 0.4 (DefaultOverlay IconStyles); the profile may already have them on
    const buttons = JSON.parse(await ev(`JSON.stringify([...${root}.querySelectorAll('.ao-row button')].map(b => { const r = b.getBoundingClientRect(); return { t: b.title, off: /opacity: ?0\.4/.test(b.innerHTML), x: r.x, y: r.y, w: r.width, h: r.height }; }))`));
    // Stats too: a video gets no anaglyphohol-state (only images do); with Stats on every rendered frame writes
    // anaglyphohol-cost "seq=N" on it, so a climbing seq proves 3D video frames
    for (const b of buttons.filter(b => /^(3D (images|videos) on|Performance stats)/.test(b.t) && b.off)) await clickAt(b);
    check('arrows in color once something is 3D', !/ao-off/.test(await barCls() || ''), await barCls());
    let st = '';
    for (let i = 0; i < 90; i++) { st = await states(); if (/img-wide=anaglyph/.test(st) && !/=(queued|active)/.test(st)) break; await sleep(1000); }
    check('images in 3D', /img-wide=anaglyph/.test(st) && !/failed/.test(st), st);
    const seq = () => ev(`+((document.getElementById('video-main').getAttribute('anaglyphohol-cost') || '').match(/seq=(\\d+)/) || [0, 0])[1]`);
    let s0 = 0; for (let i = 0; i < 60 && !s0; i++) { s0 = await seq(); if (!s0) await sleep(1000); }
    await sleep(3000);
    const s1 = await seq();
    check('video in 3D (rendered frames climbing)', s0 > 0 && s1 > s0, `seq ${s0} -> ${s1} in 3 s`);
    await shot('3d');
    // drag the whole bar 250 px left by its arrows (fast, 25 px steps), it stays shown
    const before = await rectOf('.ao-bar'), a = await rectOf('.ao-toggle');
    const acts = [mv(a.x + a.w / 2, a.y + a.h / 2), { type: 'pointerDown', button: 0 }];
    for (let k = 1; k <= 10; k++) acts.push(mv(a.x + a.w / 2 - 25 * k, a.y + a.h / 2));
    acts.push({ type: 'pointerUp', button: 0 });
    await pointer(acts); await sleep(700);
    const after = await rectOf('.ao-bar');
    check('drag moves the whole bar and keeps it shown', Math.abs((after.x - before.x) + 250) < 6 && /ao-exp/.test(await barCls() || ''), `moved ${(after.x - before.x).toFixed(0)} px, ${await barCls()}`);
    await shot('dragged');
    await call('browsingContext.reload', { context, wait: 'complete' });
    cls = null; for (let i = 0; i < 40 && !cls; i++) { cls = await barCls(); if (!cls) await sleep(500); }
    const r2 = await rectOf('.ao-bar');
    check('reload: still shown (per site), centered again', /ao-exp/.test(cls || '') && r2 && Math.abs(r2.x + r2.w / 2 - await ev('innerWidth / 2')) < 12, `${cls}, center ${(r2?.x + r2?.w / 2).toFixed(0)}`);
    st = ''; for (let i = 0; i < 60; i++) { st = await states(); if (/img-wide=anaglyph/.test(st)) break; await sleep(1000); }
    check('reload: images 3D again (site switches kept)', /img-wide=anaglyph/.test(st), st);
    await call('session.end', {});
} catch (e) {
    console.error('FAILED', e);
} finally {
    ws.close();
}
