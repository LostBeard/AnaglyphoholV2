// bench-probe.js - injected into the PAGE world at document start (Page.addScriptToEvaluateOnNewDocument) by bench.mjs.
// Build-neutral: it never asks either extension anything. It finds the overlay canvas each build puts next to the
// page's <img>/<video> and reads its PIXELS on every animation frame:
//   - v4 (.NET): a sibling <canvas class="custom-media-overlay-canvas"> (WebGPU)
//   - store 3.0.14 (JS): a sibling <div> with an open shadow root holding a 2D <canvas>
// Results live in window.__bench (bench.mjs reads them over CDP). Times are performance.now() = ms since NAVIGATION start.
//   firstVisible  first frame the overlay was displayed (display != none, backing size > 0)
//   firstPixels   first frame the overlay held non-transparent pixels = the first 3D conversion on screen
//   for video: every frame, the frame number decoded from the overlay's code strip (make-coded-video.py layout),
//   so "new 3D frames per second" counts DISTINCT video frames shown in 3D, never a redraw of the same one.
(() => {
    if (window.top !== window || window.__bench) return;
    const BITS = 11;
    const B = window.__bench = {
        secure: isSecureContext, gpu: !!navigator.gpu,
        firstVisible: null, firstPixels: null, firstDecoded: null,
        // per animation frame while a video plays: [t, shownIndex (-1 = no valid code), sourceIndex]
        samples: [], sourceFrames: 0, sourceIndex: -1, overlayKind: null, overlaySize: null, error: null,
        // LIGHT mode (window.__benchMode = 'light', set before this script): no per-frame pixel readback - only the
        // first-pixels check. Used to measure what the readback costs each build (v4 also reports its own rendered
        // frame count in the video's anaglyphohol-cost attribute, seq=N, recorded as the 4th sample field).
        light: window.__benchMode === 'light',
    };
    const scratch = document.createElement('canvas');
    scratch.width = 320; scratch.height = 48;
    const sctx = scratch.getContext('2d', { willReadFrequently: true });

    function overlayFor(el) {
        for (const s of [el.nextElementSibling, el.previousElementSibling]) {
            if (!s) continue;
            if (s.tagName === 'CANVAS') return { canvas: s, host: null, kind: 'sibling-canvas' };
            const c = s.shadowRoot?.querySelector('canvas');
            if (c) return { canvas: c, host: s, kind: 'shadow-canvas' };
        }
        return null;
    }
    const shown = e => !!e && getComputedStyle(e).display !== 'none';

    function hasPixels(c) {
        sctx.clearRect(0, 0, 32, 32);
        sctx.drawImage(c, 0, 0, c.width, c.height, 0, 0, 32, 32);
        const d = sctx.getImageData(0, 0, 32, 32).data;
        for (let i = 3; i < d.length; i += 4) if (d[i] > 0) return true;
        return false;
    }
    // Code strip: rows of BITS blocks, block width w/16 starting at column 2, row height h/18, row r centred at
    // (r + 1) * h/18. Source region y 0..3*h/18 is drawn into 320x48, so the rows land at y 16 and 32, block i at x 20*(2+i)+10.
    function decode(c) {
        const w = c.width, h = c.height;
        sctx.clearRect(0, 0, 320, 48);
        sctx.drawImage(c, 0, 0, w, 3 * h / 18, 0, 0, 320, 48);
        const d = sctx.getImageData(0, 0, 320, 48).data;
        let a = 0, b = 0;
        for (let r = 0; r < 2; r++) {
            for (let i = 0; i < BITS; i++) {
                const o = ((16 + 16 * r) * 320 + 20 * (2 + i) + 10) * 4;
                const lum = (d[o] + d[o + 1] + d[o + 2]) / 3;
                if (d[o + 3] < 200 || (lum > 96 && lum < 160)) return -1;   // unreadable: no image yet or mid-tone
                const bit = lum >= 160 ? 1 : 0;
                if (r === 0) a = (a << 1) | bit; else b = (b << 1) | bit;
            }
        }
        return (a ^ b) === (1 << BITS) - 1 ? a : -1;   // row 2 must be row 1 inverted
    }

    let rvfcVideo = null;
    function watchSource(v) {
        if (rvfcVideo === v || !v.requestVideoFrameCallback) return;
        rvfcVideo = v;
        const cb = (now, md) => {
            B.sourceFrames++;
            B.sourceIndex = Math.round(md.mediaTime * 30);
            v.requestVideoFrameCallback(cb);
        };
        v.requestVideoFrameCallback(cb);
    }

    function tick() {
        try {
            const t = performance.now();
            const el = document.querySelector('video, img');
            const ov = el && overlayFor(el);
            if (ov) {
                const visible = shown(ov.canvas) && (!ov.host || shown(ov.host)) && ov.canvas.width > 0 && ov.canvas.height > 0;
                if (visible) {
                    B.overlayKind ??= ov.kind;
                    B.overlaySize = ov.canvas.width + 'x' + ov.canvas.height;
                    if (B.firstVisible === null) B.firstVisible = t;
                    if (B.firstPixels === null && hasPixels(ov.canvas)) B.firstPixels = t;
                    if (el.tagName === 'VIDEO') {
                        watchSource(el);
                        const d0 = performance.now();
                        const idx = B.light ? -1 : decode(ov.canvas);
                        const probeMs = performance.now() - d0;   // the probe's own main-thread cost this frame (readback included)
                        if (B.firstDecoded === null && (B.light ? B.firstPixels !== null : idx >= 0)) B.firstDecoded = t;
                        const seq = +((el.getAttribute('anaglyphohol-cost') || '').match(/seq=(\d+)/)?.[1] ?? -1);
                        if (B.samples.length < 20000) B.samples.push([Math.round(t * 10) / 10, idx, B.sourceIndex, seq, Math.round(probeMs * 100) / 100]);
                    }
                }
            }
            if (el && el.tagName === 'VIDEO') watchSource(el);
        } catch (e) { B.error = String(e); }
        requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
})();
