// Firefox background page loader (Chrome uses background.worker.js as a service worker).
(async function () {
    async function loadScript(src) {
        src = chrome.runtime.getURL(src);
        const script = document.createElement('script');
        const loadTask = new Promise((onload, onerror) => Object.assign(script, { onload, onerror, src }));
        (document.head || document.documentElement).append(script);
        await loadTask;
    }
    // background.js holds runtime events that fire before .NET has booted (see the comments there).
    await loadScript('app/background.js');
    // .NET WASM app
    await loadScript('app/main.classic.js');
})();
