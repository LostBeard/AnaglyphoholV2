// This script could be running in a Firefox extension background page or a Chrome extension ServiceWorker

// !! IMPORTANT !!: The service worker will not be woken up for events if the events are not attached to at the top level like below.
// https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Background_scripts#move_event_listeners
// Because Blazor is WebAssembly, it starts asynchronously and therefore cannot attach to events during the initial synchronous load.
// Service workers and other contexts expect the code to be ready to handle events after the initial synchronous load.
// To try and adapt this old fashioned synchronous startup to work with Blazor we have to hold events until Blazor is ready to handle them.
// !! IMPORTANT !!: The way this method works simplifies some things but can complicate others.
// Important part: Event data is held in a queue and eventually re-dispatched once Blazor signals it is ready.
// This can cause issues if you have other listeners to these events outside of Blazor.
var holding = [];
var asyncStartupRunning = true;
var attached = {};
// attaches temporary event handlers
function attachToEvent(target, tempCb) {
    if (!target) return;
    if (attached[target]) return;
    var att = {
        target: target,
        cb: function () {
            var args = [...arguments];
            var held = {
                target: target,
                args: args,
            };
            holding.push(held);
            return !tempCb ? void 0 : tempCb(...args);
        }
    };
    attached[target] = att;
    target.addListener(att.cb);
}
// Blazor will call this method after it has finished starting and initializing all service that implement IBackgroundService and IAsyncBackgroundService
function finalizeAsyncStartup() {
    if (!asyncStartupRunning) return;
    asyncStartupRunning = false;
    var ret = holding;
    holding = [];
    // detach temporary event handlers
    var keys = Object.keys(attached);
    for (var key of keys) {
        var att = attached[key];
        var target = att.target;
        target.removeListener(att.cb);
    }
    // re-dispatch events
    for (var e of ret) {
        try {
            e.target.dispatch(...e.args);
        } catch (e) {
            console.error(e);
        }
    }
}

// attach temporary event handlers
attachToEvent(chrome.runtime.onInstalled);
attachToEvent(chrome.runtime.onStartup);
attachToEvent(chrome.runtime.onSuspend);
attachToEvent(chrome.runtime.onMessageExternal, (data, sender, response) => response != null);
attachToEvent(chrome.runtime.onMessage, (data, sender, response) => response != null);

