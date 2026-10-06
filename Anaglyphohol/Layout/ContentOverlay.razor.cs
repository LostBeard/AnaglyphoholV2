using System.Diagnostics.CodeAnalysis;
using Anaglyphohol.Services;
using Anaglyphohol.Services.Gpu;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using SpawnDev.SpawnJS.BrowserExtension;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.RazorRenderer;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Anaglyphohol.Layout
{
    public partial class ContentOverlay
    {
        /// <summary>Assembly scanned for [ContentLocation] components (defaults to this app).</summary>
        [Parameter]
        public Assembly AppAssembly { get; set; } = typeof(ContentOverlay).Assembly;


        [Inject]
        ContentOverlayService ContentOverlayService { get; set; } = default!;

        [Inject]
        SpawnDev.SpawnJS.SpawnJSRuntime JS { get; set; } = default!;

        /// <summary>
        /// This content script runs in an iframe (content.js sets anaglyphoholInIframe). The toolbar starts MINIMIZED
        /// there and its show/hide is not saved, as in the store version (AnaglyphoholUI: never expanded in an iframe):
        /// an expanded toolbar per frame stacked toolbars over embedded players (MEASURED 2026-10-05 on rumble.com).
        /// </summary>
        bool InIframe => _inIframe ??= ReadInIframe();
        bool? _inIframe;
        bool ReadInIframe()
        {
            try { return JS.Get<bool?>("anaglyphoholInIframe") == true; }
            catch { return false; }
        }

        StorageArea? SyncStorage { get; set; }

        public bool HideContent => HideContentI == 0;

        /// <summary>
        /// Shown / minimized is remembered PER SITE and starts minimized, as in the store version (AnaglyphoholUI:
        /// storageSiteSet('ui-expanded'), default false). 1 = shown.
        /// </summary>
        string ShownKey => $"{BrowserExtensionService.LocationUri.Host.Replace(".", "_")}_{nameof(ContentOverlay)}_Shown";

        int HideContentI { get; set; }

        [Parameter]
        public IEnumerable<Assembly>? AdditionalAssemblies { get; set; }

        [Inject]
        TrackedMedia TrackedMedia { get; set; } = default!;

        [Inject]
        BrowserExtensionService BrowserExtensionService { get; set; } = default!;

        public Dictionary<string, object> ContentParameters { get; set; } = new Dictionary<string, object>();
        Type? ContentType { get; set; }
        DynamicComponent? dynamicComponent = null;
        bool BeenInit = false;
        public List<ContentRouteInfo> ContentRouteInfos { get; } = new List<ContentRouteInfo>();
        public ContentOverlayRouteInfo? ContentOverlayRouteInfo { get; private set; }
        public bool Loading { get; private set; } = true;
        /// <summary>
        /// Loading progress from 0.0 - 100.0, or null if not available
        /// </summary>
        public float? LoadingProgress { get; private set; }
        public void SetLoading(float? progress = null)
        {
            progress = progress == null ? null : Math.Min(Math.Max(progress.Value, 0f), 100f);
            if (progress == 100f)
            {
                SetLoadingComplete();
                return;
            }
            if (Loading && LoadingProgress == progress)
            {
                return;
            }
            //Console.WriteLine($"SetLoading: {progress?.ToString() ?? "-"}");
            Loading = true;
            LoadingProgress = progress;
            // called from service events (model load progress) that may run off the renderer's dispatcher
            _ = InvokeAsync(StateHasChanged);
        }
        public void SetLoadingComplete()
        {
            if (!Loading) return;
            //Console.WriteLine($"SetLoadingComplete");
            Loading = false;
            LoadingProgress = null;
            _ = InvokeAsync(StateHasChanged);
        }
        /// <summary>The store version's arrows, in the mode's colors; CSS flips them to point down while minimized.</summary>
        string[] ButtonIcons
        {
            get
            {
                switch (TrackedMedia?.Mode3D ?? ThreeDMode.RedCyan)
                {
                    case ThreeDMode.RedCyan:
                        return new string[] { "arrows-rb-64v.png", "arrows-rb-64v.png" };
                    case ThreeDMode.GreenMagenta:
                        return new string[] { "arrows-gm-64v.png", "arrows-gm-64v.png" };
                    default:
                        // Dimenco 2D+Z: no dedicated arrows image; the mode's own icon for both states
                        return new string[] { "icon-128.png", "icon-128.png" };
                }
            }
        }
        protected override void OnInitialized()
        {
            ContentOverlayService.ContentOverlay = this;
            if (!BeenInit)
            {
                SyncStorage = BrowserExtensionService.Browser!.Storage!.Sync;
                BeenInit = true;
                CacheRoutes();
                ContentOverlayUpdate();
                BrowserExtensionService.OnLocationChanged += BrowserExtensionService_OnLocationChanged;
                TrackedMedia.OnStateChanged += TrackedMedia_OnStateChanged;
            }
        }
        private void TrackedMedia_OnStateChanged()
        {
            _ = InvokeAsync(StateHasChanged);
        }

        /// <summary>Something on this page is set to 3D (images or videos); otherwise the toggle is drawn grey.</summary>
        bool Active3D => TrackedMedia.AnaglyphImagesEnabled || TrackedMedia.AnaglyphVideosEnabled;

        // Dragging the arrows moves the whole bar sideways. Not stored: a page load starts centered (the store version's
        // behavior). Native pointer events with pointer CAPTURE on the arrows' div: every move reaches it however fast the
        // pointer goes, and the bar moves by its transform with no Razor render per move (RazorRenderer's own guidance).
        // MEASURED 2026-10-06 (_tools/probe-capture-click.mjs, Chrome 151 + Firefox 156, real input): with the div holding
        // capture, the click after a press - moved or not - lands on the DIV and never on UIToggle's button inside it, so
        // ToggleClick does the toggling; a keyboard click (detail 0) still goes to the button, which toggles itself.
        const double DragThreshold = 5;
        ElementReference _barRef, _toggleRef;
        HTMLElement? _barEl, _toggleEl;
        int _pointerId = -1;
        bool _captured, _dragged;
        double _downX, _offsetAtDown, _offset;
        string? BarStyle => _offset == 0 ? null : $"transform: translateX({Css(_offset)}px);";
        static string Css(double px) => px.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

        protected override void OnAfterRender(bool firstRender)
        {
            if (_toggleEl != null) return;
            _toggleEl = _toggleRef.As<HTMLElement>();
            _barEl = _barRef.As<HTMLElement>();
            if (_toggleEl == null || _barEl == null) return;
            _toggleEl.OnPointerDown += Toggle_OnPointerDown;
            _toggleEl.OnPointerMove += Toggle_OnPointerMove;
            _toggleEl.OnPointerUp += Toggle_OnPointerUp;
            _toggleEl.OnPointerCancel += Toggle_OnPointerCancel;
        }

        void Toggle_OnPointerDown(PointerEvent e)
        {
            using (e)
            {
                if (e.Button != MouseButton.PrimaryButton) return;
                _pointerId = e.PointerId;
                _downX = e.ClientX;
                _offsetAtDown = _offset;
                _dragged = false;
                _captured = false;
                try
                {
                    _toggleEl!.SetPointerCapture(e.PointerId);
                    _captured = true;
                }
                catch { }   // not captured: the click goes to the button, which toggles itself (ToggleClick stays out)
            }
        }

        void Toggle_OnPointerMove(PointerEvent e)
        {
            using (e)
            {
                if (!_captured || e.PointerId != _pointerId) return;
                var dx = e.ClientX - _downX;
                if (!_dragged && Math.Abs(dx) < DragThreshold) return;
                _dragged = true;
                // keep the bar's center on screen: half the viewport either way
                double half = 0;
                try { half = JS.Get<double>("innerWidth") / 2; } catch { }
                var offset = _offsetAtDown + dx;
                _offset = half > 0 ? Math.Clamp(offset, -half + 24, half - 24) : offset;
                using var style = _barEl!.Style;
                style["transform"] = $"translateX({Css(_offset)}px)";
            }
        }

        void Toggle_OnPointerUp(PointerEvent e)
        {
            using (e) if (e.PointerId == _pointerId) _pointerId = -1;   // capture ends with the release; the click follows
        }

        void Toggle_OnPointerCancel(PointerEvent e)
        {
            using (e)
            {
                if (e.PointerId != _pointerId) return;
                _pointerId = -1;
                _captured = false;   // no click follows a cancel
            }
        }

        async Task ToggleClick(MouseEventArgs e)
        {
            var pointerClick = e.Detail > 0 && _captured;   // detail 0 = keyboard: the button already toggled
            var dragged = _dragged;
            _captured = false;
            _dragged = false;
            if (pointerClick && !dragged) await Clicked(HideContentI == 0 ? 1 : 0);
        }
        protected override async Task OnInitializedAsync()
        {
            if (InIframe)
            {
                HideContentI = 0;   // minimized; the top page's saved preference is not this frame's
                return;
            }
            if (SyncStorage != null)
            {
                try
                {
                    HideContentI = await SyncStorage.Get<int>(ShownKey, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"OnInitializedAsync SyncStorage failed: {ex.ToString()}");
                }
            }
        }
        async Task Clicked(int index)
        {
            HideContentI = index;
            if (InIframe) return;   // a frame's toggle does not change the saved (top page) preference
            try
            {
                if (SyncStorage != null) await SyncStorage.Set(ShownKey, HideContentI);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ContentOverlay.Clicked SyncStorage failed: {ex.ToString()}");
            }
        }
        private void BrowserExtensionService_OnLocationChanged(Uri obj)
        {
            _ = InvokeAsync(ContentOverlayUpdate);
        }
        void ContentOverlayUpdate()
        {
            var routeInfo = GetBestContentComponentRoute(BrowserExtensionService.Location);
            ContentOverlayRouteInfo = routeInfo;
            var contentType = routeInfo?.ContentComponentType;
            if (ContentType != contentType)
            {
                ContentType = contentType;
#if DEBUG && false
                Console.WriteLine($"ContentType changed: {ContentType?.Name ?? "NONE"}");
#endif
                StateHasChanged();
            }
        }
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Scans the application assembly (plus any AdditionalAssemblies the app passes in) for [ContentLocation] components, the way Blazor's own Router discovers routes. The Blazor SDK roots the application assembly, so its component types are never trimmed.")]
        void CacheRoutes()
        {
            var routes = new List<ContentOverlayRouteInfo>();
            var assemblies = new List<Assembly> { AppAssembly };
            if (AdditionalAssemblies != null) assemblies.AddRange(AdditionalAssemblies);
            foreach (var assembly in assemblies)
            {
                var componentTypes = assembly.ExportedTypes.Where(o => o.IsSubclassOf(typeof(ComponentBase))).ToList();
                foreach (var componentType in componentTypes)
                {
                    var attrs = componentType.GetCustomAttributes<ContentLocationAttribute>();
                    attrs = attrs.OrderByDescending(o => o.Weight).ThenByDescending(o => o.LocationRegexPattern.Length).ToList();
                    if (attrs.Count() == 0) continue;
                    var contentRouteInfo = new ContentRouteInfo
                    {
                        Assembly = assembly,
                        ComponentType = componentType,
                        ContentLocations = (List<ContentLocationAttribute>)attrs,
                    };
                    ContentRouteInfos.Add(contentRouteInfo);
                }
            }
        }
        ContentOverlayRouteInfo? GetBestContentComponentRoute(string location)
        {
            var routes = new List<ContentOverlayRouteInfo>();
            foreach (var contentRouteInfo in ContentRouteInfos)
            {
                foreach (var attr in contentRouteInfo.ContentLocations)
                {
                    var m = Regex.Match(location, attr.LocationRegexPattern);
                    if (m.Success)
                    {
                        routes.Add(new ContentOverlayRouteInfo(location, contentRouteInfo.ComponentType, m, attr));
                        break;
                    }
                }
            }
            // sort by weight first, and then pattern length; then take the first
            var ret = routes.OrderByDescending(o => o.ContentLocationAttribute.Weight).ThenByDescending(o => o.ContentLocationAttribute.LocationRegexPattern.Length).FirstOrDefault();
            return ret;
        }
    }
}
