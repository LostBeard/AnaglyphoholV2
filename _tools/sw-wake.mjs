// sw-wake.mjs - capture a COLD WAKE of the Anaglyphohol service worker: stops the running worker, then wakes it with a
// runtime message from an open extension page (app/index.html) - the way a content script wakes it in real use, as
// opposed to sw-catch.mjs's extension reload (an install/update start). Logs the worker's console, exceptions and
// FAILED network requests (Network.loadingFailed with the URL), then whether .NET finished starting
// (background.js asyncStartupRunning / held events).
// Usage: node _tools/sw-wake.mjs [seconds=20] [message]   (debug Chrome on CDP_PORT, default 9224; an extension page
// tab must be open: /json/new?chrome-extension://<id>/app/index.html)
const port = process.env.CDP_PORT || 9224;
const seconds = Number(process.argv[2] || 20);
const message = process.argv[3] || 'wake';
const ver = await (await fetch(`http://localhost:${port}/json/version`)).json();
const ws = new WebSocket(ver.webSocketDebuggerUrl);
let id = 0;
const pending = new Map();
const send = (method, params = {}, sessionId) => new Promise((res) => {
    const msgId = ++id;
    pending.set(msgId, res);
    ws.send(JSON.stringify({ id: msgId, method, params, sessionId }));
});
const isOurSw = t => t.type === 'service_worker' && /ffhohkfijjpeecdmjdcbbflphkdmlmho/.test(t.url);
const t0 = Date.now();
const ms = () => `+${Date.now() - t0}`.padStart(6);
const requests = new Map();
let swSession = null;
ws.onmessage = async (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
    if (m.method === 'Target.attachedToTarget') {
        const { sessionId, targetInfo, waitingForDebugger } = m.params;
        if (isOurSw(targetInfo)) {
            swSession = sessionId;
            console.log(`${ms()} [sw] started ${targetInfo.url}`);
            await send('Runtime.enable', {}, sessionId);
            await send('Log.enable', {}, sessionId);
            await send('Network.enable', {}, sessionId);
        }
        if (waitingForDebugger) await send('Runtime.runIfWaitingForDebugger', {}, sessionId);
        return;
    }
    if (!m.sessionId || m.sessionId !== swSession) return;
    const p = m.params;
    if (m.method === 'Runtime.exceptionThrown') {
        const d = p.exceptionDetails;
        console.log(`${ms()} [sw EXCEPTION] ${(d.exception?.description || d.text).split('\n').slice(0, 3).join(' | ')}`);
    } else if (m.method === 'Runtime.consoleAPICalled') {
        console.log(`${ms()} [sw ${p.type}] ${p.args.map(a => a.value ?? a.description ?? '').join(' ').slice(0, 400)}`);
    } else if (m.method === 'Log.entryAdded') {
        console.log(`${ms()} [sw log ${p.entry.level}] ${p.entry.text} ${p.entry.url || ''}`);
    } else if (m.method === 'Network.requestWillBeSent') {
        requests.set(p.requestId, p.request.url);
    } else if (m.method === 'Network.loadingFailed') {
        console.log(`${ms()} [sw FETCH FAILED] ${requests.get(p.requestId) ?? p.requestId}: ${p.errorText}${p.blockedReason ? ' (' + p.blockedReason + ')' : ''}`);
    }
};
await new Promise((r) => ws.onopen = r);
const { targetInfos } = await send('Target.getTargets');
const running = targetInfos.find(isOurSw);
if (running) {
    await send('Target.closeTarget', { targetId: running.targetId });
    console.log(`${ms()} [sw] stopped the running worker`);
    await new Promise(r => setTimeout(r, 1500));
}
await send('Target.setDiscoverTargets', { discover: true });
await send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true });
const page = targetInfos.find(t => t.type === 'page' && /ffhohkfijjpeecdmjdcbbflphkdmlmho\/app\/index\.html/.test(t.url));
if (!page) { console.log('no extension page tab open'); process.exit(1); }
const { sessionId: pageSession } = await send('Target.attachToTarget', { targetId: page.targetId, flatten: true });
const r = await send('Runtime.evaluate', {
    expression: `new Promise(res => { const t = setTimeout(() => res('no reply in ${seconds} s'), ${seconds * 1000});
        chrome.runtime.sendMessage(${JSON.stringify(message)}).then(v => { clearTimeout(t); res('reply: ' + String(v).slice(0, 120)); },
            e => { clearTimeout(t); res('ERR ' + e.message); }); })`,
    awaitPromise: true, returnByValue: true }, pageSession);
console.log(`${ms()} [page] ${r?.result?.value ?? JSON.stringify(r)}`);
if (swSession) {
    const s = await send('Runtime.evaluate', { expression: 'JSON.stringify({ startupRunning: asyncStartupRunning, held: holding.length })', returnByValue: true }, swSession);
    console.log(`${ms()} [sw] ${s?.result?.value ?? JSON.stringify(s)}`);
}
ws.close();
process.exit(0);
