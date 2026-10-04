// Page-world probe of Dimenco 2D+Z output (run on a page with a tracked video in Dimenco mode).
// - Header: the fixed 512x1 canvas Anaglyphohol draws at the screen's top-left. Decodes the bits back to the 32 header
//   bytes (one bit per EVEN pixel, BBBA), checks both markers (241, 242/20) and both MSB-first CRC-32s
//   (poly 0x04C11DB7, init 0), and returns factor/offset so the caller can compare them with the 3D sliders.
// - Frame: the first video's overlay canvas. Right half must be grey (depth), left half must match the video squeezed
//   2:1 (mean abs diff, 0..255), with an UNsqueezed crop as the control (it must differ more).
(() => {
  const out = {};
  const hc = document.querySelector('canvas.anaglyphohol-dimenco-header');
  if (!hc) out.header = 'missing';
  else {
    const r = hc.getBoundingClientRect();
    const px = hc.getContext('2d').getImageData(0, 0, hc.width, hc.height).data;
    const bytes = [];
    let alphaOk = true, greyOk = true;
    for (let p = 0; p < hc.width; p++) {
      const i = p * 4;
      if (p % 2 === 1) { if (px[i + 3] !== 0) alphaOk = false; continue; }
      if (px[i + 3] !== 255) alphaOk = false;
      if (!(px[i] === px[i + 1] && px[i + 1] === px[i + 2] && (px[i] === 0 || px[i] === 255))) greyOk = false;
      const bit = px[i + 2] === 255 ? 1 : 0, k = p / 2;
      if (k % 8 === 0) bytes.push(0);
      bytes[bytes.length - 1] |= bit << (7 - (k % 8));
    }
    const table = [];
    for (let n = 0; n < 256; n++) { let c = (n << 24) >>> 0; for (let k = 0; k < 8; k++) c = (c & 0x80000000) ? (((c << 1) ^ 0x04C11DB7) >>> 0) : ((c << 1) >>> 0); table.push(c); }
    const crc = a => { let c = 0; for (const b of a) c = (((c << 8) >>> 0) ^ table[((c >>> 24) ^ b) & 255]) >>> 0; return c; };
    const be = (a, o) => ((a[o] << 24) | (a[o + 1] << 16) | (a[o + 2] << 8) | a[o + 3]) >>> 0;
    out.header = {
      rect: [r.left, r.top, r.width, r.height].join(','), position: getComputedStyle(hc).position, display: hc.style.display,
      parent: hc.parentElement && (hc.parentElement.tagName + (hc.parentElement.id ? '#' + hc.parentElement.id : '')),
      size: hc.width + 'x' + hc.height, bytes: bytes.length, alphaOk, greyOk,
      marker1: bytes[0], contentType: bytes[1], factor: bytes[2], offset: bytes[3], flags: bytes[4],
      crc1Ok: crc(bytes.slice(0, 6)) === be(bytes, 6),
      marker2: bytes[10] + '/' + bytes[11], crc2Ok: crc(bytes.slice(10, 28)) === be(bytes, 28),
    };
  }
  const v = document.querySelector('video');
  const c = v && v.nextElementSibling && v.nextElementSibling.tagName === 'CANVAS' ? v.nextElementSibling : null;
  if (!c) out.frame = 'no overlay canvas after the first video';
  else {
    const w = c.width, h = c.height, half = Math.floor(w / 2);
    const o = c.getContext('2d').getImageData(0, 0, w, h).data;
    let grey = 0, n = 0;
    for (let y = 0; y < h; y += 2) for (let x = half + 1; x < w; x += 2) {
      const i = (y * w + x) * 4; n++;
      if (Math.max(Math.abs(o[i] - o[i + 1]), Math.abs(o[i + 1] - o[i + 2])) <= 2) grey++;
    }
    const t = document.createElement('canvas'); t.width = w; t.height = h;
    const g = t.getContext('2d');
    g.drawImage(v, 0, 0, half, h);                 // squeezed into the left half: what 2D+Z should show
    const sq = g.getImageData(0, 0, half, h).data;
    g.drawImage(v, 0, 0, w, h);                    // control: the unsqueezed frame's left half
    const un = g.getImageData(0, 0, half, h).data;
    let dSq = 0, dUn = 0, m = 0;
    for (let y = 0; y < h; y += 2) for (let x = 0; x < half; x += 2) {
      const i = (y * w + x) * 4, j = (y * half + x) * 4; m++;
      for (let ch = 0; ch < 3; ch++) { dSq += Math.abs(o[i + ch] - sq[j + ch]); dUn += Math.abs(o[i + ch] - un[j + ch]); }
    }
    out.frame = { size: w + 'x' + h, paused: v.paused, rightHalfGrey: (grey / n).toFixed(4),
      leftVsSqueezed: (dSq / (m * 3)).toFixed(2), leftVsUnsqueezedControl: (dUn / (m * 3)).toFixed(2) };
  }
  // Fullscreen SCREEN mode (ThreeDKernels.TwoDZScreenKernel): the canvas covers the viewport and holds the whole screen
  // as 2D | depth. Expected layout recomputed here from the video's box + object-fit; bars must be exactly black in BOTH
  // halves, the left half must match the screen as composed here (black + video at its content rect) squeezed 2:1, and
  // the right half must be grey inside the video's columns.
  if (c && document.fullscreenElement) {
    const dpr = devicePixelRatio, W = c.width, H = c.height, half = Math.floor(W / 2);
    const cr = c.getBoundingClientRect(), vr = v.getBoundingClientRect();
    const fit = getComputedStyle(v).objectFit || 'contain';
    const fw = v.videoWidth, fh = v.videoHeight;
    let s = Math.min(vr.width / fw, vr.height / fh);
    if (fit === 'cover') s = Math.max(vr.width / fw, vr.height / fh);
    const cw = fit === 'fill' ? vr.width : fw * s, ch = fit === 'fill' ? vr.height : fh * s;
    const rx = (vr.left + (vr.width - cw) / 2) * dpr, ry = (vr.top + (vr.height - ch) / 2) * dpr, rw = cw * dpr, rh = ch * dpr;
    const o = c.getContext('2d').getImageData(0, 0, W, H).data;
    // a pixel is a bar when its 2-column footprint misses the content rect (the kernel's rule)
    const isBar = (x, y) => { const sx = x < half ? 2 * x + 1 : 2 * (x - half) + 1, sy = y + 0.5;
      return sx + 1 <= rx || sx - 1 >= rx + rw || sy + 0.5 <= ry || sy - 0.5 >= ry + rh; };
    let bars = 0, barsBlack = 0, depthIn = 0, depthGrey = 0;
    for (let y = 0; y < H; y += 3) for (let x = 0; x < W; x += 3) {
      const i = (y * W + x) * 4;
      if (isBar(x, y)) { bars++; if (o[i] === 0 && o[i + 1] === 0 && o[i + 2] === 0) barsBlack++; }
      else if (x >= half) { depthIn++; if (Math.max(Math.abs(o[i] - o[i + 1]), Math.abs(o[i + 1] - o[i + 2])) <= 2) depthGrey++; }
    }
    const scr = document.createElement('canvas'); scr.width = W; scr.height = H;
    const sg = scr.getContext('2d');
    sg.fillStyle = '#000'; sg.fillRect(0, 0, W, H); sg.drawImage(v, rx, ry, rw, rh);
    const sq = document.createElement('canvas'); sq.width = half; sq.height = H;
    const qg = sq.getContext('2d');
    qg.drawImage(scr, 0, 0, half, H);
    const exp = qg.getImageData(0, 0, half, H).data;
    qg.drawImage(v, 0, 0, half, H);            // control: the video stretched over the whole screen, squeezed
    const ctl = qg.getImageData(0, 0, half, H).data;
    let d = 0, dc = 0, m = 0;
    for (let y = 0; y < H; y += 3) for (let x = 0; x < half; x += 3) {
      const i = (y * W + x) * 4, j = (y * half + x) * 4; m++;
      for (let k = 0; k < 3; k++) { d += Math.abs(o[i + k] - exp[j + k]); dc += Math.abs(o[i + k] - ctl[j + k]); }
    }
    out.screen = { canvasRect: [cr.left, cr.top, cr.width, cr.height].map(Math.round).join(','), viewport: innerWidth + 'x' + innerHeight,
      backing: W + 'x' + H, contentRect: [rx, ry, rw, rh].map(Math.round).join(','), objectFit: fit,
      barsBlack: barsBlack + '/' + bars, depthGreyInside: (depthGrey / Math.max(1, depthIn)).toFixed(4),
      leftVsComposedScreen: (d / (m * 3)).toFixed(2), leftVsStretchedControl: (dc / (m * 3)).toFixed(2) };
  }
  return JSON.stringify(out, null, 1);
})()
