// content.js
(async function () {
    // In an iframe (all_frames): .NET reads this to keep the toolbar minimized there, as the store version did - one
    // expanded toolbar per frame stacked toolbars over embedded players (rumble.com). Comparing the window proxies is
    // allowed even for a cross-origin parent.
    const inIframe = window.self !== window.top;
    globalThis.anaglyphoholInIframe = inIframe;
    // An iframe boots .NET only once it holds something to convert. Every boot is a whole .NET runtime: MEASURED
    // 2026-10-05 (Chrome 151, AOT build, iframes.html ?n=5 vs ?n=0, 3 interleaved runs) each media-free iframe cost
    // ~430 ms of main-thread CPU and ~14 MB of JS heap - and ad-heavy pages carry many such iframes. The tracker
    // (TrackedMedia) only handles img + video, and skips anything under 100x100 (TrackedMediaElement.MinWidth/MinHeight).
    if (inIframe) await waitForMedia();
    // Load .Net app
    await import(chrome.runtime.getURL('app/main.module.js'));

    function hasMedia() {
        if (document.querySelector('video')) return true;
        for (const img of document.images) {
            if (img.naturalWidth >= 100 && img.naturalHeight >= 100) return true;
        }
        return false;
    }

    function waitForMedia() {
        if (hasMedia()) return Promise.resolve();
        return new Promise(resolve => {
            let pending = false;
            const check = () => {
                pending = false;
                if (!hasMedia()) return;
                observer.disconnect();
                document.removeEventListener('load', onLoad, true);
                resolve();
            };
            const schedule = () => { if (!pending) { pending = true; setTimeout(check, 100); } };
            const onLoad = e => { if (e.target instanceof HTMLImageElement) schedule(); };
            // new elements (and a src swapped onto an existing img), plus images that finish loading later
            const observer = new MutationObserver(schedule);
            observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src', 'srcset'] });
            document.addEventListener('load', onLoad, true);
        });
    }
})();
