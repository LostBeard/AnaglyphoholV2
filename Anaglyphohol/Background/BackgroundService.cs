using Action = System.Action;
using Anaglyphohol.Services;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Background
{
    /// <summary>
    /// Extension background (Chrome service worker / Firefox background page) message handling: runtime.onMessage is
    /// acknowledged (content scripts use it to wake the worker). Anaglyphohol is free on every site, so there is no
    /// account token and no external (spawndev.com) channel.
    /// </summary>
    public class BackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _Ready ??= InitAsync();
        private Task? _Ready;
        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BrowserExtensionService;
        Runtime? _runtime;

        public BackgroundService(SpawnJSRuntime js, BrowserExtensionService browserExtensionService)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
        }


        async Task InitAsync()
        {
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.Background) return;
            _runtime = BrowserExtensionService.Runtime;
            if (_runtime == null) return;
            _runtime.OnMessage += Runtime_OnMessage;
        }

        bool Runtime_OnMessage(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            data.Dispose();
            sender.Dispose();
            if (sendResponse != null)
            {
                try { sendResponse.CallVoid(); } catch { }
                sendResponse.Dispose();
            }
            return false;
        }
    }
}
