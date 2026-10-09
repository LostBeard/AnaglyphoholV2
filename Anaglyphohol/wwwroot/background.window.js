// Firefox background page loader (Chrome uses background.worker.js as a service worker).
(async function () {
    async function loadScript(src) {
        src = chrome.runtime.getURL(src);
        const script = document.createElement('script');
        const loadTask = new Promise((onload, onerror) => Object.assign(script, { onload, onerror, src }));
        (document.head || document.documentElement).append(script);
        await loadTask;
    }
    // background.js is NOT loaded here: it is listed BEFORE this file in the manifest's background scripts, so it runs
    // synchronously at startup. An event page only gets the events whose listeners exist after that first synchronous
    // run - loaded from here (async) it attached too late and runtime.onInstalled was lost (MEASURED 2026-10-04: the
    // install-time kernel shader warm-up never ran on Firefox).
    // .NET WASM app
    await loadScript('app/main.classic.js');
})();
