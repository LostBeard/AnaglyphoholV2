// Page-world pixel check of every VISIBLE Anaglyphohol overlay canvas against the element it covers.
// Reports: blank (no variance), how much it differs from the source, and the red-vs-cyan channel divergence an
// anaglyph must show (red from one eye, green/blue from the other). Same-origin sources only (a tainted source
// cannot be read back from the page).
(() => {
  const out = [];
  for (const c of document.querySelectorAll('canvas.custom-media-overlay-canvas')) {
    if (c.style.display === 'none' || !c.width || !c.height) continue;
    const src = c.previousElementSibling;
    const w = Math.min(c.width, 256), h = Math.max(1, Math.round(c.height * w / c.width));
    const a = new OffscreenCanvas(w, h).getContext('2d'); a.drawImage(c, 0, 0, w, h);
    const b = new OffscreenCanvas(w, h).getContext('2d');
    try { b.drawImage(src, 0, 0, w, h); } catch (e) { out.push({ id: src?.id, error: 'source unreadable: ' + e.message }); continue; }
    const A = a.getImageData(0, 0, w, h).data, B = b.getImageData(0, 0, w, h).data;
    let sum = 0, sum2 = 0, diff = 0, split = 0, n = w * h;
    for (let i = 0; i < A.length; i += 4) {
      const lum = (A[i] + A[i + 1] + A[i + 2]) / 3; sum += lum; sum2 += lum * lum;
      diff += Math.abs(A[i] - B[i]) + Math.abs(A[i + 1] - B[i + 1]) + Math.abs(A[i + 2] - B[i + 2]);
      split += Math.abs(A[i] - (A[i + 1] + A[i + 2]) / 2);
    }
    const mean = sum / n;
    out.push({ id: src?.id || src?.tagName, state: src?.getAttribute('anaglyphohol-state'), size: `${c.width}x${c.height}`,
      stddev: +Math.sqrt(sum2 / n - mean * mean).toFixed(1), meanAbsDiffVsSource: +(diff / (3 * n)).toFixed(1),
      meanRedVsCyan: +(split / n).toFixed(1) });
  }
  return JSON.stringify(out, null, 1);
})()
