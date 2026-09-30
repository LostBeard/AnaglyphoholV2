(() => {
  if (window.__conHooked) return 'already';
  window.__conLog = [];
  for (const k of ['log', 'warn', 'error']) {
    const o = console[k].bind(console);
    console[k] = (...a) => { try { window.__conLog.push(k + ': ' + a.map(x => { try { return typeof x === 'string' ? x : JSON.stringify(x); } catch { return String(x); } }).join(' ').slice(0, 300)); } catch {} o(...a); };
  }
  window.addEventListener('error', e => window.__conLog.push('onerror: ' + (e.message || '') + ' @' + (e.filename || '')));
  window.addEventListener('unhandledrejection', e => window.__conLog.push('unhandledrejection: ' + String(e.reason).slice(0, 300)));
  window.__conHooked = true;
  return 'hooked';
})()
