using Anaglyphohol.Services.Gpu;
using SpawnDev.ILGPU.Rendering;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using System.Diagnostics;

namespace Anaglyphohol.Services
{
    /// <summary>
    /// One tracked &lt;img&gt; or &lt;video&gt; on the page: owns the overlay canvas the 3D result is drawn on and
    /// decides when the element needs a redraw. <see cref="TrackedMedia"/> calls <see cref="Redraw"/> when it is this
    /// element's turn (frames are rendered one at a time).
    /// </summary>
    public class TrackedMediaElement : IDisposable
    {
        public ThreeDMode Mode3D { get; private set; }
        public float Level3D { get; private set; }
        public float Focus3D { get; private set; }
        /// <summary>Video depth resolution level (<see cref="DepthService.ProcessResolution"/>), driven by the measured GPU cost.</summary>
        public int DepthLevel { get; private set; } = DepthService.DefaultVideoLevel;
        public const string ElementUIDKey = "__extensionElementId";
        const string ProfileRequestAttribute = "anaglyphohol-profile-request";
        const string ProfileResultAttribute = "anaglyphohol-profile";
        string? _sweepMode;
        int _sweepLeft;
        readonly List<double> _sweepDepthMs = new();
        (long Hits, long Misses) _sweepBg;
        /// <summary>The "flicker" sweep's result: mean |delta disparity| per pixel per frame (disparity in [0,1]).</summary>
        string FlickerSummary()
        {
            var f = TrackedMedia.ThreeDRenderer.FlickerSamples;
            if (f.Count == 0) return "";
            var sorted = f.OrderBy(v => v).ToArray();
            double Q(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
            static string Stats(string label, List<double> v)
            {
                if (v.Count == 0) return $" {label}: none";
                var s = v.OrderBy(x => x).ToArray();
                return $" {label} med={s[s.Length / 2]:F4} mean={v.Average():F4} p90={s[Math.Min(s.Length - 1, (int)(0.9 * s.Length))]:F4}";
            }
            var r = TrackedMedia.ThreeDRenderer;
            return $" n={f.Count} oe={r.OneEuroMinCutoff:G3}/{r.OneEuroBeta:G3}" + Stats("SHOWN all", r.FlickerSamples) + Stats("SHOWN static", r.FlickerStaticSamples)
                + Stats("VS OTHER all", r.FlickerRawSamples) + Stats("OTHER static", r.FlickerRawStaticSamples);
        }   // WebGPUBackend.BatchBindGroupCounters at the sweep's start
        long _sweepOverflows;
        public const string DoNotTrackElementKey = "__doNotTrackElement";
        public const string OverlayCanvasKey = "overlayCanvasElement";
        public static string? GetElementUID(HTMLElement element, bool allowCreate = false)
        {
            var ret = element.JSRef!.Get<string?>(ElementUIDKey);
            if (allowCreate && string.IsNullOrEmpty(ret))
            {
                ret = Guid.NewGuid().ToString();
                SetElementUID(element, ret);
            }
            return ret;
        }
        public static bool? GetElementDoNotTrack(HTMLElement element) => element.JSRef!.Get<bool?>(DoNotTrackElementKey);
        public static void SetElementDoNotTrack(HTMLElement element, bool value) => element.JSRef!.Set(DoNotTrackElementKey, value);
        public static void RemoveElementDoNotTrack(HTMLElement element) => element.JSRef!.Delete(DoNotTrackElementKey);
        public static void SetElementUID(HTMLElement element, string uid) => element.JSRef!.Set(ElementUIDKey, uid);
        public string UID { get; private set; }
        readonly SpawnJSRuntime JS;
        public HTMLElement Element { get; private set; }
        public HTMLImageElement? ImageElement { get; private set; }
        public HTMLVideoElement? VideoElement { get; private set; }
        CSSStyleDeclaration? OverlayStyle { get; set; }
        public int MinWidth { get; set; } = 100;
        public int MinHeight { get; set; } = 100;
        /// <summary>
        /// Returns true if the image loading is complete and it meets the minimum size requirements
        /// </summary>
        public bool MeetsMinSizeRequirements
        {
            get
            {
                if (FrameWidth < MinWidth || FrameHeight < MinHeight) return false;
                using var rect = Element.GetBoundingClientRect();
                return rect.Width >= MinWidth && rect.Height >= MinHeight;
            }
        }
        public int FrameWidth => VideoElement?.VideoWidth ?? ImageElement?.NaturalWidth ?? 0;
        public int FrameHeight => VideoElement?.VideoHeight ?? ImageElement?.NaturalHeight ?? 0;
        public bool IsImageLoaded => ImageElement?.Complete == true && ImageElement.NaturalWidth > 0 && ImageElement.NaturalHeight > 0;
        public bool IsVideoLoaded => VideoElement != null && VideoElement.ReadyState >= 2 && VideoElement.VideoWidth > 0 && VideoElement.VideoHeight > 0;
        public bool IsHTMLImageElement => TagName == "IMG";
        public bool IsHTMLVideoElement => TagName == "VIDEO";
        public string TagName { get; }
        readonly Window window;
        readonly Document document;
        public const string StateAttributeName = "anaglyphohol-state";
        string State = "";
        HTMLCanvasElement? OverlayCanvasElement { get; set; }
        ICanvasRenderer? OverlayRenderer { get; set; }
        readonly HTMLElement parent;
        static bool? supportsWindowRequestAnimationFrame = null;
        static bool? supportsRequestVideoFrameCallback = null;
        /// <summary>Persistent frame callback: requestVideoFrameCallback / requestAnimationFrame reuse it every frame.</summary>
        ActionCallback? _frameCallback;
        readonly TrackedMedia TrackedMedia;
        /// <summary>
        /// A CORS-clean copy of a cross-origin image (loaded with crossOrigin set), when the page's own element is
        /// tainted. Reset when the element loads a new image.
        /// </summary>
        HTMLImageElement? _usableImage;
        bool _imageTainted;

        public TrackedMediaElement(TrackedMedia trackedMedia, string uid, HTMLElement htmlElement, SpawnJSRuntime js)
        {
            TrackedMedia = trackedMedia;
            UID = uid;
            JS = js;
            TagName = htmlElement.TagName.ToUpperInvariant();
            window = JS.Get<Window>("window")!;
            document = JS.Get<Document>("document")!;
            parent = htmlElement.ParentElementAs<HTMLElement>()!;
            switch (TagName)
            {
                case "IMG":
                    ImageElement = htmlElement.JSRefAs<HTMLImageElement>();
                    Element = ImageElement;
                    ImageElement.OnLoad += ImageElement_OnLoad;
                    break;
                case "VIDEO":
                    VideoElement = htmlElement.JSRefAs<HTMLVideoElement>();
                    Element = VideoElement;
                    supportsWindowRequestAnimationFrame ??= window.JSRef!.Has("requestAnimationFrame");
                    supportsRequestVideoFrameCallback ??= VideoElement.SupportsRequestVideoFrameCallback;
                    VideoElement.OnLoadedData += VideoElement_OnLoadedData;
                    _frameCallback = new ActionCallback(UpdateFrame);
                    break;
                default:
                    Element = htmlElement;
                    break;
            }
            Element.OnMouseEnter += Element_OnMouseEnter;
        }
        public TrackedMediaElement(TrackedMedia trackedMedia, HTMLElement element, SpawnJSRuntime js) : this(trackedMedia, GetElementUID(element, true)!, element, js) { }

        void Element_OnMouseEnter()
        {
            UpdateFrame(true);
        }
        public bool AwaitingRedraw { get; private set; } = false;
        public double RedrawTime { get; private set; }
        /// <summary>Cost of the last rendered frame.</summary>
        public FrameStats? LastFrame { get; private set; }

        /// <summary>
        /// TrackedMedia calls this when it is this element's turn to use the GPU.
        /// </summary>
        public async Task Redraw()
        {
            if (IsDisposed || !AwaitingRedraw) return;
            AwaitingRedraw = false;
            // hidden since it was queued (3D switched off): no GPU work for a picture nobody sees
            if (OverlayCanvasElement == null || !OverlayVisible) return;
            bool rendered = false;   // a depth + 3D frame was produced by THIS redraw (not a failed / idle pass)
            try
            {
                Mode3D = TrackedMedia.Mode3D;
                Level3D = TrackedMedia.Level3D;
                Focus3D = TrackedMedia.Focus3D;
                OverlayRenderer ??= await TrackedMedia.ThreeDRenderer.CreateCanvasRendererAsync(OverlayCanvasElement);
                if (ImageElement != null && IsImageLoaded)
                {
                    SetState("active");
                    if (await RenderImage())
                    {
                        rendered = true;
                        UpdateDimencoHeader();
                        SetState("anaglyph");
                    }
                    else
                    {
                        SetState("failed");
                    }
                }
                else if (VideoElement != null && IsVideoLoaded)
                {
                    // DIAGNOSTIC (Stats mode only): _tools/profile-frame.js sets anaglyphohol-profile-request ("basic" or "ops")
                    // and reads the one-frame breakdown back from anaglyphohol-profile.
                    // "basic" / "ops" profile ONE frame with counters; "plain:N" / "noexec:N" / "nodispatch:N" time N frames
                    // with NO profiling marks. noexec = GraphExecutor.DiagSkipOperatorExecute (executor bookkeeping only);
                    // nodispatch = WebGPUAccelerator.DiagSkipRunKernel (everything but the per-dispatch WebGPU work);
                    // stop1/2/3 = WebGPUAccelerator.DiagRunKernelStopAfter (RunKernel ends after that stage).
                    // Both render garbage for those frames.
                    FrameProfiler? profiler = null;
                    if (TrackedMedia.DrawStats && Element.GetAttribute(ProfileRequestAttribute) is string request)
                    {
                        Element.RemoveAttribute(ProfileRequestAttribute);
                        var parts = request.Split(':');
                        if (parts[0] is "plain" or "noexec" or "nodispatch" or "stop1" or "stop2" or "stop3" or "stop4" or "stop5" or "stop6" or "stop7" or "stop8" or "stop9" or "multipass" or "jsnodispatch" or "jsnosubmit" or "noarena" or "noreuse" or "lifopool" or "flicker" or "rawrange" or "nofilter" or "rollwin")
                        {
                            _sweepMode = parts[0];
                            _sweepLeft = parts.Length > 1 && int.TryParse(parts[1], out var n) && n > 0 ? n : 20;
                            // Optional One Euro tuning for the sweep: mode:N:minCutoff:beta (cycles per frame). Kept after the sweep.
                            var inv = System.Globalization.CultureInfo.InvariantCulture;
                            if (parts.Length > 2 && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out var mc))
                                TrackedMedia.ThreeDRenderer.OneEuroMinCutoff = mc;
                            if (parts.Length > 3 && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, inv, out var be))
                                TrackedMedia.ThreeDRenderer.OneEuroBeta = be;
                            _sweepDepthMs.Clear();
                            _sweepBg = SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchBindGroupCounters;
                            TrackedMedia.ThreeDRenderer.FlickerSamples.Clear();
                            TrackedMedia.ThreeDRenderer.FlickerStaticSamples.Clear();
                            TrackedMedia.ThreeDRenderer.FlickerRawSamples.Clear();
                            TrackedMedia.ThreeDRenderer.FlickerRawStaticSamples.Clear();
                            _sweepOverflows = SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.ScalarArenaOverflows;
                        }
                        else profiler = new FrameProfiler(ops: parts[0] == "ops");
                    }
                    bool sweeping = _sweepMode != null && _sweepLeft > 0;
                    SpawnDev.ILGPU.ML.Graph.GraphExecutor.DiagSkipOperatorExecute = sweeping && _sweepMode == "noexec";
                    SpawnDev.ILGPU.WebGPU.WebGPUAccelerator.DiagSkipRunKernel = sweeping && _sweepMode == "nodispatch";
                    // multipass = the A/B arm of WebGPUBackend.BatchSinglePass (a compute pass per dispatch, as before)
                    SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchSinglePass = !(sweeping && _sweepMode == "multipass");
                    // noarena = the A/B arm of WebGPUBackend.BatchScalarArena + BatchBindGroupReuse (pooled scalar buffers,
                    // a createBindGroup per dispatch, as before); noreuse = the arena without the bind-group cache.
                    SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchScalarArena = !(sweeping && _sweepMode == "noarena");
                    SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchBindGroupReuse = !(sweeping && _sweepMode is "noarena" or "noreuse");
                    // lifopool = the A/B arm of BufferPool.DeterministicReuse (the old last-returned-first order). Sweep it LAST:
                    // switching back leaves the free lists only partly sorted until they turn over.
                    SpawnDev.ILGPU.ML.Tensors.BufferPool.DeterministicReuse = !(sweeping && _sweepMode == "lifopool");
                    // flicker = measure the displayed disparity's frame-to-frame change (ThreeDKernels.FlickerKernel).
                    // rawrange = the A/B arm of ThreeDRenderer.SmoothDepthRange (each frame's own min/max), WITH the flicker probe.
                    TrackedMedia.ThreeDRenderer.FlickerProbe = sweeping && _sweepMode is "flicker" or "rawrange" or "nofilter" or "rollwin";
                    // rollwin = the DepthRollingWindow port instead of the default One Euro filter.
                    TrackedMedia.ThreeDRenderer.FilterKind = sweeping && _sweepMode == "rollwin" ? Gpu.TemporalFilterKind.RollingWindow : Gpu.TemporalFilterKind.OneEuro;
                    // nofilter = the A/B arm of ThreeDRenderer.TemporalFilter (the paired probe then compares shown vs raw range).
                    TrackedMedia.ThreeDRenderer.TemporalFilter = !(sweeping && _sweepMode == "nofilter");
                    TrackedMedia.ThreeDRenderer.SmoothDepthRange = !(sweeping && _sweepMode == "rawrange");
                    SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.DiagSubmitAblation = !sweeping ? 0
                        : _sweepMode switch { "jsnodispatch" => 2, "jsnosubmit" => 3, _ => 0 };   // (1 = skip writes HUNG the GPU: kernels ran on stale scalars)
                    SpawnDev.ILGPU.WebGPU.WebGPUAccelerator.DiagRunKernelStopAfter = !sweeping ? 0
                        : _sweepMode switch { "stop1" => 1, "stop2" => 2, "stop3" => 3, "stop4" => 4, "stop5" => 5, "stop6" => 6, "stop7" => 7, "stop8" => 8, "stop9" => 9, _ => 0 };
                    try
                    {
                        // A sweep PINS the floor level: comparing builds or switches at the cost loop's chosen level
                        // compared different input sizes (AOT sat at 224x126 while the interpreter fell to 168x98).
                        LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(VideoElement, FrameWidth, FrameHeight, OverlayRenderer,
                            Mode3D, Level3D, Focus3D, video: true, sweeping ? 0 : DepthLevel, profiler);
                    }
                    finally
                    {
                        SpawnDev.ILGPU.ML.Graph.GraphExecutor.DiagSkipOperatorExecute = false;
                        SpawnDev.ILGPU.WebGPU.WebGPUAccelerator.DiagSkipRunKernel = false;
                        SpawnDev.ILGPU.WebGPU.WebGPUAccelerator.DiagRunKernelStopAfter = 0;
                        SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchSinglePass = true;
                        SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchScalarArena = true;
                        SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchBindGroupReuse = true;
                        SpawnDev.ILGPU.ML.Tensors.BufferPool.DeterministicReuse = true;
                        TrackedMedia.ThreeDRenderer.SmoothDepthRange = true;
                        TrackedMedia.ThreeDRenderer.TemporalFilter = true;
                        SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.DiagSubmitAblation = 0;
                    }
                    rendered = true;
                    if (profiler != null) Element.SetAttribute(ProfileResultAttribute, profiler.Finish());
                    if (sweeping && LastFrame.Value.RecompileMs == 0)
                    {
                        _sweepDepthMs.Add(LastFrame.Value.DepthMs);
                        if (--_sweepLeft == 0)
                        {
                            var sorted = _sweepDepthMs.OrderBy(v => v).ToArray();
                            double Q(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
                            var bg = SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.BatchBindGroupCounters;
                            Element.SetAttribute(ProfileResultAttribute,
                                $"SWEEP {_sweepMode} n={sorted.Length} input={LastFrame.Value.DepthWidth}x{LastFrame.Value.DepthHeight} depth median={Q(0.5):F1}ms p10={Q(0.1):F1} p90={Q(0.9):F1} min={sorted[0]:F1}"
                                + $" bg hits={bg.Hits - _sweepBg.Hits} misses={bg.Misses - _sweepBg.Misses} arena overflows={SpawnDev.ILGPU.WebGPU.Backend.WebGPUBackend.ScalarArenaOverflows - _sweepOverflows}"
                                + FlickerSummary());
                            _sweepMode = null;
                        }
                    }
                    // A frame that compiled a new input shape also paid its first forward: a one-off, not the level's
                    // steady cost. Counting it stepped the level down after every step up, onto yet another new shape.
                    if (LastFrame.Value.RecompileMs == 0)
                    {
                        costThisSecondMs += LastFrame.Value.DepthMs + LastFrame.Value.RenderMs;
                        costFramesThisSecond++;
                    }
                    UpdateDimencoHeader();
                    if (TrackedMedia.DrawStats) DrawTextLines(StatsLines());
                }
                else
                {
                    SetState("");
                }
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: redraw failed: {ex.Message}");
                SetState("failed");
            }
            framesThisSecond++;
            if (rendered) renderedFrames++;
            if (rendered && TrackedMedia.DrawStats && LastFrame is FrameStats cost)
            {
                // Stats mode: THIS frame's cost on the element itself, readable by page-world tooling (_tools/)
                // - the content script's own objects live in an isolated world CDP page evaluation cannot see.
                // Written only when a frame was rendered, so a timing window never counts a stale copy as a frame.
                try { Element.SetAttribute("anaglyphohol-cost", $"seq={renderedFrames} depth={cost.DepthMs:0.0}ms recompile={cost.RecompileMs:0.0}ms 3d={cost.RenderMs:0.0}ms input={cost.DepthWidth}x{cost.DepthHeight} frame={cost.Width}x{cost.Height}"); } catch { }
            }
            var elapsedSeconds = waitTime.Elapsed.TotalSeconds;
            if (elapsedSeconds >= 1d)
            {
                checkFrameSize = true;
                waitTime.Restart();
                var fps = framesThisSecond / elapsedSeconds;
                var pad = 2d;
                FPS = (FPS * pad + fps) / (pad + 1);
                framesThisSecond = 0;
                RedrawTime = 1000d / FPS;
                if (IsHTMLVideoElement && costFramesThisSecond > 0)
                {
                    // Adapt the depth resolution to the measured GPU cost per frame, not to the frame rate: a video
                    // can never render faster than its own frame rate, so the old FPS rule ("raise above 28 FPS") could
                    // only ever LOWER the resolution on a 30 fps source - it sank to the floor and stayed there.
                    var costMs = costThisSecondMs / costFramesThisSecond;
                    AverageFrameCostMs = costMs;
                    if (costMs > FrameBudgetMs * 0.85 && DepthLevel > 0)
                    {
                        DepthLevel--;
                    }
                    else if (costMs < FrameBudgetMs * 0.6 && DepthLevel < DepthService.VideoLevels - 1)
                    {
                        DepthLevel++;
                    }
                }
                costThisSecondMs = 0;
                costFramesThisSecond = 0;
            }
            if (IsHTMLVideoElement && OverlayVisible)
            {
                // a video requests its next frame after every draw
                RequestVideoFrameCallback();
            }
        }

        /// <summary>
        /// Renders the image: the page's own element first; if its pixels are tainted (cross-origin without CORS), a
        /// copy reloaded with crossOrigin "anonymous", then "use-credentials" (what the page itself shows only when the
        /// server allows it). Returns false when no usable copy exists.
        /// </summary>
        async Task<bool> RenderImage()
        {
            int w = FrameWidth, h = FrameHeight;
            if (!_imageTainted)
            {
                try
                {
                    LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(ImageElement!, w, h, OverlayRenderer!, Mode3D, Level3D, Focus3D, video: false, videoLevel: 0);
                    return true;
                }
                catch (Exception ex) when (IsTaintedError(ex))
                {
                    _imageTainted = true;
                }
            }
            _usableImage ??= await LoadCorsCopy(w, h);
            if (_usableImage == null) return false;
            LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(_usableImage, w, h, OverlayRenderer!, Mode3D, Level3D, Focus3D, video: false, videoLevel: 0);
            return true;
        }

        static bool IsTaintedError(Exception ex)
        {
            var m = ex.Message;
            return m.Contains("SecurityError", StringComparison.OrdinalIgnoreCase)
                || m.Contains("tainted", StringComparison.OrdinalIgnoreCase)
                || m.Contains("cross-origin", StringComparison.OrdinalIgnoreCase);
        }

        async Task<HTMLImageElement?> LoadCorsCopy(int width, int height)
        {
            var src = ImageElement?.CurrentSrc;
            if (string.IsNullOrEmpty(src)) return null;
            foreach (var mode in new[] { "anonymous", "use-credentials" })
            {
                try
                {
                    var copy = await HTMLImageElement.CreateFromImageAsync(src, mode);
                    if (copy.NaturalWidth == width && copy.NaturalHeight == height) return copy;
                    copy.Dispose();
                }
                catch { }   // the server refused CORS for this mode
            }
            return null;
        }

        void UpdateDimencoHeader()
        {
            var header = TrackedMedia.DimencoHeaderService;
            if (Mode3D == ThreeDMode.Dimenco2DZ)
            {
                // Level3D -> depth factor, Focus3D -> depth offset (MultiView.Dimenco RenderDimenco2DZ.ApplyEffect)
                header.Header.Factor = (byte)Math.Clamp(Level3D * 255d, 0, 255);
                header.Header.Offset = (byte)Math.Clamp(Focus3D * 255d, 0, 255);
                header.Show(true);
            }
            else
            {
                header.Show(false);
            }
        }

        List<string> StatsLines()
        {
            var lines = new List<string>();
            if (TrackedMedia.DrawStats && LastFrame is FrameStats f)
            {
                lines.Add($"FPS: {Math.Round(FPS)}");
                lines.Add($"Video: {f.Width}x{f.Height}");
                lines.Add($"Depth: {f.DepthWidth}x{f.DepthHeight} (level {DepthLevel + 1}/{DepthService.VideoLevels}) {TrackedMedia.DepthModel}");
                lines.Add($"GPU: depth {f.DepthMs:0.0} ms, 3D {f.RenderMs:0.0} ms (avg {AverageFrameCostMs:0.0} / {FrameBudgetMs:0} ms)");
            }
            return lines;
        }

        void DrawTextLines(List<string> lines)
        {
            if (lines.Count == 0 || OverlayCanvasElement == null) return;
            // The canvas renderer presents through the canvas's 2d context, so text drawn now lands on top of the frame.
            using var ctx = OverlayCanvasElement.Get2DContext();
            var fontSize = 20;
            var y = fontSize;
            var x = fontSize;
            var boxBorderSize = 2;
            var textHeight = fontSize;
            ctx.Font = $"{fontSize}px serif";
            ctx.FillStyle = "#ffffff60";
            var longestLine = lines.OrderByDescending(l => l.Length).First();
            using var textSize = ctx.MeasureText(longestLine);
            var boxWidth = (int)Math.Round(textSize.Width + boxBorderSize * 2);
            var boxHeight = textHeight * lines.Count + boxBorderSize * 2;
            ctx.FillRect(x, y, boxWidth, boxHeight);
            ctx.FillStyle = "#000";
            foreach (var line in lines)
            {
                ctx.FillText(line, x + boxBorderSize, y + textHeight - boxBorderSize);
                y += textHeight;
            }
        }

        int framesThisSecond = 0;
        long renderedFrames = 0;
        double costThisSecondMs = 0;
        int costFramesThisSecond = 0;
        /// <summary>
        /// GPU time one video frame may take (depth + 3D): the frame interval of 30 fps video. The depth resolution
        /// steps down above 85% of it and up below 60% (the gap is the hysteresis that keeps it from oscillating).
        /// </summary>
        public double FrameBudgetMs { get; set; } = 1000d / 30d;
        /// <summary>Mean GPU cost per video frame over the last second (ms), for the stats overlay.</summary>
        public double AverageFrameCostMs { get; private set; }
        public double FPS { get; private set; }
        readonly Stopwatch waitTime = new Stopwatch();
        public void UpdateFrame()
        {
            UpdateFrame(false);
        }
        bool checkFrameSize = true;
        /// <summary>
        /// Queues a redraw with TrackedMedia. Videos are drawn as fast as the GPU allows; images when they load, resize or
        /// are hovered.
        /// </summary>
        public void UpdateFrame(bool urgent)
        {
            if (IsDisposed) return;
            // 3D is off for this element's kind (images / videos) on this site: queue nothing. The BlazorJS build
            // rendered - depth model and all - every image on every page even with 3D disabled, and drew the
            // state borders around them. OverlayVisible's setter calls UpdateFrame when 3D is switched on.
            if (!OverlayVisible) return;
            if (!urgent && AwaitingRedraw) return;
            if (!MeetsMinSizeRequirements) return;
            UpdateCanvasOverlayPositionAndSize(true, checkFrameSize);
            if (OverlayCanvasElement == null) return;
            checkFrameSize = false;
            AwaitingRedraw = true;
            RequestRedraw?.Invoke(this, urgent);
        }
        public bool SetState(string state)
        {
            if (State == state) return true;
            State = state;
            try
            {
                Element.SetAttribute(StateAttributeName, state);
                return true;
            }
            catch { }
            return false;
        }
        bool UpdateCanvasOverlayPositionAndSize(bool allowCreate = false, bool updateExisting = false)
        {
            var created = false;
            if (OverlayCanvasElement == null)
            {
                // the overlay may already exist (element re-tracked after a mutation)
                OverlayCanvasElement = Element.JSRef!.Get<HTMLCanvasElement?>(OverlayCanvasKey);
                if (OverlayCanvasElement == null)
                {
                    if (!allowCreate) return false;
                    OverlayCanvasElement = document.CreateElement<HTMLCanvasElement>("canvas");
                    using (var parentStyle = parent.Style) parentStyle["position"] = "relative";
                    OverlayCanvasElement.SetAttribute("style", "position: absolute; pointer-events: none;");
                    OverlayCanvasElement.SetAttribute("class", "custom-media-overlay-canvas");
                    Element.JSRef!.Set(OverlayCanvasKey, OverlayCanvasElement);
                    Element.After(OverlayCanvasElement);
                    created = true;
                }
            }
            OverlayStyle ??= OverlayCanvasElement.Style;
            if (!created && !updateExisting) return true;
            using var vRect = Element.GetBoundingClientRect();
            using var vStyle = window.GetComputedStyle(Element);
            int frameWidth = FrameWidth;
            int frameHeight = FrameHeight;
            var width = (int)Math.Round(vRect.Width) + "px";
            var height = (int)Math.Round(vRect.Height) + "px";
            // the only accurate offset found: the element's bounding rect minus its parent's
            using var parentRect = parent.GetBoundingClientRect();
            var top = (vRect.Top - parentRect.Top) + "px";
            var left = (vRect.Left - parentRect.Left) + "px";
            // ⚠️ CSSStyleDeclaration's indexer is getPropertyValue/setProperty: property names MUST be kebab-case.
            // camelCase ("objectFit") silently sets nothing.
            OverlayStyle["aspect-ratio"] = $"{vRect.Width} / {vRect.Height}";
            var zIndex = vStyle["z-index"] ?? "";
            zIndex = !float.TryParse(zIndex, out var zIndexFloat) ? zIndex : (zIndexFloat + 1).ToString();
            var display = _OverlayVisible ? "" : "none";
            var vObjectFit = vStyle["object-fit"];
            vObjectFit = string.IsNullOrEmpty(vObjectFit) ? "contain" : vObjectFit;
            SetStyle("object-fit", vObjectFit);
            SetStyle("display", display);
            SetStyle("top", top);
            SetStyle("left", left);
            SetStyle("z-index", zIndex);
            SetStyle("width", width);
            SetStyle("height", height);
            if (OverlayCanvasElement.Width != frameWidth) OverlayCanvasElement.Width = frameWidth;
            if (OverlayCanvasElement.Height != frameHeight) OverlayCanvasElement.Height = frameHeight;
            return true;
        }
        void SetStyle(string name, string value)
        {
            if (OverlayStyle![name] != value) OverlayStyle[name] = value;
        }
        public event Action<TrackedMediaElement, bool> RequestRedraw = default!;
        void RequestVideoFrameCallback()
        {
            if (IsDisposed || _frameCallback == null) return;
            if (supportsRequestVideoFrameCallback == true && VideoElement != null)
            {
                VideoElement.RequestVideoFrameCallback(_frameCallback);
            }
            else if (supportsWindowRequestAnimationFrame == true)
            {
                window.RequestAnimationFrame(_frameCallback);
            }
            else
            {
                window.SetTimeout(new Action(UpdateFrame), 1000d / 30d); // 30 FPS when neither frame callback exists
            }
        }
        void DetachElementEvents()
        {
            if (ImageElement != null)
            {
                ImageElement.OnLoad -= ImageElement_OnLoad;
            }
            else if (VideoElement != null)
            {
                VideoElement.OnLoadedData -= VideoElement_OnLoadedData;
            }
            Element.OnMouseEnter -= Element_OnMouseEnter;
        }
        public bool IsDisposed { get; private set; } = false;
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            DetachElementEvents();
            // A queued requestVideoFrameCallback may still fire once: it checks IsDisposed before touching anything.
            _frameCallback?.Dispose();
            _frameCallback = null;
            OverlayRenderer?.Dispose();
            OverlayRenderer = null;
            _usableImage?.Dispose();
            _usableImage = null;
            if (OverlayCanvasElement != null)
            {
                Element.JSRef?.Delete(OverlayCanvasKey);
                OverlayCanvasElement.Remove();
                OverlayCanvasElement.Dispose();
                OverlayCanvasElement = null;
            }
            OverlayStyle?.Dispose();
            OverlayStyle = null;
            Element.Dispose();
            window.Dispose();
            document.Dispose();
        }
        bool _OverlayVisible = false;
        public bool OverlayVisible
        {
            get => _OverlayVisible;
            set
            {
                if (_OverlayVisible == value) return;
                _OverlayVisible = value;
                waitTime.Restart();
                if (OverlayStyle != null)
                {
                    OverlayStyle["display"] = _OverlayVisible ? "" : "none";
                }
                if (_OverlayVisible)
                {
                    // also creates the overlay canvas for an element first seen while 3D was off
                    UpdateFrame();
                }
                else
                {
                    if (OverlayCanvasElement != null)
                    {
                        using var ctx = OverlayCanvasElement.Get2DContext();
                        ctx.ClearRect(0, 0, OverlayCanvasElement.Width, OverlayCanvasElement.Height);
                    }
                    // 3D off: no state border on the page's element either
                    SetState("");
                }
            }
        }
        void ImageElement_OnLoad()
        {
            // a new image: forget the previous one's CORS copy / taint verdict
            _usableImage?.Dispose();
            _usableImage = null;
            _imageTainted = false;
            checkFrameSize = true;
            UpdateFrame();
        }
        void VideoElement_OnLoadedData()
        {
            checkFrameSize = true;
            UpdateFrame();
        }
    }
}
