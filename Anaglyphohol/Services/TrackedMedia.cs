using Action = System.Action;
using Anaglyphohol.Services.Gpu;
using Bink;
using SpawnDev.AccountsShared.Services;
using SpawnDev;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using Window = SpawnDev.SpawnJS.JSObjects.Window;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// Content script: tracks the page's &lt;img&gt; and &lt;video&gt; elements and renders them in 3D, one frame at a
    /// time, through <see cref="ThreeDRenderer"/>.
    /// </summary>
    public class TrackedMedia : IDisposable
    {
        public Document? Document { get; private set; }
        public Window? Window { get; private set; }
        public MutationObserver? BodyObserver { get; private set; }
        readonly SpawnJSRuntime JS;
        public BrowserExtensionService BrowserExtensionService { get; }
        public ThreeDRenderer ThreeDRenderer { get; }
        public DepthService DepthService { get; }
        public DimencoHeaderService DimencoHeaderService { get; }
        public AppIdentityService AppIdentityService { get; }
        public Dictionary<string, TrackedMediaElement> TrackedElements { get; } = new Dictionary<string, TrackedMediaElement>();
        public event Action? OnTrackedElementCountChanged;
        public event Action? OnStateChanged;
        bool _AnaglyphVideosEnabled = false;
        bool _AnaglyphImagesEnabled = false;
        /// <summary>Toggle icon per <see cref="ThreeDMode"/>, in mode order.</summary>
        public List<string> RendererIcons { get; } = new List<string> { "red-blue-32.png", "green-magenta-32.png", "icon-128.png" };
        public List<RecommendedSite> RecommendedLinks { get; } = new List<RecommendedSite>
        {
            new RecommendedSite("Yahoo.com Images", "sites/yahoo.png", "https://images.search.yahoo.com/search/images?p=nature"),
            new RecommendedSite("Bing.com Images", "sites/bing.png", "https://www.bing.com/images"),
            new RecommendedSite("Google.com Images", "sites/google.png", "https://www.google.com/search?udm=2&q=nature"),
            new RecommendedSite("YouTube.com", "sites/youtube.png", "https://www.youtube.com/"),
            new RecommendedSite("Twitch.tv", "sites/twitch.png", "https://www.twitch.tv/"),
            new RecommendedSite("Pluto.tv Live Video", "sites/plutotv.png", "https://pluto.tv/"),
            new RecommendedSite("TubiTV.com Live Video", "sites/tubi.png", "https://tubitv.com/live"),
        };
        public bool AnaglyphVideosEnabled
        {
            get => _AnaglyphVideosEnabled;
            set
            {
                if (_AnaglyphVideosEnabled == value) return;
                _AnaglyphVideosEnabled = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        public bool AnaglyphImagesEnabled
        {
            get => _AnaglyphImagesEnabled;
            set
            {
                if (_AnaglyphImagesEnabled == value) return;
                _AnaglyphImagesEnabled = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        ThreeDMode _Mode3D = ThreeDMode.RedCyan;
        float _Level3D = 0.8f;
        float _Focus3D = 0.5f;

        public ThreeDMode Mode3D
        {
            get => _Mode3D;
            set
            {
                if (!Enum.IsDefined(value)) value = ThreeDMode.RedCyan;   // a stored mode from a newer build
                if (_Mode3D == value) return;
                _Mode3D = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        /// <summary>The depth model in use (DAv3 by default).</summary>
        public DepthModelKind DepthModel
        {
            get => DepthService.Model;
            set
            {
                if (DepthService.Model == value) return;
                DepthService.Model = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        bool _DrawStats = false;
        public bool DrawStats
        {
            get => _DrawStats;
            set
            {
                if (_DrawStats == value) return;
                _DrawStats = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        public float Level3D
        {
            get => _Level3D;
            set
            {
                if (_Level3D == value) return;
                _Level3D = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        public float Focus3D
        {
            get => _Focus3D;
            set
            {
                if (_Focus3D == value) return;
                _Focus3D = value;
                _ = CheckTrackedElementsDelayed();
            }
        }
        public void ToggleDrawStats()
        {
            DrawStats = !DrawStats;
        }
        public bool Started { get; private set; }
        bool _CheckTrackedElementsDelayedRunning = false;
        async Task CheckTrackedElementsDelayed()
        {
            if (_CheckTrackedElementsDelayedRunning) return;
            _CheckTrackedElementsDelayedRunning = true;
            try
            {
                await Task.Delay(200);
                foreach (var el in TrackedElements.Values.ToList())
                {
                    if (el.IsHTMLImageElement)
                    {
                        el.OverlayVisible = AnaglyphImagesEnabled;
                    }
                    else if (el.IsHTMLVideoElement)
                    {
                        el.OverlayVisible = AnaglyphVideosEnabled;
                    }
                    el.UpdateFrame();
                }
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: CheckTrackedElementsDelayed: {ex.Message}");
            }
            finally
            {
                _CheckTrackedElementsDelayedRunning = false;
            }
        }
        public bool IsRecommendedSite { get; }
        public bool Limited => !IsRecommendedSite && !AppIdentityService.User.Roles().Intersect(new[] { "Anaglyphohol", "Onyx" }).Any();
        public TrackedMedia(SpawnJSRuntime js, BrowserExtensionService browserExtensionService, ThreeDRenderer threeDRenderer,
            DepthService depthService, DimencoHeaderService dimencoHeaderService, AppIdentityService appIdentityService)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
            ThreeDRenderer = threeDRenderer;
            DepthService = depthService;
            DimencoHeaderService = dimencoHeaderService;
            AppIdentityService = appIdentityService;
            if (JS.GlobalScope == GlobalScope.Window)
            {
                Window = JS.Get<Window>("window");
                Window.OnResize += Window_OnResize;
                Document = JS.Get<Document>("document");
                Document.OnFullscreenChange += Document_OnFullscreenChange;
            }
            var host = BrowserExtensionService.LocationUri.Host;
            var recommendedHosts = RecommendedLinks.Select(o => new Uri(o.URL).Host).ToList();
            IsRecommendedSite = recommendedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
        }
        void Window_OnResize() => _ = CheckTrackedElementsDelayed();
        void Document_OnFullscreenChange() => _ = CheckTrackedElementsDelayed();

        ActionCallback<Array<MutationRecord>, MutationObserver>? BodyObserverObservedCallback = null;
        public void Start()
        {
            if (Started) return;
            Started = true;
            using var body = Document?.QuerySelector<HTMLBodyElement>("body");
            if (body != null)
            {
                BodyObserver = new MutationObserver(BodyObserverObservedCallback = new ActionCallback<Array<MutationRecord>, MutationObserver>(BodyObserver_Observed));
                BodyObserver.Observe(body, new MutationObserverOptions { ChildList = true, Subtree = true });
            }
            ElementUpdate();
        }
        public void StopIt()
        {
            if (!Started) return;
            Started = false;
            if (BodyObserver != null)
            {
                BodyObserver.Disconnect();
                BodyObserver.Dispose();
                BodyObserver = null;
                BodyObserverObservedCallback?.Dispose();
                BodyObserverObservedCallback = null;
            }
        }
        void ElementUpdate()
        {
            var changed = false;
            var elements = new List<HTMLElement>();
            elements.AddRange(Document!.QuerySelectorAll<HTMLImageElement>("img").Using(nodeList => nodeList.ToArray()));
            elements.AddRange(Document!.QuerySelectorAll<HTMLVideoElement>("video").Using(nodeList => nodeList.ToArray()));
            var uidsFound = new List<string>();
            foreach (var el in elements)
            {
                // ignore elements marked as do not track
                var doNotTrack = TrackedMediaElement.GetElementDoNotTrack(el);
                if (doNotTrack == true)
                {
                    el.Dispose();
                    continue;
                }
                var uid = TrackedMediaElement.GetElementUID(el);
                if (string.IsNullOrEmpty(uid) || !TrackedElements.ContainsKey(uid))
                {
                    // new element (or one tagged by a previous content-script instance)
                    var trackedElement = uid == null ? new TrackedMediaElement(this, el, JS) : new TrackedMediaElement(this, uid, el, JS);
                    trackedElement.RequestRedraw += TrackedElement_RequestRedraw;
                    uidsFound.Add(trackedElement.UID);
                    TrackedElements.Add(trackedElement.UID, trackedElement);
                    if (trackedElement.IsHTMLImageElement)
                    {
                        trackedElement.OverlayVisible = AnaglyphImagesEnabled;
                    }
                    else if (trackedElement.IsHTMLVideoElement)
                    {
                        trackedElement.OverlayVisible = AnaglyphVideosEnabled;
                    }
                    trackedElement.UpdateFrame();
                    changed = true;
                }
                else
                {
                    uidsFound.Add(uid);
                    el.Dispose();
                }
            }
            var uidsLost = TrackedElements.Keys.Except(uidsFound).ToList();
            foreach (var lostId in uidsLost)
            {
                if (TrackedElements.Remove(lostId, out var trackedElement))
                {
                    changed = true;
                    ToAnaglyph.Remove(trackedElement);
                    trackedElement.RequestRedraw -= TrackedElement_RequestRedraw;
                    trackedElement.Dispose();
                }
            }
            if (changed)
            {
                OnTrackedElementCountChanged?.Invoke();
                OnStateChanged?.Invoke();
            }
        }

        readonly List<TrackedMediaElement> ToAnaglyph = new List<TrackedMediaElement>();
        private void TrackedElement_RequestRedraw(TrackedMediaElement trackedElement, bool urgent)
        {
            var index = ToAnaglyph.IndexOf(trackedElement);
            var inQueue = index != -1;
            if (index == 0) return;
            if (!inQueue || urgent)
            {
                if (inQueue)
                {
                    ToAnaglyph.RemoveAt(index);
                }
                if (urgent)
                {
                    ToAnaglyph.Insert(0, trackedElement);
                    OnStateChanged?.Invoke();
                }
                else if (trackedElement.IsHTMLVideoElement)
                {
                    // Round-robin: a playing video re-requests after EVERY frame, so putting it at the front (the
                    // BlazorJS scheduler) starved every queued image for as long as any video played. At the back, each
                    // pending image gets one turn between video frames and the video keeps its rate once they are done.
                    ToAnaglyph.Add(trackedElement);
                    OnStateChanged?.Invoke();
                }
                else if (trackedElement.IsHTMLImageElement)
                {
                    trackedElement.SetState("queued");
                    ToAnaglyph.Add(trackedElement);
                    OnStateChanged?.Invoke();
                }
                if (ToAnaglyph.Any() && !Running)
                {
                    Running = true;
                    _ = StartRun();
                }
            }
        }

        /// <summary>Never set (unchanged from the BlazorJS build): wiring it to the render queue would keep the busy ring on for any playing video.</summary>
        public bool IsBusy { get; private set; }
        public float Progress
        {
            get
            {
                var total = CompatibleTrackedItemsCount;
                if (total == 0) return 0;
                var done = total - TotalJobsQueued;
                return (float)done * 100f / (float)total;
            }
        }
        public bool FullscreenWindowCheck(bool requireFullWidth = true, bool requireFullHeight = true)
        {
            if (Window == null) return false;
            using var screen = Window.Screen;
            if (screen == null) return false;
            return (!requireFullHeight || screen.Height == Window.InnerHeight) && (!requireFullWidth || screen.Width == Window.InnerWidth);
        }
        void BodyObserver_Observed(Array<MutationRecord> mutations, MutationObserver sender)
        {
            mutations.Dispose();
            sender.Dispose();
            ElementUpdate();
        }
        /// <inheritdoc />
        public void Dispose()
        {
            StopIt();
            if (Window != null)
            {
                Window.OnResize -= Window_OnResize;
                Window.Dispose();
                Window = null;
            }
            if (Document != null)
            {
                Document.OnFullscreenChange -= Document_OnFullscreenChange;
                Document.Dispose();
                Document = null;
            }
        }
        public int CompatibleTrackedItemsCount => CompatibleTrackedItems.Count;
        public int CompatibleTrackedVideoItemsCount => CompatibleTrackedVideoItems.Count;
        public int CompatibleTrackedImageItemsCount => CompatibleTrackedImageItems.Count;
        public List<TrackedMediaElement> CompatibleTrackedVideoItems => TrackedElements.Values.Where(o => o.MeetsMinSizeRequirements && o.IsHTMLVideoElement).ToList();
        public List<TrackedMediaElement> CompatibleTrackedImageItems => TrackedElements.Values.Where(o => o.MeetsMinSizeRequirements && o.IsHTMLImageElement).ToList();
        public List<TrackedMediaElement> CompatibleTrackedItems => TrackedElements.Values.Where(o => o.MeetsMinSizeRequirements).ToList();
        public int TotalJobsQueued => ToAnaglyph.Count + (CurrentJob == null ? 0 : 1);
        TrackedMediaElement? CurrentJob = null;
        bool Running = false;
        public void StateHasChanged() => OnStateChanged?.Invoke();
        async Task StartRun()
        {
            Running = true;
            StateHasChanged();
            try
            {
                while (ToAnaglyph.Any())
                {
                    var trackedElement = ToAnaglyph[0];
                    ToAnaglyph.RemoveAt(0);
                    CurrentJob = trackedElement;
                    await trackedElement.Redraw();
                    CurrentJob = null;
                    OnStateChanged?.Invoke();
                }
            }
            finally
            {
                CurrentJob = null;
                Running = false;
                OnStateChanged?.Invoke();
            }
        }
    }
}
