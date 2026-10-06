// bench.mjs - Anaglyphohol release benchmark: any build vs any build, same machine, same pages, same probe.
// Measures what a user sees, from the PAGE side (bench-probe.js), never from either build's own counters:
//   - time to first 3D image: navigation start -> the overlay first holds pixels (cold = first page after the browser
//     starts, warm = the next page in the same browser, install = first page right after a fresh install)
//   - video: time to the first 3D frame, then over a 30 s window: DISTINCT video frames shown in 3D per second
//     (decoded from the frame-coded clip, so a redraw of the same frame never counts), source frames/s, 3D lag.
// Each build gets its own Chrome profile (installed once over CDP, Extensions.loadUnpacked) and its own port; builds
// are interleaved per rep so slow drift on the machine hits both. Chrome is the INSTALLED one, launched hidden, and
// closed gracefully by the PID this script started (never by image name).
//
//   node _tools/bench/bench.mjs --build v4=<ext dir> --build store=<ext dir> [--reps 3] [--install] [--only cold,video1080]
//   (bench-server.py must be running on 8443)
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const HERE = path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'));
const CACHE = path.resolve(HERE, '..', '.cache', 'bench');
const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const HOST = 'odysee.com';            // a recommended site in BOTH builds; mapped to the bench server
const BASE = `https://${HOST}`;
const PROBE = fs.readFileSync(path.join(HERE, 'bench-probe.js'), 'utf8');

const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : def; };
const builds = [];
args.forEach((a, i) => { if (a === '--build') { const [n, d] = args[i + 1].split('='); builds.push({ name: n, dir: path.resolve(d) }); } });
const reps = +opt('--reps', 3);
const doInstall = args.includes('--install');
const only = opt('--only', 'cold,warm,video1080,video720').split(',');
const VIDEO_SETTLE_S = +opt('--settle', 10), VIDEO_WINDOW_S = +opt('--window', 30);
const LIGHT = args.includes('--light');   // probe without per-frame readback (overhead check)
const INSTALL_SETTLE_MS = +opt('--install-settle', 45) * 1000;
builds.forEach((b, i) => { b.port = 9230 + i; b.profile = path.join(CACHE, `profile-${b.name}`); });
const outFile = path.join(CACHE, 'results', `bench-${new Date().toISOString().replace(/[:.]/g, '-')}.jsonl`);
fs.mkdirSync(path.dirname(outFile), { recursive: true });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);
const record = r => { fs.appendFileSync(outFile, JSON.stringify(r) + '\n'); log(JSON.stringify(r)); };

function startHidden(cmdLine) {
    // WMI create: outside this shell's job object, ShowWindow 0 (no window left on TJ's desktop)
    const ps = `$s = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }; ` +
        `$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = '${cmdLine.replace(/'/g, "''")}'; ProcessStartupInformation = $s }; ` +
        `if ($r.ReturnValue -ne 0) { throw "WMI $($r.ReturnValue)" }; $r.ProcessId`;
    const r = spawnSync('powershell', ['-NoProfile', '-Command', ps], { encoding: 'utf8' });
    const pid = parseInt(r.stdout.trim());
    if (!pid) throw new Error(`launch failed: ${r.stderr}`);
    return pid;
}
const alive = pid => spawnSync('powershell', ['-NoProfile', '-Command', `[bool](Get-Process -Id ${pid} -ErrorAction SilentlyContinue)`], { encoding: 'utf8' }).stdout.trim() === 'True';

class Cdp {
    static async connect(url) {
        const c = new Cdp(); c.ws = new WebSocket(url); c.next = 0; c.waiting = new Map(); c.events = [];
        c.ws.onmessage = e => {
            const m = JSON.parse(e.data);
            if (m.id && c.waiting.has(m.id)) { c.waiting.get(m.id)(m); c.waiting.delete(m.id); }
            else if (m.method) c.events.push(m);
        };
        await new Promise((res, rej) => { c.ws.onopen = res; c.ws.onerror = rej; });
        return c;
    }
    call(method, params = {}, sessionId) {
        const id = ++this.next;
        return new Promise((res, rej) => {
            this.waiting.set(id, m => m.error ? rej(new Error(`${method}: ${m.error.message}`)) : res(m.result));
            this.ws.send(JSON.stringify({ id, method, params, sessionId }));
        });
    }
}

async function launch(b) {
    const flags = [
        `--remote-debugging-port=${b.port}`, `--user-data-dir="${b.profile}"`,
        '--no-first-run', '--no-default-browser-check', '--window-size=1600,1000',
        `--host-resolver-rules="MAP ${HOST} 127.0.0.1:8443"`, '--ignore-certificate-errors',
        // a hidden window must not be throttled (see launch-chrome.ps1)
        '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding', '--disable-background-timer-throttling',
        'about:blank',
    ];
    const pid = startHidden(`"${CHROME}" ${flags.join(' ')}`);
    let ver;
    for (let i = 0; i < 80 && !ver; i++) { await sleep(250); try { ver = await (await fetch(`http://127.0.0.1:${b.port}/json/version`)).json(); } catch { } }
    if (!ver) throw new Error(`${b.name}: CDP ${b.port} never came up`);
    const cdp = await Cdp.connect(ver.webSocketDebuggerUrl);
    return { pid, cdp, version: ver.Browser };
}

async function close(b, s) {
    try { await Promise.race([s.cdp.call('Browser.close'), sleep(3000)]); } catch { }
    for (let i = 0; i < 60 && alive(s.pid); i++) await sleep(250);
    if (alive(s.pid)) { spawnSync('taskkill', ['/PID', String(s.pid), '/T', '/F']); log(`${b.name}: forced PID ${s.pid}`); }
}

async function pageSession(s) {
    const { targetInfos } = await s.cdp.call('Target.getTargets');
    let page = targetInfos.find(t => t.type === 'page' && t.url === 'about:blank') ?? targetInfos.find(t => t.type === 'page');
    // close every other tab (a get-started page from onInstalled etc.)
    for (const t of targetInfos) if (t.type === 'page' && t.targetId !== page.targetId) await s.cdp.call('Target.closeTarget', { targetId: t.targetId });
    const { sessionId } = await s.cdp.call('Target.attachToTarget', { targetId: page.targetId, flatten: true });
    await s.cdp.call('Page.enable', {}, sessionId);
    await s.cdp.call('Page.addScriptToEvaluateOnNewDocument', { source: (LIGHT ? "window.__benchMode = 'light'; " : '') + PROBE }, sessionId);
    return sessionId;
}
const evalPage = async (s, sid, expr) => (await s.cdp.call('Runtime.evaluate', { expression: expr, returnByValue: true }, sid)).result.value;

async function waitFor(s, sid, field, timeoutMs) {
    const end = Date.now() + timeoutMs;
    while (Date.now() < end) {
        const v = await evalPage(s, sid, `window.__bench && window.__bench.${field}`);
        if (v !== null && v !== undefined && v !== false) return v;
        await sleep(100);
    }
    return null;
}

async function imageRun(b, s, sid, scenario, rep) {
    await s.cdp.call('Page.navigate', { url: `${BASE}/image.html?src=photo-bbb.jpg` }, sid);
    const firstPixels = await waitFor(s, sid, 'firstPixels', 60000);
    const st = await evalPage(s, sid, `JSON.stringify({ firstVisible: __bench.firstVisible, kind: __bench.overlayKind, size: __bench.overlaySize, secure: __bench.secure, gpu: __bench.gpu, error: __bench.error })`);
    record({ build: b.name, rep, scenario, firstPixelsMs: firstPixels && Math.round(firstPixels), ...JSON.parse(st) });
}

function analyse(samples, t0, t1) {
    const w = samples.filter(x => x[0] >= t0 && x[0] < t1);
    let prev = null, newFrames = 0, valid = 0, srcPrev = null, srcNew = 0;
    const lags = [];
    const seqs = w.map(x => x[3]).filter(x => x >= 0);
    for (const [, idx, src] of w) {
        if (src >= 0 && src !== srcPrev) { if (srcPrev !== null) srcNew++; srcPrev = src; }
        if (idx < 0) continue;
        valid++;
        if (prev !== null && idx !== prev) newFrames++;
        prev = idx;
        if (src >= 0) lags.push(((src - idx) % 1800 + 1800) % 1800);
    }
    lags.sort((a, b) => a - b);
    const secs = (t1 - t0) / 1000;
    return {
        fps3d: +(newFrames / secs).toFixed(2), sourceFps: +(srcNew / secs).toFixed(2),
        lagFramesMedian: lags.length ? lags[lags.length >> 1] : null,
        noSlot: w.filter(x => x[1] === -2).length, pending: w.filter(x => x[1] === -3).length,
        samples: w.length, validPct: w.length ? +(100 * valid / w.length).toFixed(1) : 0,
        rafHz: +(w.length / secs).toFixed(1),
        // v4 only: its own rendered-frame counter over the same window
        probeMsPerFrame: w.length ? +(w.reduce((a, x) => a + (x[4] || 0), 0) / w.length).toFixed(2) : null,
        probeMsPerSec: +(w.reduce((a, x) => a + (x[4] || 0), 0) / secs).toFixed(1),
        ownFps: seqs.length > 1 ? +((seqs[seqs.length - 1] - seqs[0]) / secs).toFixed(2) : null,
        light: LIGHT || undefined,
    };
}

async function videoRun(b, s, sid, v, rep) {
    await s.cdp.call('Page.navigate', { url: `${BASE}/video.html?v=${v}` }, sid);
    const firstDecoded = await waitFor(s, sid, 'firstDecoded', 90000);
    const base = { build: b.name, rep, scenario: `video${v}` };
    if (!firstDecoded) {
        const st = await evalPage(s, sid, `JSON.stringify({ firstPixels: __bench.firstPixels, kind: __bench.overlayKind, size: __bench.overlaySize, n: __bench.samples.length, last: __bench.samples.slice(-5), error: __bench.error })`);
        record({ ...base, failed: 'no decodable 3D frame in 90 s', ...JSON.parse(st) });
        return;
    }
    const t0 = firstDecoded + VIDEO_SETTLE_S * 1000, t1 = t0 + VIDEO_WINDOW_S * 1000;
    // no CDP traffic during the window
    const now = await evalPage(s, sid, 'performance.now()');
    await sleep(Math.max(0, t1 - now + 500));
    const r = JSON.parse(await evalPage(s, sid, `JSON.stringify({ samples: __bench.samples, firstPixels: __bench.firstPixels, size: __bench.overlaySize, kind: __bench.overlayKind, error: __bench.error })`));
    record({ ...base, firstFrameMs: Math.round(firstDecoded), size: r.size, kind: r.kind, error: r.error, ...analyse(r.samples, t0, t1) });
}

async function extensionEnabled(s) {
    const { targetId } = await s.cdp.call('Target.createTarget', { url: 'chrome://extensions' });
    const { sessionId } = await s.cdp.call('Target.attachToTarget', { targetId, flatten: true });
    await sleep(800);
    const r = await s.cdp.call('Runtime.evaluate', { expression: `new Promise(r => chrome.developerPrivate.getExtensionsInfo(l => r(JSON.stringify(l.map(e => ({ name: e.name, version: e.version, state: e.state, reasons: e.disableReasons }))))))`, awaitPromise: true, returnByValue: true }, sessionId);
    await s.cdp.call('Target.closeTarget', { targetId });
    return r.result.value;
}

async function session(b, rep) {
    const s = await launch(b);
    try {
        // Extensions.loadUnpacked installs for THIS browser session only (MEASURED 2026-10-05, Chrome 151: after a
        // graceful restart the extension was not even listed), and branded Chrome ignores --load-extension. So every
        // session loads it, then waits out the install-time work (v4: the shader warm-up, ~10 s; store 3.0.14: opens
        // get-started only - its model document is created on first use) before the first page. What is left then is
        // what a browser start leaves: extension loaded, no model in memory, disk caches warm.
        await s.cdp.call('Extensions.loadUnpacked', { path: b.dir });
        await sleep(INSTALL_SETTLE_MS);
        // a run with the extension missing or disabled measures nothing: check, loudly
        const ext = await extensionEnabled(s);
        // exact state: a regex for ENABLED also matches "DISABLED" (it did: the first check passed a disabled extension)
        if (!JSON.parse(ext).some(e => e.name === 'Anaglyphohol' && e.state === 'ENABLED')) throw new Error(`${b.name}: extension not enabled after launch: ${ext}`);
        const sid = await pageSession(s);
        await sleep(3000);   // the browser is up and idle, as when a user opens a page a moment after starting it
        if (only.includes('cold')) await imageRun(b, s, sid, 'image-cold', rep);
        if (only.includes('warm')) { await sleep(2000); await imageRun(b, s, sid, 'image-warm', rep); }
        if (only.includes('video1080')) await videoRun(b, s, sid, 1080, rep);
        if (only.includes('video720')) await videoRun(b, s, sid, 720, rep);
    } finally { await close(b, s); }
}

async function install(b) {
    fs.rmSync(b.profile, { recursive: true, force: true });
    const s = await launch(b);
    try {
        const r = await s.cdp.call('Extensions.loadUnpacked', { path: b.dir });
        log(`${b.name}: installed ${b.dir} as ${r.id} (${s.version})`);
        b.extId = r.id;
        // Developer mode ON, or Chrome DISABLES the unpacked extension at the next browser start (MEASURED 2026-10-05:
        // every relaunch of a fresh profile ran with no extension at all - no overlay, no extension targets).
        const { targetId } = await s.cdp.call('Target.createTarget', { url: 'chrome://extensions' });
        const { sessionId } = await s.cdp.call('Target.attachToTarget', { targetId, flatten: true });
        await sleep(800);
        await s.cdp.call('Runtime.evaluate', { expression: `new Promise(r => chrome.developerPrivate.updateProfileConfiguration({ inDeveloperMode: true }, () => r(1)))`, awaitPromise: true }, sessionId);
        await s.cdp.call('Target.closeTarget', { targetId });
        await sleep(1500);
        const sid = await pageSession(s);
        await imageRun(b, s, sid, 'image-after-install', 0);
        await sleep(30000);   // let install-time work (v4: the shader warm-up) finish before the profile is reused
    } finally { await close(b, s); }
}

log(`builds: ${builds.map(b => `${b.name}=${b.dir} :${b.port}`).join(', ')}; reps ${reps}; out ${outFile}`);
async function shots(b) {
    // Same pages, same moment, exactly the media element's box: what each build SHOWS (screenshots for TJ's A/B).
    const dir = path.join(CACHE, 'shots');
    fs.mkdirSync(dir, { recursive: true });
    const s = await launch(b);
    try {
        await s.cdp.call('Extensions.loadUnpacked', { path: b.dir });
        await sleep(INSTALL_SETTLE_MS);
        const sid = await pageSession(s);
        for (const src of ['photo-bbb.jpg', 'photo-wide.jpg']) {
            await s.cdp.call('Page.navigate', { url: `${BASE}/image.html?src=${src}` }, sid);
            const fp = await waitFor(s, sid, 'firstPixels', 60000);
            await sleep(2500);   // any later refinement / resize redraw settles
            const r = JSON.parse(await evalPage(s, sid, `JSON.stringify(document.querySelector('img').getBoundingClientRect())`));
            const shot = await s.cdp.call('Page.captureScreenshot', { format: 'png', clip: { x: r.x, y: r.y, width: r.width, height: r.height, scale: 1 } }, sid);
            const file = path.join(dir, `${src.replace(/\..*/, '')}-${b.name}.png`);
            fs.writeFileSync(file, Buffer.from(shot.data, 'base64'));
            log(b.name, src, fp ? `3D at ${Math.round(fp)} ms` : 'NO 3D', file);
        }
    } finally { await close(b, s); }
}

if (args.includes('--shots')) {
    for (const b of builds) await shots(b);
    process.exit(0);
}
if (args.includes('--check')) {
    // launch each installed profile and print the extension state, nothing else
    for (const b of builds) { const s = await launch(b); try { log(b.name, await extensionEnabled(s)); } finally { await close(b, s); } }
    process.exit(0);
}
if (doInstall) for (const b of builds) await install(b);
for (let rep = 1; rep <= reps; rep++) {
    const order = rep % 2 ? builds : [...builds].reverse();   // alternate which build goes first
    for (const b of order) await session(b, rep);
}
log('done', outFile);
