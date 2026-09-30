using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;

namespace Anaglyphohol.Background
{
    /// <summary>
    /// Releases the runtime events background.js held while .NET was starting - but only after EVERY background
    /// listener has attached. A service worker is torn down when idle and cold-started by the next event; that event is
    /// queued by background.js and replayed by <c>finalizeAsyncStartup()</c>, so anything not yet listening misses it
    /// ("Receiving end does not exist" on the sender). Register this LAST and await every service that listens.
    /// Same pattern as Gemineachy's StartupFinalizerBackgroundService.
    /// </summary>
    public class StartupFinalizerBackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        private Task? _ready;
        readonly SpawnJSRuntime _js;
        readonly BrowserExtensionService _bes;
        readonly BackgroundService _backgroundService;

        public StartupFinalizerBackgroundService(SpawnJSRuntime js, BrowserExtensionService bes, BackgroundService backgroundService)
        {
            _js = js;
            _bes = bes;
            _backgroundService = backgroundService;
        }

        async Task InitAsync()
        {
            if (_bes.ExtensionMode != ExtensionMode.Background) return;
            // Every service that registers a runtime listener must be awaited here.
            await _backgroundService.Ready;
            try
            {
                if (_js.Has("finalizeAsyncStartup")) _js.CallVoid("finalizeAsyncStartup");
            }
            catch (Exception ex)
            {
                _js.Log($"Anaglyphohol: finalizeAsyncStartup failed: {ex.Message}");
            }
        }
    }
}
