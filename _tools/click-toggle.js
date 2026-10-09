// Clicks an overlay toolbar button by its title (window.__toggle, set by a preceding cdp.cs call) and returns
// every button's title + current look, so the caller sees the state it produced.
(() => {
  const host = [...document.querySelectorAll('*')].find(e => e.shadowRoot && e.shadowRoot.querySelector('.extension-content'));
  if (!host) return 'no overlay';
  const buttons = [...host.shadowRoot.querySelectorAll('button')];
  const b = buttons.find(x => x.title === window.__toggle);
  if (!b) return 'no button titled ' + window.__toggle;
  b.click();
  return 'clicked ' + window.__toggle;
})()
