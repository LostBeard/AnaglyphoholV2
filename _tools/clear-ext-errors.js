// Clears every recorded error (manifest + runtime) for the Anaglyphohol extension. Run on chrome://extensions.
(async () => {
    const exts = await new Promise(r => chrome.developerPrivate.getExtensionsInfo({ includeDisabled: true }, r));
    const ext = exts.find(e => /anaglyphohol/i.test(e.name));
    if (!ext) return 'no anaglyphohol extension';
    const ids = [...(ext.manifestErrors || []), ...(ext.runtimeErrors || [])].map(e => e.id);
    await new Promise(r => chrome.developerPrivate.deleteExtensionErrors({ extensionId: ext.id, errorIds: ids }, r));
    return `cleared ${ids.length} error(s) for ${ext.id}`;
})()
