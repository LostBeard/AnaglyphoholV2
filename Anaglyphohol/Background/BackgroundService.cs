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
        readonly ShaderWarmupService ShaderWarmup;
        Runtime? _runtime;

        public BackgroundService(SpawnJSRuntime js, BrowserExtensionService browserExtensionService, ShaderWarmupService shaderWarmup)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
            ShaderWarmup = shaderWarmup;
        }


        async Task InitAsync()
        {
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.Background) return;
            _runtime = BrowserExtensionService.Runtime;
            if (_runtime == null) return;
            _runtime.OnMessage += Runtime_OnMessage;
            _runtime.OnInstalled += Runtime_OnInstalled;
        }

        // Install, update, or a browser update (its WebGPU may differ): prepare the kernel shaders pages will need.
        void Runtime_OnInstalled(OnInstalledDetails details)
        {
            var reason = details.Reason;
            if (reason?.Enum == OnInstalledReason.SharedModuleUpdate) return;
            _ = ShaderWarmup.WarmAsync(reason?.String ?? "installed");
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
