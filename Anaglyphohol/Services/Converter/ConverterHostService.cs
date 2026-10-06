using SpawnDev;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Anaglyphohol.Services.Gpu;
using SpawnDev.ILGPU.Rendering;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Services.Converter
{
    /// <summary>
    /// The shared converter's host, in the offscreen document (see <see cref="SharedConverter"/>): receives page ports from
    /// the service worker, renders their frames with the ONE <see cref="ThreeDRenderer"/> (one GPU device, one set of
    /// models and kernels for every tab) and sends each 3D frame back as an ImageBitmap.
    /// </summary>
    public sealed class ConverterHostService : IAsyncBackgroundService
    {
        readonly SpawnJSRuntime JS;
        readonly ThreeDRenderer Renderer;
        readonly ShaderCacheService ShaderCache;
        readonly SemaphoreSlim _gate = new(1, 1);   // one frame at a time on the one device (ThreeDRenderer shares its buffers)
        readonly Dictionary<string, object> _videoOwners = new();   // "port:videoKey" -> ThreeDRenderer video owner
        readonly Dictionary<string, long> _videoLastMs = new();
        ServiceWorkerContainer? _sw;
        Window? _window;
        HTMLCanvasElement? _canvas;
        ICanvasRenderer? _target;
        int _nextPort;
        string? _primaryKey;   // the ONE video (across all tabs) the streaming model follows
        Task? _ready;

        public ConverterHostService(SpawnJSRuntime js, ThreeDRenderer renderer, ShaderCacheService shaderCache)
        {
            JS = js;
            Renderer = renderer;
            ShaderCache = shaderCache;
        }

        public Task Ready => _ready ??= InitAsync();

        async Task InitAsync()
        {
            _sw = JS.Get<ServiceWorkerContainer>("navigator.serviceWorker");
            _sw.OnMessage += OnWorkerMessage;
            _sw.StartMessages();
            // the relay holds page ports until the host says it listens (messages to a page nobody listens to are lost)
            using var reg = await _sw.Ready;
            using var active = reg.Active;
            active?.PostMessage(new HostReady { Type = SharedConverter.MsgHostReady });
            JS.Log("Anaglyphohol: shared converter ready.");
        }

        void OnWorkerMessage(MessageEvent e)
        {
            using var data = e.GetData<SpawnJSObject?>();
            if (data?.JSRef!.Get<string?>("type") != SharedConverter.MsgPort) return;
            using var ports = e.Ports;
            if (ports.Length < 1) return;
            var port = ports[0];
            var portId = (++_nextPort).ToString();
            port.OnMessage += m => _ = OnPortMessageAsync(portId, port, m);
            port.Start();
        }

        async Task OnPortMessageAsync(string portId, MessagePort port, MessageEvent m)
        {
            using var msg = m.GetData<SpawnJSObject?>();
            if (msg == null) return;
            var type = msg.JSRef!.Get<string?>("type");
            var videoKey = $"{portId}:{msg.JSRef!.Get<string?>("videoKey") ?? ""}";
            if (type == "reset") { if (_videoOwners.TryGetValue(videoKey, out var o)) Renderer.ResetVideo(o); return; }
            if (type == "release")
            {
                if (_videoOwners.Remove(videoKey, out var o)) Renderer.ReleaseVideo(o);
                _videoLastMs.Remove(videoKey);
                if (_primaryKey == videoKey) _primaryKey = null;
                return;
            }
            if (type != "render") return;
            int id = msg.JSRef!.Get<int>("id");
            var bmp = msg.JSRef!.Get<ImageBitmap?>("bmp");
            await _gate.WaitAsync();
            try
            {
                if (bmp == null) throw new InvalidOperationException("no frame");
                int w = msg.JSRef!.Get<int>("w"), h = msg.JSRef!.Get<int>("h");
                var mode = (ThreeDMode)msg.JSRef!.Get<int>("mode");
                float level = msg.JSRef!.Get<float>("level"), focus = msg.JSRef!.Get<float>("focus");
                bool video = msg.JSRef!.Get<bool>("video");
                int videoLevel = msg.JSRef!.Get<int>("videoLevel");
                object? owner = null;
                bool primary = false;
                if (video)
                {
                    if (!_videoOwners.TryGetValue(videoKey, out owner)) _videoOwners[videoKey] = owner = new object();
                    long now = Environment.TickCount64;
                    _videoLastMs[videoKey] = now;
                    // the page names its own primary video; across TABS the streaming model keeps the one it follows while
                    // that video still plays (a frame in the last 3 s) - two tabs must not reset each other's window per frame
                    bool wantsPrimary = msg.JSRef!.Get<bool>("primary");
                    if (wantsPrimary && (_primaryKey == null || _primaryKey == videoKey
                        || !_videoLastMs.TryGetValue(_primaryKey, out var last) || now - last > 3000))
                        _primaryKey = videoKey;
                    primary = _primaryKey == videoKey;
                }
                if (_canvas == null) { using var document = JS.Get<Document>("document"); _canvas = document.CreateElement<HTMLCanvasElement>("canvas"); }
                // the renderer draws into the canvas at ITS size (a page sizes its overlay to the frame; here we do): a new
                // canvas is 300x150 and returned only that corner in 3D (MEASURED 2026-10-05, first shared run)
                if (_canvas.Width != w) _canvas.Width = w;
                if (_canvas.Height != h) _canvas.Height = h;
                _target ??= await Renderer.CreateCanvasRendererAsync(_canvas);
                var stats = await Renderer.RenderAsync(bmp, w, h, _target, mode, level, focus, video, videoLevel,
                    videoOwner: owner, primaryVideo: primary);
                ShaderCache.FrameRendered();   // stores any kernel shader the converter had to compile (as pages do)
                _window ??= JS.Get<Window>("window");
                var result = await _window.CreateImageBitmap(_canvas);   // GPU-side copy of the presented frame
                using var reply = new SpawnJSObject(JS.New("Object"));
                reply.JSRef!.Set("type", "rendered");
                reply.JSRef!.Set("id", id);
                reply.JSRef!.Set("bmp", result);
                reply.JSRef!.Set("stats", JsonSerializer.Serialize(stats, ConverterJson.Default.FrameStats));
                port.PostMessage(reply, new object[] { result });
                result.Dispose();
            }
            catch (Exception ex)
            {
                using var reply = new SpawnJSObject(JS.New("Object"));
                reply.JSRef!.Set("type", "failed");
                reply.JSRef!.Set("id", id);
                reply.JSRef!.Set("error", ex.Message);
                try { port.PostMessage(reply); } catch { }
            }
            finally
            {
                _gate.Release();
                try { bmp?.Close(); } catch { }
                bmp?.Dispose();
            }
        }

        sealed class HostReady
        {
            [JsonPropertyName("type")] public string Type { get; set; } = "";
        }
    }

    [JsonSerializable(typeof(FrameStats))]
    internal partial class ConverterJson : JsonSerializerContext { }
}
