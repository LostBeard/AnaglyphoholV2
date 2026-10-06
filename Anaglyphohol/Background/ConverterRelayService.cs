using SpawnDev;
using Anaglyphohol.Services.Converter;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Background
{
    /// <summary>
    /// Shared converter, background side (Chrome service worker): takes the ports pages connect
    /// (<see cref="ExtensionPortService"/>), makes sure the offscreen document exists (<see cref="SharedConverter.DocumentPath"/>)
    /// and hands each port to it once its <see cref="ConverterHostService"/> says it listens. After that the page and the
    /// offscreen document talk directly; this worker only matchmakes.
    /// </summary>
    public sealed class ConverterRelayService : IAsyncBackgroundService
    {
        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BES;
        readonly ExtensionPortService Ports;
        readonly Queue<MessagePort> _waiting = new();
        string? _hostClientId;   // the offscreen document's client id once it reported ready
        Task? _creating;
        Task? _ready;

        public ConverterRelayService(SpawnJSRuntime js, BrowserExtensionService bes, ExtensionPortService ports)
        {
            JS = js;
            BES = bes;
            Ports = ports;
        }

        public Task Ready => _ready ??= InitAsync();

        Task InitAsync()
        {
            if (BES.ExtensionMode != ExtensionMode.Background || !JS.IsServiceWorkerGlobalScope) return Task.CompletedTask;
            Ports.OnPortConnected += OnPortConnected;
            using var self = JS.Get<ServiceWorkerGlobalScope>("self");
            self.OnMessage += OnWorkerMessage;
            return Task.CompletedTask;
        }

        void OnWorkerMessage(ExtendableMessageEvent e)
        {
            using var data = e.Data;
            var type = data?.JSRef!.Get<string?>("type");
            if (type == SharedConverter.MsgHostBroken) { _ = ReplaceBrokenHostAsync(); return; }
            if (type != SharedConverter.MsgHostReady) return;
            using var source = e.Source;
            _hostClientId = source.Id;
            _ = FlushAsync();
        }

        async Task ReplaceBrokenHostAsync()
        {
            _hostClientId = null;
            try
            {
                var offscreen = BES.Browser?.Offscreen;
                if (offscreen != null && await offscreen.HasDocument()) await offscreen.CloseDocument();
                JS.Log("Anaglyphohol: shared converter document closed (its frames kept failing); the next page opens a new one.");
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: could not close the broken shared converter ({ex.Message}).");
            }
        }

        void OnPortConnected(string name, MessagePort port)
        {
            if (name != SharedConverter.PortName) { port.Close(); return; }
            _waiting.Enqueue(port);
            _ = FlushAsync();
        }

        async Task FlushAsync()
        {
            try
            {
                if (_waiting.Count == 0) return;
                var offscreen = BES.Browser?.Offscreen;
                if (offscreen == null)
                {
                    JS.Log("Anaglyphohol: shared converter unavailable (no offscreen API); pages render themselves.");
                    while (_waiting.TryDequeue(out var p)) p.Close();
                    return;
                }
                if (!await offscreen.HasDocument())
                {
                    _hostClientId = null;   // a new document reports ready again
                    _creating ??= offscreen.CreateDocument(new OffscreenCreateParameters
                    {
                        Url = SharedConverter.DocumentPath,
                        Reasons = new[] { OffscreenReason.Workers },
                        Justification = "Runs Anaglyphohol's shared depth + 3D converter (WebGPU) for every tab.",
                    });
                    try { await _creating; } finally { _creating = null; }
                    return;   // the host's ready message flushes the queue
                }
                if (_hostClientId == null)
                {
                    // this worker restarted while the document lived on: find it (it has been listening since it started)
                    using var self = JS.Get<ServiceWorkerGlobalScope>("self");
                    using var clients = self.Clients;
                    using var all = await clients.MatchAll(new ClientsMatchAllOptions { IncludeUncontrolled = true });
                    for (int i = 0; i < all.Length && _hostClientId == null; i++)
                    {
                        using var c = all[i];
                        if (c.Url.Contains("$=" + SharedConverter.PageKey, StringComparison.Ordinal)) _hostClientId = c.Id;
                    }
                    if (_hostClientId == null) return;   // still loading: its ready message flushes the queue
                }
                using (var self = JS.Get<ServiceWorkerGlobalScope>("self"))
                using (var clients = self.Clients)
                using (var host = await clients.Get(_hostClientId))
                {
                    if (host == null) { _hostClientId = null; return; }
                    while (_waiting.TryDequeue(out var port))
                    {
                        using var msg = new SpawnJSObject(JS.New("Object"));
                        msg.JSRef!.Set("type", SharedConverter.MsgPort);
                        host.PostMessage(msg, new object[] { port });
                        port.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: shared converter relay failed ({ex.Message}).");
            }
        }
    }
}
