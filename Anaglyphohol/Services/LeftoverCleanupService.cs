using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// Removes what a PREVIOUS Anaglyphohol content script left in this page, before this one draws anything.
    /// <para>
    /// MEASURED 2026-10-06 (Firefox 156, _tools/probe-firefox-reinstall.mjs): installing or UPDATING the add-on runs the new
    /// content script in tabs that are already open, while the previous version's toolbar and overlay canvases stay in the
    /// page DEAD - a second toolbar that no longer answers, and overlays that keep a frozen 3D frame on top of the media
    /// (5 overlays became 10 after one update). Every Firefox user would see that on every update until they reload.
    /// Chrome does not inject into open tabs, so there this finds nothing.
    /// </para>
    /// Removed: our toolbar host (<see cref="ToolbarHostAttribute"/>), overlay canvases, the Dimenco header, the state
    /// attributes on media; and the store's 3.x build's toolbar root and overlays (the 3.0.x -> 4.0 update).
    /// </summary>
    public sealed class LeftoverCleanupService : IAsyncBackgroundService
    {
        /// <summary>Set on the toolbar's host element (Program.cs ConfigureHost) so a later instance can find it.</summary>
        public const string ToolbarHostAttribute = "anaglyphohol-ui";

        // 3.x (vjs/anglyphoholv3): AnaglyphoholUI's root, style set once by setAttribute and never changed (closed shadow)
        const string V3ToolbarSelector = "body > div[style=\"position: fixed; top: 0; left: 0; right: 0; height: 0; overflow: visible; display: flex; flex-direction: row; justify-content:center; z-index: 65536;\"]";
        // 3.x TrackedMediaElement's overlay: a div next to the img / video, open shadow root holding ONE canvas styled
        // "width: 100%; height: 100%;" (+ object-fit). Its own style grows (top, left, display...) after these first two declarations.
        const string V3OverlaySelector = "div[style^=\"position: absolute; pointer-events: none;\"]";

        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BrowserExtensionService;
        Task? _ready;

        public LeftoverCleanupService(SpawnJSRuntime js, BrowserExtensionService browserExtensionService)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
        }

        public Task Ready => _ready ??= InitAsync();

        Task InitAsync()
        {
            if (BrowserExtensionService.ExtensionMode != ExtensionMode.Content || !JS.IsWindow) return Task.CompletedTask;
            try
            {
                using var document = JS.Get<Document>("document");
                int removed = RemoveAll(document, $"[{ToolbarHostAttribute}]")
                    + RemoveAll(document, ".custom-media-overlay-canvas")
                    + RemoveAll(document, ".anaglyphohol-dimenco-header")
                    + RemoveAll(document, V3ToolbarSelector)
                    + RemoveV3Overlays(document);
                int cleared = 0;
                foreach (var name in new[] { TrackedMediaElement.StateAttributeName, TrackedMediaElement.FallbackAttributeName, "anaglyphohol-cost", "anaglyphohol-profile" })
                    cleared += ClearAttribute(document, name);
                if (removed + cleared > 0)
                    JS.Log($"Anaglyphohol: removed {removed} element(s) and {cleared} attribute(s) a previous version left in this page.");
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: leftover cleanup failed ({ex.Message}).");
            }
            return Task.CompletedTask;
        }

        static int RemoveAll(Document document, string selector)
        {
            using var list = document.QuerySelectorAll<Element>(selector);
            int n = list.Length;
            for (int i = 0; i < n; i++)
            {
                using var el = list[i];
                el.Remove();
            }
            return n;
        }

        static int RemoveV3Overlays(Document document)
        {
            using var list = document.QuerySelectorAll<Element>(V3OverlaySelector);
            int removed = 0;
            for (int i = 0; i < list.Length; i++)
            {
                using var el = list[i];
                using var shadow = el.ShadowRoot;
                if (shadow == null || shadow.ChildElementCount != 1) continue;
                using var canvas = shadow.QuerySelector<Element>("canvas");
                // MEASURED: 3.x then appends object-fit ("width: 100%; height: 100%; object-fit: fill;"), so a prefix
                if (canvas == null || !(canvas.GetAttribute("style") ?? "").StartsWith("width: 100%; height: 100%;", StringComparison.Ordinal)) continue;
                el.Remove();
                removed++;
            }
            return removed;
        }

        static int ClearAttribute(Document document, string name)
        {
            using var list = document.QuerySelectorAll<Element>($"[{name}]");
            int n = list.Length;
            for (int i = 0; i < n; i++)
            {
                using var el = list[i];
                el.RemoveAttribute(name);
            }
            return n;
        }
    }
}
