using System.Text.Json;
using System.Text.Json.Serialization;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Services
{
    // A page image from a host that sends no CORS headers cannot be read by the content script in Chrome (the canvas /
    // texture copy is tainted, and a crossOrigin reload is refused). Bing's image preview is the common case: the first
    // slide comes through Bing's CORS proxy, every next slide is the ORIGINAL from the source site (MEASURED 2026-10-05:
    // wallpaperaccess.com / wallpapercave.com slides stayed "failed", flat). The extension BACKGROUND holds host
    // permissions and fetches it without CORS; the bytes come back over runtime messaging and decode in the content
    // script into an ImageBitmap, which is never tainted. Same protocol shape as Gemineachy's BackgroundHttpRelay: JSON
    // strings (not marshalled objects) cross the boundary.
    // Firefox content scripts already read these pixels (host permissions apply to them), so this path never runs there.
    //
    // Limits, so the background is not a general cross-origin reader: http(s) only, NO credentials (the fetch carries no
    // cookies - only what any anonymous visitor gets), image/* responses only, at most 32 MB.

    /// <summary>Image relay request, content -> background.</summary>
    public record ImageRelayRequest(string Type, string Url);

    /// <summary>Image relay response, background -> content. <see cref="Data"/> is base64 in the JSON.</summary>
    public record ImageRelayResponse(bool Ok, string? ContentType, byte[]? Data, string? Error);

    [JsonSerializable(typeof(ImageRelayRequest))]
    [JsonSerializable(typeof(ImageRelayResponse))]
    internal partial class ImageRelayJson : JsonSerializerContext;

    /// <summary>Content side: asks the background for an image's bytes and decodes them.</summary>
    public static class ImageRelay
    {
        public const string RequestType = "anaglyphohol-image-relay";
        public const int MaxBytes = 32 * 1024 * 1024;

        /// <summary>Whether a runtime message is an image relay request (the background's other handlers skip it: the
        /// FIRST sendResponse wins, and they answer at once).</summary>
        public static bool IsRequest(string? raw) => raw != null && raw.Contains(RequestType, StringComparison.Ordinal);

        /// <summary>
        /// The image at <paramref name="url"/> as an ImageBitmap, fetched by the extension background, or null (not an
        /// http(s) URL, the host refused, not an image, too large, or no background).
        /// </summary>
        public static async Task<ImageBitmap?> FetchAsync(SpawnJSRuntime js, BrowserExtensionService bes, string url)
        {
            if (!IsHttpUrl(url)) return null;
            var runtime = bes.Runtime;
            if (runtime == null) return null;
            var reqJson = JsonSerializer.Serialize(new ImageRelayRequest(RequestType, url), ImageRelayJson.Default.ImageRelayRequest);
            string? respJson = null;
            // a sleeping MV3 service worker: the first message wakes it, but can land before .NET attached its listener
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                try { respJson = await runtime.SendMessage<string>(reqJson); break; }
                catch (Exception ex) when (attempt < 4 && IsWorkerAsleep(ex)) { await Task.Delay(150 * attempt); }
                catch { return null; }
            }
            if (string.IsNullOrEmpty(respJson)) return null;
            var resp = JsonSerializer.Deserialize(respJson, ImageRelayJson.Default.ImageRelayResponse);
            if (resp == null || !resp.Ok || resp.Data == null || resp.Data.Length == 0) return null;
            using var blob = new Blob(new[] { resp.Data }, new BlobOptions { Type = resp.ContentType ?? "" });
            using var window = js.Get<SpawnDev.SpawnJS.JSObjects.Window>("window");
            try { return await window!.CreateImageBitmap(blob); }
            catch { return null; }   // not decodable as an image
        }

        internal static bool IsHttpUrl(string? url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

        static bool IsWorkerAsleep(Exception ex) =>
            ex.Message.Contains("Receiving end does not exist", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Could not establish connection", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("message port closed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Background side: fetches the requested image (host permissions, no page CORS, no credentials) and answers with its
    /// bytes. Auto-starts as an IAsyncBackgroundService.
    /// </summary>
    public class ImageRelayBackgroundService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        Task? _ready;
        readonly BrowserExtensionService _bes;
        readonly SpawnJSRuntime JS;
        // a browser HttpClient is fetch(): with no credentials option set, fetch's default "same-origin" sends no cookies
        // to the image host (the request comes from the extension's origin)
        static readonly HttpClient _http = new();

        public ImageRelayBackgroundService(SpawnJSRuntime js, BrowserExtensionService bes)
        {
            JS = js;
            _bes = bes;
        }

        Task InitAsync()
        {
            if (_bes.ExtensionMode != ExtensionMode.Background) return Task.CompletedTask;
            var runtime = _bes.Runtime;
            if (runtime != null) runtime.OnMessage += OnMessage;
            return Task.CompletedTask;
        }

        // true keeps the message channel open for the asynchronous sendResponse
        bool OnMessage(SpawnJSObject data, MessageSender sender, Function? sendResponse)
        {
            if (sendResponse == null) return false;
            string? raw;
            try { raw = data.JSRef!.As<string>(); }
            catch { return false; }   // not a string message - not ours
            if (!ImageRelay.IsRequest(raw)) return false;
            ImageRelayRequest? req;
            try { req = JsonSerializer.Deserialize(raw!, ImageRelayJson.Default.ImageRelayRequest); }
            catch { return false; }
            if (req == null || req.Type != ImageRelay.RequestType) return false;
            _ = RespondAsync(req.Url, sendResponse);
            return true;
        }

        async Task RespondAsync(string url, Function sendResponse)
        {
            ImageRelayResponse result;
            try
            {
                result = await FetchImageAsync(url);
            }
            catch (Exception ex)
            {
                result = new ImageRelayResponse(false, null, null, ex.Message);
            }
            try
            {
                sendResponse.CallVoid(null, JsonSerializer.Serialize(result, ImageRelayJson.Default.ImageRelayResponse));
            }
            catch (Exception ex) { JS.Log($"Anaglyphohol: image relay response failed ({ex.Message})."); }
            finally { sendResponse.Dispose(); }
        }

        static async Task<ImageRelayResponse> FetchImageAsync(string url)
        {
            if (!ImageRelay.IsHttpUrl(url)) return new ImageRelayResponse(false, null, null, "not an http(s) url");
            using var r = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!r.IsSuccessStatusCode) return new ImageRelayResponse(false, null, null, $"HTTP {(int)r.StatusCode}");
            var contentType = r.Content.Headers.ContentType?.MediaType;
            if (contentType == null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return new ImageRelayResponse(false, contentType, null, "not an image");
            if (r.Content.Headers.ContentLength > ImageRelay.MaxBytes) return new ImageRelayResponse(false, contentType, null, "too large");
            var bytes = await r.Content.ReadAsByteArrayAsync();
            if (bytes.Length > ImageRelay.MaxBytes) return new ImageRelayResponse(false, contentType, null, "too large");
            return new ImageRelayResponse(true, contentType, bytes, null);
        }
    }
}
