// sw-catch.mjs - capture the Anaglyphohol SERVICE WORKER's startup exceptions/console, including a worker that dies
// during script evaluation ("Service worker registration failed. Status code: N" with no runtime error recorded).
// Browser-level CDP: auto-attach to every new target paused (waitForDebuggerOnStart), enable Runtime on service
// workers, then let them run. Triggers an extension reload itself via the chrome://extensions page.
// Usage: node _tools/sw-catch.mjs [seconds=12]   (debug Chrome on CDP_PORT, default 9224)
const port = process.env.CDP_PORT || 9224;
const seconds = Number(process.argv[2] || 12);
const ver = await (await fetch(`http://localhost:${port}/json/version`)).json();
const ws = new WebSocket(ver.webSocketDebuggerUrl);
let id = 0;
const pending = new Map();
const send = (method, params = {}, sessionId) => new Promise((res) => {
    const msgId = ++id;
    pending.set(msgId, res);
    ws.send(JSON.stringify({ id: msgId, method, params, sessionId }));
});
const swSessions = new Set();
ws.onmessage = async (ev) => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result ?? m.error); pending.delete(m.id); return; }
    if (m.method === 'Target.attachedToTarget') {
        const { sessionId, targetInfo, waitingForDebugger } = m.params;
        const isSw = targetInfo.type === 'service_worker' && /ffhohkfijjpeecdmjdcbbflphkdmlmho|anaglyph/i.test(targetInfo.url);
        if (isSw) {
            swSessions.add(sessionId);
            console.log(`[sw] attached ${targetInfo.url}`);
            await send('Runtime.enable', {}, sessionId);
            await send('Log.enable', {}, sessionId);
        }
        if (waitingForDebugger) await send('Runtime.runIfWaitingForDebugger', {}, sessionId);
        return;
    }
    if (!m.sessionId || !swSessions.has(m.sessionId)) return;
    if (m.method === 'Runtime.exceptionThrown') {
        const d = m.params.exceptionDetails;
        console.log(`[sw EXCEPTION] ${d.exception?.description || d.text} @ ${d.url || ''}:${d.lineNumber}:${d.columnNumber}`);
    } else if (m.method === 'Runtime.consoleAPICalled') {
        console.log(`[sw ${m.params.type}] ${m.params.args.map(a => a.value ?? a.description ?? '').join(' ')}`);
    } else if (m.method === 'Log.entryAdded') {
        const e = m.params.entry;
        console.log(`[sw log ${e.level}] ${e.text} ${e.url || ''}:${e.lineNumber ?? ''}`);
    } else if (m.method === 'Target.detachedFromTarget' || m.method === 'Inspector.detached') {
        console.log(`[sw] detached`);
    }
};
await new Promise((r) => ws.onopen = r);
await send('Target.setDiscoverTargets', { discover: true });
await send('Target.setAutoAttach', { autoAttach: true, waitForDebuggerOnStart: true, flatten: true });
// Reload the extension from the chrome://extensions page.
const targets = await (await fetch(`http://localhost:${port}/json/list`)).json();
const ext = targets.find(t => t.url.startsWith('chrome://extensions'));
if (!ext) { console.log('no chrome://extensions tab open'); process.exit(1); }
const { sessionId: extSession } = await send('Target.attachToTarget', { targetId: ext.id, flatten: true });
const r = await send('Runtime.evaluate', {
    expression: `new Promise(res => chrome.developerPrivate.getExtensionsInfo({includeDisabled:true}, list => {
        const e = list.find(x => /anaglyphohol/i.test(x.name));
        chrome.developerPrivate.reload(e.id, {failQuietly:true}, () => res('reloaded ' + e.id)); }))`,
    awaitPromise: true, returnByValue: true }, extSession);
console.log(`[ext] ${r?.result?.value ?? JSON.stringify(r)}`);
await new Promise((res) => setTimeout(res, seconds * 1000));
ws.close();
process.exit(0);
