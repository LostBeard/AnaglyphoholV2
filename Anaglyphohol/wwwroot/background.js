// Event holder: runs BEFORE .NET in the Chrome extension service worker (background.worker.js) and the Firefox
// background page (background.window.js).
//
// !! IMPORTANT !!: a service worker is only woken for events whose listeners were attached during the initial
// SYNCHRONOUS load. https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Background_scripts#move_event_listeners
// .NET (WASM) starts asynchronously, so events that arrive while it boots are HELD here and re-dispatched once
// .NET calls finalizeAsyncStartup() - after every background listener has attached
// (StartupFinalizerBackgroundService).
var holding = [];
var asyncStartupRunning = true;
// an ARRAY, not an object keyed by target: every event target stringifies to "[object Object]", so an object
// map attached only the first event and silently dropped the rest (the pre-SpawnJS background.common.js bug).
var attached = [];
function attachToEvent(target, tempCb) {
    if (!target) return;
    if (attached.some(function (a) { return a.target === target; })) return;
    var att = {
        target: target,
        cb: function (...args) {
            if (!asyncStartupRunning) return;
            holding.push({ target: target, args: args });
            return !tempCb ? void 0 : tempCb(...args);
        }
    };
    attached.push(att);
    target.addListener(att.cb);
    // Record the listeners .NET adds while it starts, so a held event can be replayed to them in EVERY browser: Chrome's
    // event objects have an (undocumented) dispatch(), Firefox's do not (MEASURED 2026-10-05: typeof
    // chrome.runtime.onInstalled.dispatch is "undefined" there) - the replay threw and every held event
    // (runtime.onInstalled, messages that woke the page) was lost; the get-started page never opened on install.
    att.listeners = [];
    try {
        const add = target.addListener;
        target.addListener = function (fn, ...rest) {
            if (asyncStartupRunning && fn !== att.cb) att.listeners.push(fn);
            return add.call(target, fn, ...rest);
        };
    } catch (ex) {
        console.error('Anaglyphohol: could not watch listeners for held events', ex);
    }
}
// Called by .NET once all IBackgroundService / IAsyncBackgroundService services (and their listeners) are ready.
function finalizeAsyncStartup() {
    if (!asyncStartupRunning) return;
    asyncStartupRunning = false;
    var held = holding;
    holding = [];
    for (const att of attached) {
        att.target.removeListener(att.cb);
    }
    for (var e of held) {
        try {
            if (typeof e.target.dispatch === 'function') {
                e.target.dispatch(...e.args);
            } else {
                const att = attached.find(function (a) { return a.target === e.target; });
                for (const fn of (att && att.listeners) || []) fn(...e.args);
            }
        } catch (ex) {
            console.error(ex);
        }
    }
}

// For .NET's startup log (StartupFinalizerBackgroundService): how many events were held.
globalThis.heldRuntimeEventCount = function () { return holding.length; };

attachToEvent(chrome.runtime.onInstalled);
attachToEvent(chrome.runtime.onStartup);
attachToEvent(chrome.runtime.onSuspend);
// returning true keeps the sendResponse channel open until .NET answers the re-dispatched message
attachToEvent(chrome.runtime.onMessageExternal, (data, sender, response) => response != null);
attachToEvent(chrome.runtime.onMessage, (data, sender, response) => response != null);
