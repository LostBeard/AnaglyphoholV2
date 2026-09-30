using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// Content mode: adds the stylesheets that must live in the HOST document, not in the overlay's shadow root.
    /// <list type="bullet">
    /// <item>css/page.css - the anaglyphohol-state borders on the page's own &lt;img&gt;/&lt;video&gt; elements.</item>
    /// <item>css/MaterialIcons.css - its @font-face: a font face declared inside a shadow root is not loaded, but a
    /// document-level one is usable from inside it.</item>
    /// </list>
    /// </summary>
    public class PageStyleService : IAsyncBackgroundService
    {
        public Task Ready => _ready ??= InitAsync();
        Task? _ready;
        readonly SpawnJSRuntime JS;
        readonly SpawnDomRenderer Renderer;
        readonly BrowserExtensionService BrowserExtensionService;

        public PageStyleService(SpawnJSRuntime js, SpawnDomRenderer renderer, BrowserExtensionService browserExtensionService)
        {
            JS = js;
            Renderer = renderer;
            BrowserExtensionService = browserExtensionService;
        }

        Task InitAsync()
        {
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.Content || !JS.IsWindow) return Task.CompletedTask;
            using var document = JS.Get<Document>("document");
            foreach (var path in new[] { "css/page.css", "css/MaterialIcons.css" })
            {
                var href = BrowserExtensionService.GetURL(path);
                using var existing = Renderer.GetStyleSheet(document, href);
                if (existing != null) continue;   // a previous content-script instance on this page already added it
                using var link = Renderer.AttachStyleSheet(document, href);
            }
            return Task.CompletedTask;
        }
    }
}
