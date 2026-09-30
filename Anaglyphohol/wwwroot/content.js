// content.js
(async function () {
    // Load .Net app
    await import(chrome.runtime.getURL('app/main.module.js'));
})();
