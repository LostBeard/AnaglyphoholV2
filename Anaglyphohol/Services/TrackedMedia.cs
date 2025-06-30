using SpawnDev.BlazorJS;
using SpawnDev.BlazorJS.BrowserExtension.Services;
using SpawnDev.BlazorJS.JSObjects;
using SpawnDev.BlazorJS.MultiView;
using SpawnDev.BlazorJS.TransformersJS.DepthAnythingV2;
using Action = System.Action;
using Window = SpawnDev.BlazorJS.JSObjects.Window;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// Extension content script for tracking elements on a website
    /// </summary>
    public class TrackedMedia : IDisposable
    {
        public Document? Document { get; private set; }
        public Window? Window { get; private set; }
        public MutationObserver? BodyObserver { get; private set; }
        public BlazorJSRuntime JS;
        public BrowserExtensionService BrowserExtensionService { get; private set; }
        ContentBridgeService ContentBridge;
        public Dictionary<string, TrackedMediaElement> TrackedElements { get; } = new Dictionary<string, TrackedMediaElement>();
        public delegate void WatchedNodesUpdatedDelegate(List<string> found, List<string> lost);
        public delegate void BodyObserverObservedDelegate(Array<MutationRecord> mutations, MutationObserver sender);
        public event BodyObserverObservedDelegate OnBodyObserverObserved;
        public event Action OnTrackedElementCountChanged;
        public event Action OnStateChanged;
        public DepthAnythingService DepthAnythingService { get; private set; }
        bool _AnaglyphVideosEnabled = false;
        bool _AnaglyphImagesEnabled = false;
        public RenderAnaglyph AnaglyphRenderer { get; private set; }
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
        int _AnaglyphProfile = 0;
        float _Level3D = 0.8f;
        float _Focus3D = 0.5f;
        float _DepthScale = 0.8f;

        public int AnaglyphProfile
        {
            get => _AnaglyphProfile;
            set
            {
                if (_AnaglyphProfile == value) return;
                _AnaglyphProfile = value;
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
        public float DepthScale
        {
            get => _DepthScale;
            set
            {
                if (_DepthScale == value) return;
                _DepthScale = value;
                _ = CheckTrackedElementsDelayed();
            }
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
                //CheckTrackedElements();
                foreach (var el in TrackedElements.Values)
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
            finally
            {
                _CheckTrackedElementsDelayedRunning = false;
            }
        }
        public TrackedMedia(BlazorJSRuntime js, BrowserExtensionService browserExtensionService, ContentBridgeService contentBridgeService, DepthAnythingService depthAnythingService)
        {
            JS = js;
            DepthAnythingService = depthAnythingService;
            BrowserExtensionService = browserExtensionService;
            ContentBridge = contentBridgeService;
            AnaglyphRenderer = new RenderAnaglyph();
            if (JS.GlobalScope == GlobalScope.Window)
            {
                // Window
                Window = JS.Get<Window>("window");
                Document = JS.Get<Document>("document");
            }
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
                BodyObserver.Observe(body, new MutationObserveOptions { ChildList = true, Subtree = true });
            }
            ElementUpdate();
        }
        public void StopIt()
        {
            if (!Started) return;
            Started = false;
            if (BodyObserver != null)
            {
                BodyObserverObservedCallback?.Dispose();
                BodyObserverObservedCallback = null;
                BodyObserver.Disconnect();
                BodyObserver.Dispose();
                BodyObserver = null;
            }
        }
        void ElementUpdate()
        {
            var changed = false;
            List<HTMLElement> imageElements = Document!.QuerySelectorAll<HTMLImageElement>("img").Using(nodeList => nodeList.ToList()).ToList<HTMLElement>();
            List<HTMLElement> videoElements = Document!.QuerySelectorAll<HTMLVideoElement>("video").Using(nodeList => nodeList.ToList()).ToList<HTMLElement>();
            var elements = new List<HTMLElement>(imageElements);
            elements.AddRange(videoElements);
            var uidsFound = new List<string>();
            foreach (var el in elements)
            {
                // ignore elements marked as do not track
                var doNotTrack = TrackedMediaElement.GetElementDoNotTrack(el);
                if (doNotTrack == true) continue;
                var uid = TrackedMediaElement.GetElementUID(el);
                if (string.IsNullOrEmpty(uid))
                {
                    var trackedElement = new TrackedMediaElement(el, JS);
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
            var uidsLost = TrackedElements.Keys.Except(uidsFound);
            foreach (var lostId in uidsLost)
            {
                if (TrackedElements.TryGetValue(lostId, out var trackedElement))
                {
                    changed = true;
                    TrackedElements.Remove(lostId);
                    trackedElement.RequestRedraw -= TrackedElement_RequestRedraw;
                    trackedElement.Dispose();
                }
            }
            if (changed)
            {
                //Console.WriteLine("OnTrackedElementCountChanged");
                OnTrackedElementCountChanged?.Invoke();
                OnStateChanged?.Invoke();
            }
        }

        List<TrackedMediaElement> ToAnaglyph = new List<TrackedMediaElement>();
        private void TrackedElement_RequestRedraw(TrackedMediaElement trackedElement, bool urgent)
        {
            // the tracked element is requesting a redraw
            var isCurrentJob = trackedElement == CurrentJob;
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
                    if (isCurrentJob && ToAnaglyph.Any())
                    {
                        ToAnaglyph.Insert(1, trackedElement);
                    }
                    else
                    {
                        ToAnaglyph.Insert(0, trackedElement);
                    }
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
            OnBodyObserverObserved?.Invoke(mutations, sender);
            ElementUpdate();
        }
        /// <inheritdoc />
        public void Dispose()
        {
            StopIt();
        }
        public int CompatibleTrackedItemsCount => CompatibleTrackedItems.Count;
        public List<TrackedMediaElement> CompatibleTrackedItems => TrackedElements.Values.Where(o => o.MeetsMinSizeRequirements == true).ToList();
        public int TotalJobsQueued => ToAnaglyph.Count + (CurrentJob == null ? 0 : 1);
        TrackedMediaElement? CurrentJob = null;
        bool Running = false;
        public void StateHasChanged() => OnStateChanged?.Invoke();
        async Task StartRun()
        {
            Running = true;
            //Console.WriteLine(">> StartRun");
            StateHasChanged();
            try
            {
                while (ToAnaglyph.Any())
                {
                    var trackedElement = ToAnaglyph[0];
                    ToAnaglyph.RemoveAt(0);
                    CurrentJob = trackedElement;
                    //CurrentJob.SetState("active");
                    await trackedElement.Redraw(this);
                    CurrentJob = null;
                    OnStateChanged?.Invoke();
                }
            }
            finally
            {
                //Console.WriteLine("<< StartRun");
                CurrentJob = null;
                Running = false;
                OnStateChanged?.Invoke();
            }
        }
    }
}
