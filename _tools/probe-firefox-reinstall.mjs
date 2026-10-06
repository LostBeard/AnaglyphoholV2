// probe-firefox-reinstall.mjs - MEASURES what an add-on install / update does to an ALREADY OPEN page in Firefox:
// does the new version's content script run there, and what is left of the previous version's toolbar?
// Browser: launch-firefox.ps1 (BiDi 9225) + its test server.
// usage: node _tools/probe-firefox-reinstall.mjs <first build dir> [<update build dir>]   (e.g. a 3.x build, then 4.x)
import path from 'node:path';

const [firstDir, updateDir] = process.argv.slice(2);
const port = +(process.env.BIDI_PORT || 9225);
const sleep = ms => new Promise(r => setTimeout(r, ms));
const ws = new WebSocket(`ws://127.0.0.1:${port}/session`);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = () => rej(new Error(`no BiDi on ${port}`)); });
let next = 0; const pending = new Map();
ws.onmessage = e => { const m = JSON.parse(e.data); if (m.id != null && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } };
const call = (method, params = {}) => new Promise(r => { const id = ++next; pending.set(id, r); ws.send(JSON.stringify({ id, method, params })); });
try {
    await call('session.new', { capabilities: {} });
    const { result: { context } } = await call('browsingContext.create', { type: 'tab' });
    await call('browsingContext.navigate', { context, url: 'http://localhost:8765/?reinstall-probe', wait: 'complete' });
    const ev = async expr => (await call('script.evaluate', { expression: expr, target: { context }, awaitPromise: true })).result?.result?.value;
    const hosts = () => ev(`JSON.stringify([...document.documentElement.querySelectorAll('*')].filter(e => e.shadowRoot?.querySelector('.ao-bar')).map(h => h.shadowRoot.querySelector('.ao-bar').className))`);
    const overlays = () => ev(`document.querySelectorAll('.custom-media-overlay-canvas').length`);
    // the toggle of host i: click it in the page and see whether ITS bar changes (a dead instance does not answer)
    const answers = async i => {
        const before = await ev(`[...document.documentElement.querySelectorAll('*')].filter(e => e.shadowRoot?.querySelector('.ao-bar'))[${i}].shadowRoot.querySelector('.ao-bar').className`);
        await ev(`(() => { const t = [...document.documentElement.querySelectorAll('*')].filter(e => e.shadowRoot?.querySelector('.ao-bar'))[${i}].shadowRoot.querySelector('.ao-toggle button'); t.click(); return 1; })()`);
        await sleep(800);
        const after = await ev(`[...document.documentElement.querySelectorAll('*')].filter(e => e.shadowRoot?.querySelector('.ao-bar'))[${i}].shadowRoot.querySelector('.ao-bar').className`);
        return `${before} -> ${after}${before === after ? ' (DEAD)' : ' (live)'}`;
    };
    // 3.x leftovers (closed-shadow toolbar root with a fixed style; overlay divs holding one shadow canvas)
    const v3 = () => ev(`JSON.stringify({ toolbars: document.querySelectorAll('body > div[style="position: fixed; top: 0; left: 0; right: 0; height: 0; overflow: visible; display: flex; flex-direction: row; justify-content:center; z-index: 65536;"]').length, overlays: [...document.querySelectorAll('div[style^="position: absolute; pointer-events: none;"]')].filter(d => d.shadowRoot && d.shadowRoot.querySelector('canvas')).length })`);
    console.log('page open, no add-on yet: toolbars', await hosts(), 'overlays', await overlays(), '3.x', await v3());
    for (let n = 1; n <= 2; n++) {
        const dir = n === 2 && updateDir ? updateDir : firstDir;
        const r = await call('webExtension.install', { extensionData: { type: 'path', path: path.resolve(dir) } });
        console.log(`install #${n}:`, r.error ? `${r.error} ${r.message}` : r.result.extension);
        await sleep(8000);
        const h = JSON.parse(await hosts());
        console.log(`  after install #${n} ${path.basename(path.dirname(path.dirname(dir)))}/${path.basename(dir)} (page NOT reloaded): toolbars ${JSON.stringify(h)}, overlays ${await overlays()}, 3.x ${await v3()}`);
        for (let i = 0; i < h.length; i++) console.log(`  toolbar ${i}: ${await answers(i)}`);
    }
    await call('browsingContext.close', { context });
    await call('session.end', {});
} finally {
    ws.close();
}
