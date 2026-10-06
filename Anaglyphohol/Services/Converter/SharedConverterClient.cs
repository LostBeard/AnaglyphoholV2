using System.Text.Json;
using Anaglyphohol.Services.Gpu;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using GPUCopyExternalImageSource = SpawnDev.SpawnJS.Union<SpawnDev.SpawnJS.JSObjects.ImageBitmap, SpawnDev.SpawnJS.JSObjects.ImageData, SpawnDev.SpawnJS.JSObjects.HTMLImageElement, SpawnDev.SpawnJS.JSObjects.HTMLVideoElement, SpawnDev.SpawnJS.JSObjects.VideoFrame, SpawnDev.SpawnJS.JSObjects.HTMLCanvasElement, SpawnDev.SpawnJS.JSObjects.OffscreenCanvas>;

namespace Anaglyphohol.Services.Converter
{
    /// <summary>
    /// Shared converter, page side (see <see cref="SharedConverter"/>): sends a frame as an ImageBitmap to the offscreen
    /// document's <see cref="ConverterHostService"/> and draws the 3D frame it returns into the overlay canvas (2D context,
    /// the same canvas the local WebGPU renderer draws into). The page itself never creates a GPU device, loads a model or
    /// compiles a kernel in this mode.
    /// </summary>
    public sealed class SharedConverterClient : IDisposable
    {
        readonly SpawnJSRuntime JS;
        readonly ExtensionPortService Ports;
        readonly Dictionary<int, TaskCompletionSource<(ImageBitmap? Bitmap, string? Stats, string? Error)>> _pending = new();
        MessagePort? _port;
        Task<bool>? _connecting;
        int _nextId;
        int _consecutiveErrors;
        bool _anyReply;

        /// <summary>
        /// The converter stopped answering, or failed <see cref="MaxConsecutiveErrors"/> frames in a row (its GPU device
        /// lost, its document closed or crashed). One way: every page using it renders on its own GPU from then on
        /// (<see cref="TrackedMedia"/>), as it would have without the shared converter.
        /// </summary>
        public bool Failed { get; private set; }
        /// <summary>Raised once, when <see cref="Failed"/> becomes true.</summary>
        public event Action? OnFailed;
        /// <summary>Converter-side errors in a row that mean it is broken, not that one frame was bad.</summary>
        public const int MaxConsecutiveErrors = 3;
        /// <summary>
        /// A request with no reply for this long means the converter is gone: a closed or crashed document's port drops
        /// messages silently, and the page would wait forever. The first reply includes the converter's model load and
        /// kernel builds (MEASURED 2026-10-05: 5.8 s), later ones may queue behind other tabs' frames (one at a time).
        /// </summary>
        public static readonly TimeSpan FirstReplyTimeout = TimeSpan.FromSeconds(45), ReplyTimeout = TimeSpan.FromSeconds(15);

        /// <summary>Why the last connect failed (null = connected or never tried).</summary>
        public string? ConnectError { get; private set; }

        public SharedConverterClient(SpawnJSRuntime js, ExtensionPortService ports)
        {
            JS = js;
            Ports = ports;
        }

        /// <summary>Connects once per page; false = the shared converter is unavailable (the page renders itself).</summary>
        public Task<bool> ConnectAsync() => _connecting ??= ConnectCoreAsync();

        async Task<bool> ConnectCoreAsync()
        {
            try
            {
                _port = await Ports.ConnectAsync(SharedConverter.PortName);
                _port.OnMessage += OnMessage;
                _port.Start();
                return true;
            }
            catch (Exception ex)
            {
                ConnectError = ex.Message;
                JS.Log($"Anaglyphohol: shared converter not reached ({ex.Message}); this page renders itself.");
                return false;
            }
        }

        void OnMessage(MessageEvent e)
        {
            using var msg = e.GetData<SpawnJSObject?>();
            if (msg == null) return;
            int id = msg.JSRef!.Get<int>("id");
            var type = msg.JSRef!.Get<string?>("type");
            if (!_pending.Remove(id, out var tcs))
            {
                // a reply after its request timed out: release the frame it carries
                if (type == "rendered") { using var late = msg.JSRef!.Get<ImageBitmap?>("bmp"); try { late?.Close(); } catch { } }
                return;
            }
            if (type == "rendered") tcs.TrySetResult((msg.JSRef!.Get<ImageBitmap?>("bmp"), msg.JSRef!.Get<string?>("stats"), null));
            else tcs.TrySetResult((null, null, msg.JSRef!.Get<string?>("error") ?? "failed"));
        }

        /// <summary>
        /// Renders <paramref name="source"/> (natural size <paramref name="width"/> x <paramref name="height"/>) in 3D in the
        /// shared converter and draws the result on <paramref name="canvas"/>, sized to the frame.
        /// </summary>
        public async Task<FrameStats> RenderAsync(GPUCopyExternalImageSource source, int width, int height, HTMLCanvasElement canvas,
            ThreeDMode mode, float level3D, float focus3D, bool video, int videoLevel, string? videoKey, bool primaryVideo)
        {
            if (Failed) throw new InvalidOperationException("shared converter failed; this page renders itself");
            var port = _port ?? throw new InvalidOperationException("not connected");
            // a snapshot of the source; TRANSFERRED to the converter (no pixel copy across the process boundary)
            using var frame = await SnapshotAsync(source.Value);
            int id = ++_nextId;
            var tcs = new TaskCompletionSource<(ImageBitmap? Bitmap, string? Stats, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            using (var msg = new SpawnJSObject(JS.New("Object")))
            {
                msg.JSRef!.Set("type", "render");
                msg.JSRef!.Set("id", id);
                msg.JSRef!.Set("bmp", frame);
                msg.JSRef!.Set("w", width);
                msg.JSRef!.Set("h", height);
                msg.JSRef!.Set("mode", (int)mode);
                msg.JSRef!.Set("level", level3D);
                msg.JSRef!.Set("focus", focus3D);
                msg.JSRef!.Set("video", video);
                msg.JSRef!.Set("videoLevel", videoLevel);
                msg.JSRef!.Set("videoKey", videoKey ?? "");
                msg.JSRef!.Set("primary", primaryVideo);
                port.PostMessage(msg, new object[] { frame });
            }
            var timeout = _anyReply ? ReplyTimeout : FirstReplyTimeout;
            if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)) != tcs.Task)
            {
                _pending.Remove(id);
                Fail($"no reply in {timeout.TotalSeconds:0} s");
                throw new InvalidOperationException("shared converter: no reply");
            }
            var (bitmap, statsJson, error) = await tcs.Task;
            if (Failed) { try { bitmap?.Close(); } catch { } bitmap?.Dispose(); throw new InvalidOperationException("shared converter failed"); }
            _anyReply = true;
            if (bitmap == null)
            {
                if (++_consecutiveErrors >= MaxConsecutiveErrors) Fail($"{_consecutiveErrors} frames failed in a row, last: {error}");
                throw new InvalidOperationException($"shared converter: {error}");
            }
            _consecutiveErrors = 0;
            try
            {
                if (canvas.Width != width) canvas.Width = width;
                if (canvas.Height != height) canvas.Height = height;
                using var ctx = canvas.Get2DContext();
                ctx.DrawImage(bitmap, 0, 0);
            }
            finally
            {
                bitmap.Close();
                bitmap.Dispose();
            }
            return statsJson != null ? JsonSerializer.Deserialize(statsJson, ConverterJson.Default.FrameStats) : default;
        }

        Task<ImageBitmap> SnapshotAsync(object? value)
        {
            _window ??= JS.Get<Window>("window");
            return value switch
            {
                HTMLVideoElement v => _window.CreateImageBitmap(v),
                HTMLImageElement i => _window.CreateImageBitmap(i),
                ImageBitmap b => _window.CreateImageBitmap(b),   // a copy: the caller keeps its bitmap
                HTMLCanvasElement c => _window.CreateImageBitmap(c),
                OffscreenCanvas o => _window.CreateImageBitmap(o),
                VideoFrame f => _window.CreateImageBitmap(f),
                ImageData d => _window.CreateImageBitmap(d),
                _ => throw new NotSupportedException($"shared converter: unsupported frame source {value?.GetType().Name ?? "null"}"),
            };
        }
        Window? _window;

        /// <summary>A video seeked / changed source: its temporal history starts over.</summary>
        public void ResetVideo(string videoKey) => Send("reset", videoKey);
        /// <summary>A video went away: its history is freed.</summary>
        public void ReleaseVideo(string videoKey) => Send("release", videoKey);

        void Send(string type, string videoKey)
        {
            if (_port == null) return;
            using var msg = new SpawnJSObject(JS.New("Object"));
            msg.JSRef!.Set("type", type);
            msg.JSRef!.Set("videoKey", videoKey);
            try { _port.PostMessage(msg); } catch { }
        }

        void Fail(string reason)
        {
            if (Failed) return;
            Failed = true;
            JS.Log($"Anaglyphohol: shared converter failed ({reason}); this page renders itself from now on.");
            foreach (var t in _pending.Values) t.TrySetResult((null, null, "shared converter failed"));
            _pending.Clear();
            try { _port?.Close(); } catch { }
            _port?.Dispose();
            _port = null;
            try { OnFailed?.Invoke(); } catch (Exception ex) { JS.Log($"Anaglyphohol: shared converter OnFailed: {ex.Message}"); }
        }

        public void Dispose()
        {
            try { _port?.Close(); } catch { }
            _port?.Dispose();
            _port = null;
        }
    }
}
