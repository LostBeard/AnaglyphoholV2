// bench-probe.js - injected into the PAGE world at document start (Page.addScriptToEvaluateOnNewDocument) by bench.mjs.
// Build-neutral: it never asks either extension anything. It finds the overlay canvas each build puts next to the
// page's <img>/<video> and reads its PIXELS on every animation frame:
//   - v4 (.NET): a sibling <canvas class="custom-media-overlay-canvas"> (WebGPU)
//   - store 3.0.14 (JS): a sibling <div> with an open shadow root holding a 2D <canvas>
// Results live in window.__bench (bench.mjs reads them over CDP). Times are performance.now() = ms since NAVIGATION start,
// always the time of the animation frame the pixels were TAKEN in (results arrive a frame or two later).
//   firstVisible  first frame the overlay was displayed (display != none, backing size > 0)
//   firstPixels   first frame the overlay held non-transparent pixels = the first 3D conversion on screen
//   for video: every frame, the frame number decoded from the overlay's code strip (make-coded-video.py layout),
//   so "new 3D frames per second" counts DISTINCT video frames shown in 3D, never a redraw of the same one.
//
// READBACK WITHOUT BLOCKING THE PAGE: the first version used drawImage + getImageData on a CPU (willReadFrequently)
// canvas and cost v4 14-18 ms of main thread PER FRAME (MEASURED 2026-10-05: the whole 1080p WebGPU canvas came back
// to the CPU, synchronously, every frame - ~65% of each second), on the same thread both extensions use. Now the probe
// has its own WebGPU device and copies only the 2 code rows (or 1 centre row) with copyExternalImageToTexture ->
// copyTextureToBuffer -> mapAsync: nothing on the main thread waits for the GPU. probeMs = the main-thread time of
// that encode + submit, recorded per sample so the report can state the probe's cost.
(() => {
    if (window.top !== window || window.__bench) return;
    const BITS = 11;
    const B = window.__bench = {
        secure: isSecureContext, gpu: !!navigator.gpu, device: false,
        firstVisible: null, firstPixels: null, firstDecoded: null,
        // per animation frame while a video's overlay is shown: [t, shownIndex, sourceIndex, seq, probeMs]
        //   shownIndex: >= 0 decoded, -1 unreadable, -2 no free readback slot, -3 result still pending
        samples: [], sourceFrames: 0, sourceIndex: -1, overlayKind: null, overlaySize: null, error: null,
        light: window.__benchMode === 'light',   // no per-frame video readback (first-pixels check only)
    };

    let dev = null, tex = null, texW = 0;
    (async () => {
        try {
            const ad = await navigator.gpu?.requestAdapter();
            dev = await ad?.requestDevice();
            B.device = !!dev;
        } catch (e) { B.error = 'probe device: ' + e; }
    })();
    const slots = [];
    function slot(bytes) {
        for (const s of slots) if (!s.busy && s.size >= bytes) return s;
        if (slots.length >= 32) return null;
        const s = { buf: dev.createBuffer({ size: bytes, usage: GPUBufferUsage.COPY_DST | GPUBufferUsage.MAP_READ }), size: bytes, busy: false };
        slots.push(s);
        return s;
    }
    // copy rows ys (backing px) of canvas c; done(data, bytesPerRow) runs when the GPU has them. false = no slot free,
    // null = the canvas has no rendering context yet.
    function readRows(c, ys, done) {
        const w = c.width, bpr = Math.ceil(w * 4 / 256) * 256;
        if (!tex || texW !== w) {
            tex?.destroy();
            tex = dev.createTexture({ size: [w, 2], format: 'rgba8unorm', usage: GPUTextureUsage.COPY_DST | GPUTextureUsage.COPY_SRC | GPUTextureUsage.RENDER_ATTACHMENT });
            texW = w;
        }
        const s = slot(bpr * ys.length);
        if (!s) return false;
        try {
            ys.forEach((y, i) => dev.queue.copyExternalImageToTexture({ source: c, origin: { x: 0, y: Math.round(y) } }, { texture: tex, origin: { x: 0, y: i } }, [w, 1]));
        } catch (e) {
            // v4 inserts its canvas (displayed) a moment before it gets its WebGPU context: nothing drawn yet
            if (/without rendering context/.test(String(e))) { B.noContextFrames = (B.noContextFrames || 0) + 1; return null; }
            throw e;
        }
        const enc = dev.createCommandEncoder();
        enc.copyTextureToBuffer({ texture: tex }, { buffer: s.buf, bytesPerRow: bpr }, [w, ys.length]);
        dev.queue.submit([enc.finish()]);
        s.busy = true;
        s.buf.mapAsync(GPUMapMode.READ).then(() => {
            try { done(new Uint8Array(s.buf.getMappedRange()), bpr); } catch (e) { B.error = 'probe read: ' + e; }
            s.buf.unmap(); s.busy = false;
        }, e => { s.busy = false; B.error = 'probe map: ' + e; });
        return true;
    }

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

    // Code strip: BITS blocks of width w/16 from column 2, rows of height h/18 centred at y = h/18 and 2h/18.
    // Each bit = mean of 5 px around the block centre; white >= 160, black <= 96, anything between = unreadable.
    function decodeRows(d, bpr, w) {
        let a = 0, b = 0;
        for (let r = 0; r < 2; r++) {
            for (let i = 0; i < BITS; i++) {
                const cx = Math.round(w / 16 * (2 + i) + w / 32);
                let lum = 0, alpha = 255;
                for (let k = -2; k <= 2; k++) {
                    const o = r * bpr + (cx + k) * 4;
                    lum += (d[o] + d[o + 1] + d[o + 2]) / 3;
                    alpha = Math.min(alpha, d[o + 3]);
                }
                lum /= 5;
                if (alpha < 200 || (lum > 96 && lum < 160)) return -1;
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

    let pixelCheckPending = false;
    function tick() {
        try {
            const t = performance.now();
            const el = document.querySelector('video, img');
            if (el && el.tagName === 'VIDEO') watchSource(el);
            const ov = el && overlayFor(el);
            if (ov && dev) {
                const c = ov.canvas;
                const visible = shown(c) && (!ov.host || shown(ov.host)) && c.width > 0 && c.height > 0;
                if (visible) {
                    B.overlayKind ??= ov.kind;
                    B.overlaySize = c.width + 'x' + c.height;
                    if (B.firstVisible === null) B.firstVisible = t;
                    if (B.firstPixels === null && !pixelCheckPending) {
                        pixelCheckPending = !!readRows(c, [c.height / 2], d => {
                            pixelCheckPending = false;
                            for (let i = 3; i < c.width * 4; i += 4) if (d[i] > 0) { if (B.firstPixels === null) B.firstPixels = t; break; }
                        });
                    }
                    if (el.tagName === 'VIDEO' && !B.light) {
                        const seq = +((el.getAttribute('anaglyphohol-cost') || '').match(/seq=(\d+)/)?.[1] ?? -1);
                        const sample = [Math.round(t * 10) / 10, -3, B.sourceIndex, seq, 0];
                        const p0 = performance.now();
                        const w = c.width, h = c.height;
                        const ok = readRows(c, [h / 18, 2 * h / 18], (d, bpr) => {
                            sample[1] = decodeRows(d, bpr, w);
                            if (sample[1] >= 0 && (B.firstDecoded === null || t < B.firstDecoded)) B.firstDecoded = t;
                        });
                        if (ok === false) sample[1] = -2;
                        else if (ok === null) sample[1] = -1;
                        sample[4] = Math.round((performance.now() - p0) * 100) / 100;
                        if (B.samples.length < 20000) B.samples.push(sample);
                    }
                }
            }
        } catch (e) { B.error = String(e); }
        requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
})();
