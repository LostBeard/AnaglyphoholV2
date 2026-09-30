(() => {
  const mgr = document.querySelector('extensions-manager');
  const list = mgr.shadowRoot.querySelector('extensions-item-list');
  const items = [...list.shadowRoot.querySelectorAll('extensions-item')];
  const it = items.find(i => (i.shadowRoot.querySelector('#name')?.textContent || '').toLowerCase().includes('anaglyphohol'));
  if (!it) return 'not found';
  const sr = it.shadowRoot;
  const txt = (sel) => (sr.querySelector(sel)?.textContent || '').trim();
  const all = [...sr.querySelectorAll('*')].map(e => (e.childElementCount === 0 ? (e.textContent || '').trim() : '')).filter(Boolean);
  return JSON.stringify({ name: txt('#name'), enabled: sr.querySelector('#enableToggle')?.checked,
    errors: all.filter(s => /error|inactive|service worker/i.test(s)).slice(0, 10) }, null, 1);
})()
