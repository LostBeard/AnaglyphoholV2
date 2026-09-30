// Page-world probe of the Anaglyphohol content script's visible effects (run on the test page / any page).
// Content-script JS objects are in an isolated world, so this reads only the DOM: the overlay's open shadow root,
// the stylesheets it added to the document, and the attributes/canvases it puts on tracked media.
(() => {
  const hosts = [...document.querySelectorAll('*')].filter(e => e.shadowRoot && e.shadowRoot.querySelector('.extension-content'));
  const host = hosts[0];
  const sr = host?.shadowRoot;
  const buttons = sr ? [...sr.querySelectorAll('button')].map(b => b.title || b.textContent.trim()) : [];
  const links = [...document.querySelectorAll('link[rel=stylesheet]')].map(l => l.href.split('/').slice(-2).join('/'));
  const states = {};
  for (const el of document.querySelectorAll('[anaglyphohol-state]')) {
    const s = el.getAttribute('anaglyphohol-state') || '(empty)';
    states[s] = (states[s] || 0) + 1;
  }
  const canvases = [...document.querySelectorAll('canvas.custom-media-overlay-canvas')].map(c => ({
    w: c.width, h: c.height, display: c.style.display, css: `${c.style.width}x${c.style.height}`
  }));
  return JSON.stringify({ overlayHosts: hosts.length, shadowStyles: sr ? [...sr.querySelectorAll('link')].map(l => l.href.split('/').pop()) : [],
    buttons, documentStylesheets: links, states, canvases }, null, 1);
})()
