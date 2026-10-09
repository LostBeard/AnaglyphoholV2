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
            _ = ShaderWarmup.EnsureAsync();   // also covers an install / update event this background did not see
        }

        // Install, update, or a browser update (its WebGPU may differ): prepare the kernel shaders pages will need.
        void Runtime_OnInstalled(OnInstalledDetails details)
        {
            var reason = details.Reason;
            JS.Log($"Anaglyphohol: runtime.onInstalled ({reason?.String ?? "?"})");
            if (reason?.Enum == OnInstalledReason.SharedModuleUpdate) return;
            // a FIRST install opens the get-started page (the store version opened its get-started.html the same way), and so
            // does an update to a new MAJOR version (3.x -> 4.0: a new engine, and its kernel warm-up shows there - TJ
            // 2026-10-06). A minor update opens nothing.
            if (reason?.Enum == OnInstalledReason.Install) _ = OpenGetStartedAsync(false);
            else if (reason?.Enum == OnInstalledReason.Update && IsMajorUpdate(details.PreviousVersion)) _ = OpenGetStartedAsync(true);
            _ = ShaderWarmup.WarmAsync(reason?.String ?? "installed");
        }

        bool IsMajorUpdate(string? previousVersion)
        {
            try
            {
                using var manifest = _runtime!.GetManifest();
                var current = manifest.JSRef!.Get<string?>("version");
                static int Major(string? v) => int.TryParse((v ?? "").Split('.')[0], out var m) ? m : -1;
                return Major(previousVersion) >= 0 && Major(current) > Major(previousVersion);
            }
            catch
            {
                return false;
            }
        }

        async Task OpenGetStartedAsync(bool updated)
        {
            try
            {
                var tabs = BrowserExtensionService.Browser?.Tabs;
                if (tabs == null) return;
                var url = BrowserExtensionService.GetURL(updated ? "index.html?$=installed&updated=1" : "index.html?$=installed");
                using var tab = await tabs.Create(new CreateTabProperties { Url = url });
                JS.Log($"Anaglyphohol: opened the get-started page ({url}).");
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: could not open the get-started page ({ex.Message}).");
            }
        }

        bool Runtime_OnMessage(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            // an image relay request is ImageRelayBackgroundService's to answer, later: the FIRST sendResponse wins, so
            // this immediate acknowledgement would hand the content script an empty reply
            string? raw = null;
            try { raw = data.JSRef!.As<string>(); } catch { }
            if (ImageRelay.IsRequest(raw))
            {
                data.Dispose();
                sender.Dispose();
                sendResponse?.Dispose();
                return false;
            }
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
