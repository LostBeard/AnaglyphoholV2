// Run against the extension's service worker target: has .NET released the held runtime events?
// background.js sets asyncStartupRunning=true and finalizeAsyncStartup() (called by StartupFinalizerBackgroundService
// after every listener attached) flips it to false.
(() => JSON.stringify({ asyncStartupRunning: typeof asyncStartupRunning === 'undefined' ? 'undefined' : asyncStartupRunning,
  held: typeof holding === 'undefined' ? null : holding.length }))()
