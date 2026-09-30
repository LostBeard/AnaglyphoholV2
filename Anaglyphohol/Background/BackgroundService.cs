using Action = System.Action;
using Anaglyphohol.Services;
using Bink;
using SpawnDev;
using SpawnDev.AccountsShared.Services;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using System.Text.Json.Serialization;

namespace Anaglyphohol.Background
{
    /// <summary>
    /// Extension background (Chrome service worker / Firefox background page) message handling:
    /// <list type="bullet">
    /// <item>runtime.onMessageExternal: spawndev.com hands over the signed-in account token as <c>{ type: "uv", value }</c>
    /// (manifest <c>externally_connectable</c>); it is stored through <see cref="AppIdentityService"/> and every content
    /// script picks it up from storage.sync.</item>
    /// <item>runtime.onMessage: acknowledged (content scripts use it to wake the worker).</item>
    /// </list>
    /// </summary>
    public class BackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _Ready ??= InitAsync();
        private Task? _Ready;
        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BrowserExtensionService;
        readonly SyncStorageService SyncStorageService;
        readonly AppIdentityService AppIdentityService;
        Runtime? _runtime;

        public BackgroundService(SpawnJSRuntime js, BrowserExtensionService browserExtensionService, SyncStorageService syncStorageService, AppIdentityService appIdentityService)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
            SyncStorageService = syncStorageService;
            AppIdentityService = appIdentityService;
        }

        public string UserName => string.IsNullOrEmpty(AppIdentityService.User.UsernameInClaim()) ? "Guest" : AppIdentityService.User.UsernameInClaim();
        public event Action? OnStateHasChanged;

        async Task InitAsync()
        {
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.Background) return;
            await SyncStorageService.Ready;
            await AppIdentityService.Ready;
            _runtime = BrowserExtensionService.Runtime;
            if (_runtime == null) return;
            _runtime.OnMessage += Runtime_OnMessage;
            _runtime.OnMessageExternal += Runtime_OnMessageExternal;
        }

        /// <summary>The <c>{ succ, from }</c> reply spawndev.com expects.</summary>
        sealed class ExternalReply
        {
            [JsonPropertyName("succ")] public bool Succ { get; set; }
            [JsonPropertyName("from")] public string? From { get; set; }
        }

        bool Runtime_OnMessageExternal(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            using var _data = data;
            using var _sender = sender;
            if (sendResponse == null) return false;
            string? type = null, value = null;
            if (data.JSRef!.TypeOf() == "object")
            {
                type = data.JSRef.Get<string?>("type");
                value = data.JSRef.Get<string?>("value");
            }
            if (type != "uv")
            {
                Reply(sendResponse, false);
                return false;
            }
            _ = SetTokenAndReply(value ?? "", sendResponse);
            return true;   // keep the channel open for the async reply
        }

        async Task SetTokenAndReply(string token, Function sendResponse)
        {
            var ok = false;
            try
            {
                await AppIdentityService.SetToken(token);
                ok = true;
                OnStateHasChanged?.Invoke();
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: account token update failed: {ex.Message}");
            }
            Reply(sendResponse, ok);
        }

        void Reply(Function sendResponse, bool ok)
        {
            try
            {
                sendResponse.CallVoid(null, new ExternalReply { Succ = ok, From = SyncStorageService.QueryableKey });
            }
            catch (Exception ex)
            {
                // an unhandled exception on a runtime callback would kill the WASM runtime
                JS.Log($"Anaglyphohol: external reply failed: {ex.Message}");
            }
            finally
            {
                sendResponse.Dispose();
            }
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
