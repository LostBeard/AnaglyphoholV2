// Run against chrome://extensions: the Anaglyphohol extension's id plus its manifest / runtime errors.
(async () => {
  const all = await chrome.developerPrivate.getExtensionsInfo();
  const ext = all.find(e => (e.name || '').toLowerCase().includes('anaglyphohol'));
  if (!ext) return 'anaglyphohol not installed';
  const pick = e => ({ msg: (e.message || '').slice(0, 400), src: e.source || e.contextUrl || '', line: e.lineNumber, level: e.severity });
  return JSON.stringify({ id: ext.id, path: ext.path, manifestErrors: (ext.manifestErrors || []).map(pick), runtimeErrors: (ext.runtimeErrors || []).slice(-12).map(pick) }, null, 1);
})()
