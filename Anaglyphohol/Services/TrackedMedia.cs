using Anaglyphohol.Services.Converter;
using Action = System.Action;
using Anaglyphohol.Services.Gpu;
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
        /// <summary>The shared converter's page side (see <see cref="SharedConverter"/>).</summary>
        public SharedConverterClient SharedConverterClient { get; }
        Task<SharedConverterClient?>? _shared;
        /// <summary>
        /// The shared converter when this page uses it: the storage.local setting <see cref="SharedConverter.SettingKey"/> is
        /// on AND the offscreen document was reached. Decided ONCE per page (the overlay canvases are drawn one way for the
        /// page's life); null = this page renders on its own GPU device, as always.
        /// </summary>
        public Task<SharedConverterClient?> GetSharedConverterAsync() => _shared ??= DecideSharedAsync();
        async Task<SharedConverterClient?> DecideSharedAsync()
        {
            try
            {
                var local = BrowserExtensionService.Browser?.Storage?.Local;
                if (local == null || !await local.Get<bool>(SharedConverter.SettingKey, false)) return null;
                if (!await SharedConverterClient.ConnectAsync()) return null;
                // Failed mid-session (its document closed / crashed, its GPU device lost): from then on this page renders on
                // its own GPU, and everything already shown is drawn again that way (TrackedMediaElement leaves the client).
                SharedConverterClient.OnFailed += () =>
                {
                    _shared = Task.FromResult<SharedConverterClient?>(null);
                    _ = CheckTrackedElementsDelayed();
                };
                return SharedConverterClient;
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: shared converter setting not read ({ex.Message}); this page renders itself.");
                return null;
            }
        }
        /// <summary>The stored kernel shaders (see <see cref="Gpu.ShaderCacheService"/>).</summary>
        public ShaderCacheService ShaderCache { get; }
        public DimencoHeaderService DimencoHeaderService { get; }
        public Dictionary<string, TrackedMediaElement> TrackedElements { get; } = new Dictionary<string, TrackedMediaElement>();
        public event Action? OnTrackedElementCountChanged;
        public event Action? OnStateChanged;
        bool _AnaglyphVideosEnabled = false;
        bool _AnaglyphImagesEnabled = false;
        /// <summary>Toggle icon per <see cref="ThreeDMode"/>, in mode order.</summary>
        public List<string> RendererIcons { get; } = new List<string> { "red-blue-32.png", "green-magenta-32.png", "icon-128.png" };
        /// <summary>The recommended sites (see <see cref="RecommendedSite.All"/>).</summary>
        public IReadOnlyList<RecommendedSite> RecommendedLinks => RecommendedSite.All;
        /// <summary>
        /// Whether <paramref name="host"/> is one of <see cref="RecommendedLinks"/> - the sites TJ tested. Their images and
        /// videos are on by default (the store version's rule: per-site "enabled" defaults to inSiteList, matched on the
        /// EXACT hostname); any other site starts with both off until the user switches them on.
        /// </summary>
        public bool IsRecommendedHost(string host) => RecommendedSite.IsRecommendedHost(host);
        public bool AnaglyphVideosEnabled
        {
            get => _AnaglyphVideosEnabled;
            set
            {
                if (_AnaglyphVideosEnabled == value) return;
                _AnaglyphVideosEnabled = value;
                _ = CheckTrackedElementsDelayed();
                OnStateChanged?.Invoke();
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
                OnStateChanged?.Invoke();
            }
        }
        ThreeDMode _Mode3D = ThreeDMode.RedCyan;
        // the 3.x store build's defaults (Anaglyphohol.js _level3D 0.5, _focus3D 0.66): with SepMaxPx that is its 9 px
        // separation, focus plane 2/3 of the way to near. (Dimenco: the header's factor/offset follow these, 127 / 168.)
        float _Level3D = 0.5f;
        float _Focus3D = 0.66f;

        public ThreeDMode Mode3D
        {
            get => _Mode3D;
            set
            {
                if (!Enum.IsDefined(value)) value = ThreeDMode.RedCyan;   // a stored mode from a newer build
                if (_Mode3D == value) return;
                _Mode3D = value;
                UpdateFullscreenState();
                _ = CheckTrackedElementsDelayed();
            }
        }
        /// <summary>
        /// The depth level a NEW video starts at: the level the page's main video last held for 10 s (the GPU's measured
        /// steady level here), else <see cref="DepthService.DefaultVideoLevel"/>. Twitch swaps its player's &lt;video&gt;
        /// as the carousel moves; each new one climbed from 224 again, one streaming-model reset per step.
        /// </summary>
        public int StartVideoLevel { get; set; } = DepthService.DefaultVideoLevel;
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
        bool _CheckTrackedElementsAgain = false;
        async Task CheckTrackedElementsDelayed()
        {
            // A change that arrives while a pass runs asks for one more pass: the 3D sliders apply live (every input event),
            // and the pass that was already past its delay would otherwise leave the overlays drawn with an older value.
            if (_CheckTrackedElementsDelayedRunning) { _CheckTrackedElementsAgain = true; return; }
            _CheckTrackedElementsDelayedRunning = true;
            try
            {
                do
                {
                    _CheckTrackedElementsAgain = false;
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
                        // a mode / fullscreen change can move the overlay (fullscreen Dimenco covers the whole screen)
                        el.InvalidateGeometry();
                        el.UpdateFrame();
                    }
                    UpdateDimencoHeaderVisibility();
                } while (_CheckTrackedElementsAgain);
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
        public TrackedMedia(SpawnJSRuntime js, BrowserExtensionService browserExtensionService, ThreeDRenderer threeDRenderer,
            DepthService depthService, DimencoHeaderService dimencoHeaderService, ShaderCacheService shaderCache,
            SharedConverterClient sharedConverter)
        {
            JS = js;
            SharedConverterClient = sharedConverter;
            ShaderCache = shaderCache;
            BrowserExtensionService = browserExtensionService;
            ThreeDRenderer = threeDRenderer;
            DepthService = depthService;
            DimencoHeaderService = dimencoHeaderService;
            if (JS.GlobalScope == GlobalScope.Window)
            {
                Window = JS.Get<Window>("window");
                Window.OnResize += Window_OnResize;
                Document = JS.Get<Document>("document");
                Document.OnFullscreenChange += Document_OnFullscreenChange;
            }
        }
        void Window_OnResize() => _ = CheckTrackedElementsDelayed();
        void Document_OnFullscreenChange()
        {
            UpdateFullscreenState();
            _ = CheckTrackedElementsDelayed();
        }

        TrackedMediaElement? _primaryVideo;

        /// <summary>
        /// Whether <paramref name="video"/> is the page's PRIMARY video - the one the streaming depth model (VDA) follows. Its
        /// window is ~MB per frame (~500 MB at the top level) and holds ONE clip's history, so several playing videos
        /// (Twitch's front page plays three) used to reset it on every frame. The primary is the largest playing video; it keeps
        /// the role while it plays unless another is 1.5x larger (no flapping between similar sizes). Every other video gets
        /// the per-frame model, with its own temporal filter.
        /// </summary>
        public bool IsPrimaryVideo(TrackedMediaElement video)
        {
            long now = Environment.TickCount64;
            // Playing = waiting in the GPU queue for its next frame, rendering now (the caller), or redrawn in the last 3 s.
            // NOT "redrawn in the last second" alone - MEASURED on _tools/testpage/multivideo.html: one slow frame (a VDA
            // compile at a new input size, seconds) left NO video recent, every caller made itself primary, VDA reset on
            // every frame (2-4 s each) and the page never recovered.
            bool Active(TrackedMediaElement e) => !e.IsDisposed && e.IsHTMLVideoElement && e.OverlayVisible
                && (e.AwaitingRedraw || ReferenceEquals(e, video) || now - e.LastVideoRedrawMs < 3000);
            var current = _primaryVideo;
            if (current != null && Active(current) && TrackedElements.ContainsKey(current.UID))
            {
                if (ReferenceEquals(current, video)) return true;
                if (Active(video) && video.DisplayArea > current.DisplayArea * 1.5) { _primaryVideo = video; return true; }
                return false;
            }
            TrackedMediaElement? best = null;
            foreach (var e in TrackedElements.Values)
                if (Active(e) && (best == null || e.DisplayArea > best.DisplayArea)) best = e;
            _primaryVideo = best ?? video;
            return ReferenceEquals(_primaryVideo, video);
        }

        /// <summary>Something on the page is fullscreen (document.fullscreenElement is set).</summary>
        public bool IsFullscreen { get; private set; }
        /// <summary>Dimenco 2D+Z mode while something is fullscreen: the display reads the WHOLE screen as 2D | depth.</summary>
        public bool DimencoFullscreen => IsFullscreen && Mode3D == ThreeDMode.Dimenco2DZ;
        /// <summary>
        /// Hide the Anaglyphohol toolbar (TJ 2026-10-03): in Dimenco fullscreen anything drawn over the 2D+Z frame is read by
        /// the display as picture / depth, and the toolbar sits at top centre ACROSS the 2D | depth boundary. Mouse movement
        /// reveals it for <see cref="UiRevealMs"/>, like a video player's own controls.
        /// </summary>
        public bool HideOverlayUi => DimencoFullscreen && !_uiRevealed;
        const long UiRevealMs = 2500;
        bool _uiRevealed, _mouseHooked;
        long _lastMouseMoveMs;

        void UpdateFullscreenState()
        {
            if (Document == null) return;
            using (var fullscreenElement = Document.FullscreenElement) IsFullscreen = fullscreenElement != null;
            // mousemove only crosses into .NET while it matters (Dimenco fullscreen), never on every page
            var want = DimencoFullscreen;
            if (want != _mouseHooked)
            {
                if (want) Document.OnMouseMove += Document_OnMouseMove;
                else Document.OnMouseMove -= Document_OnMouseMove;
                _mouseHooked = want;
            }
            _uiRevealed = false;
            OnStateChanged?.Invoke();
        }

        void Document_OnMouseMove()
        {
            _lastMouseMoveMs = Environment.TickCount64;
            if (_uiRevealed) return;
            _uiRevealed = true;
            OnStateChanged?.Invoke();
            ScheduleUiHideCheck();
        }

        void ScheduleUiHideCheck() => Window?.SetTimeout(new Action(UiHideCheck), 500d);

        void UiHideCheck()
        {
            if (!_uiRevealed) return;
            if (Environment.TickCount64 - _lastMouseMoveMs < UiRevealMs) { ScheduleUiHideCheck(); return; }
            _uiRevealed = false;
            OnStateChanged?.Invoke();
        }

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
                UpdateDimencoHeaderVisibility();
                OnTrackedElementCountChanged?.Invoke();
                OnStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// The Dimenco header follows the STATE: shown only while the mode is Dimenco 2D+Z and some tracked element shows
        /// 3D. It used to change only inside a rendered frame, so switching 3D off (no more frames) left it up - and the
        /// header switches a Dimenco display into 2D+Z for the WHOLE screen, turning a plain 2D page into broken 3D
        /// (MEASURED 2026-10-03: Global Enable off, every overlay hidden, header still display:block).
        /// </summary>
        void UpdateDimencoHeaderVisibility()
        {
            DimencoHeaderService.Show(Mode3D == ThreeDMode.Dimenco2DZ
                && TrackedElements.Values.Any(e => e.OverlayVisible && e.MeetsMinSizeRequirements));
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
                if (_mouseHooked) Document.OnMouseMove -= Document_OnMouseMove;
                _mouseHooked = false;
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
