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
        /// A readable copy of a cross-origin image when the page's own element is tainted: a CORS reload (crossOrigin
        /// set) or, when the host sends no CORS, the extension background's fetch (<see cref="ImageRelay"/>). Reset when
        /// the element loads a new image.
        /// </summary>
        ImageBitmap? _usableImage;
        string? _usableImageMode;
        // the copy is tried ONCE per loaded image (each attempt is a network fetch; it used to retry on every redraw)
        bool _usableImageTried;
        bool _imageTainted;
        // The last source that rendered and how to load it again (a crossOrigin mode, or RelayLoadMode), for the
        // fallback in RenderImage; _fallbackImage is that source reloaded while the current one cannot be read.
        string? _lastGoodSrc;
        string? _lastGoodLoadMode;
        double _lastGoodAspect;
        ImageBitmap? _fallbackImage;
        /// <summary>Load mode for a source read through the extension background (<see cref="ImageRelay"/>).</summary>
        const string RelayLoadMode = "extension";
        /// <summary>Attribute on the page's element while it shows the last usable image (see RenderImage).</summary>
        public const string FallbackAttributeName = "anaglyphohol-fallback";

        public TrackedMediaElement(TrackedMedia trackedMedia, string uid, HTMLElement htmlElement, SpawnJSRuntime js)
        {
            TrackedMedia = trackedMedia;
            UID = uid;
            JS = js;
            DepthLevel = trackedMedia.StartVideoLevel;   // where the page's main video settled, not the cold default
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
                    VideoElement.OnSeeked += VideoElement_OnSeeked;
                    _frameCallback = new ActionCallback(OnFrameCallback);
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
            // A size change that arrived while this element was QUEUED (UpdateFrame returns early then) is applied
            // here, before the frame is drawn. MEASURED on Google Images, cold start, and reproduced by
            // _tools/testpage/swap.html: a thumbnail swapped to its full image while queued kept its placeholder-sized
            // canvas (350x140 for a 1400x560 image), and the full frame drawn into it showed only its top-left corner.
            // Images also re-measure on EVERY redraw (they redraw rarely): the page may have reflowed since they were queued.
            if (checkFrameSize || IsHTMLImageElement)
            {
                UpdateCanvasOverlayPositionAndSize(false, true);
                checkFrameSize = false;
            }
            bool rendered = false;   // a depth + 3D frame was produced by THIS redraw (not a failed / idle pass)
            if (VideoElement != null) LastVideoRedrawMs = Environment.TickCount64;
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
                        if (parts[0] == "vdaweights" && parts.Length > 1)
                        {
                            // DIAGNOSTIC A/B: "vdaweights:half" / "vdaweights:source" (reloads VDA on the next frame).
                            TrackedMedia.DepthService.VdaWeightStorage = parts[1] == "half"
                                ? SpawnDev.ILGPU.ML.WeightStorage.Half : SpawnDev.ILGPU.ML.WeightStorage.Source;
                            Element.SetAttribute(ProfileResultAttribute, $"VDAWEIGHTS {TrackedMedia.DepthService.VdaWeightStorage}");
                        }
                        else if (parts[0] == "model" && parts.Length > 1)
                        {
                            // DIAGNOSTIC model A/B: "model:vda" / "model:dav3" (the next frame loads it).
                            TrackedMedia.DepthModel = parts[1] == "vda" ? Gpu.DepthModelKind.VdaSmall : Gpu.DepthModelKind.DAv3Small;
                            Element.SetAttribute(ProfileResultAttribute, $"MODEL {TrackedMedia.DepthModel}");
                        }
                        else if (parts[0] is "plain" or "noexec" or "nodispatch" or "stop1" or "stop2" or "stop3" or "stop4" or "stop5" or "stop6" or "stop7" or "stop8" or "stop9" or "multipass" or "jsnodispatch" or "jsnosubmit" or "noarena" or "noreuse" or "lifopool" or "flicker" or "rawrange" or "nofilter" or "rollwin")
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
                            Mode3D, Level3D, Focus3D, video: true, sweeping ? 0 : DepthLevel, profiler, videoOwner: this,
                            primaryVideo: _isPrimaryVideo = TrackedMedia.IsPrimaryVideo(this),
                            dimencoScreen: Mode3D == ThreeDMode.Dimenco2DZ ? _dimencoScreen : null);
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
                // Logged once per distinct error, not per frame: a video that cannot be read fails on EVERY frame.
                if (ex.Message != _lastFailure)
                {
                    _lastFailure = ex.Message;
                    var hint = ex.Message.Contains("back resource", StringComparison.OrdinalIgnoreCase)
                        ? " (a protected/DRM video: the browser does not let pages or extensions read its frames)" : "";
                    // the first frames of the stack too: a bare message ("Arg_NullReferenceException" in a trimmed build) says
                    // nothing about where (MEASURED 2026-10-05 on Firefox)
                    var where = string.Join(" <- ", (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(4).Select(l => l.Trim()));
                    JS.Log($"Anaglyphohol: redraw failed: {ex.Message}{hint}{(where.Length > 0 ? $" [{where}]" : "")}");
                }
                _consecutiveFailures++;
                SetState("failed");
            }
            if (rendered)
            {
                _consecutiveFailures = 0;
                _lastFailure = null;
                TrackedMedia.ShaderCache.FrameRendered();   // stores any kernel shader this page had to compile
            }
            framesThisSecond++;
            if (rendered) renderedFrames++;
            if (rendered && TrackedMedia.DrawStats && LastFrame is FrameStats cost)
            {
                // Stats mode: THIS frame's cost on the element itself, readable by page-world tooling (_tools/)
                // - the content script's own objects live in an isolated world CDP page evaluation cannot see.
                // Written only when a frame was rendered, so a timing window never counts a stale copy as a frame.
                try { Element.SetAttribute("anaglyphohol-cost", $"seq={renderedFrames} depth={cost.DepthMs:0.0}ms recompile={cost.RecompileMs:0.0}ms 3d={cost.RenderMs:0.0}ms input={cost.DepthWidth}x{cost.DepthHeight} frame={cost.Width}x{cost.Height} model={cost.Model} stream={cost.StreamFrame}"); } catch { }
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
                    // A level change is a new input SIZE: a streaming model (VDA) restarts its window, any model compiles
                    // or reuses another shape. MEASURED (3 videos sharing the GPU): the level oscillated 392 <-> 448 <-> 504
                    // and every step reset VDA's history (5 resets in 20 s). So: step DOWN only after 2 seconds over
                    // budget; and a level that just proved too expensive is a CEILING for 30 s - stepping back up to it is
                    // what made the oscillation. Climbing below the ceiling stays fast (1 s per step at startup).
                    long nowMs = Environment.TickCount64;
                    if (costMs > FrameBudgetMs * 0.85) { _overBudgetSeconds++; _underBudgetSeconds = 0; }
                    else if (costMs < FrameBudgetMs * 0.6) { _underBudgetSeconds++; _overBudgetSeconds = 0; }
                    else { _overBudgetSeconds = 0; _underBudgetSeconds = 0; }
                    bool ceilingActive = nowMs < _levelCeilingUntilMs;
                    if (_overBudgetSeconds >= 2 && DepthLevel > 0)
                    {
                        // no ceiling from the STARTUP seconds: cold frames (first compiles, a second model loading) run
                        // slow once. MEASURED on twitch.tv: a startup step-down capped the level for 30 s, and the video
                        // then climbed from 168 for ~50 s.
                        if (_measuredSeconds > StartupSeconds)
                        {
                            _levelCeiling = DepthLevel;
                            _levelCeilingUntilMs = nowMs + 30000;
                        }
                        DepthLevel--;
                        _overBudgetSeconds = 0;
                    }
                    // A STREAMING model loses its history at every step, so once it has some (past the startup climb) it
                    // steps UP only after 5 s of headroom. MEASURED on twitch.tv: 1 s per step = 7 VDA resets in 30 s.
                    // Stepping DOWN stays at 2 s: a real overload must still drop.
                    else if (_underBudgetSeconds >= (DepthService.IsStreaming(LastFrame.Value.Model) && _measuredSeconds > StartupSeconds ? 5 : 1)
                        && DepthLevel < DepthService.VideoLevels - 1
                        && !(ceilingActive && DepthLevel + 1 >= _levelCeiling))
                    {
                        DepthLevel++;
                        _underBudgetSeconds = 0;
                    }
                    _measuredSeconds++;
                    if (DepthLevel != _lastLevel) { _lastLevel = DepthLevel; _secondsAtLevel = 0; }
                    else if (++_secondsAtLevel >= 10 && _isPrimaryVideo) TrackedMedia.StartVideoLevel = DepthLevel;
                }
                costThisSecondMs = 0;
                costFramesThisSecond = 0;
            }
            // stamped at the END too: a slow frame (a cold model load takes seconds) must not make this video look idle
            if (VideoElement != null) LastVideoRedrawMs = Environment.TickCount64;
            if (IsHTMLVideoElement && OverlayVisible)
            {
                // A video requests its next frame after every draw. One that keeps failing (MEASURED on pluto.tv: a
                // DRM stream, copyExternalImageToTexture refused on every frame) retries once a second instead, so it
                // still recovers on its own (the stream switches to unprotected content, the GPU device comes back).
                if (_consecutiveFailures >= FailuresBeforeBackoff) window.SetTimeout(new Action(UpdateFrame), 1000d);
                else RequestVideoFrameCallback();
            }
        }
        const int FailuresBeforeBackoff = 3;
        int _consecutiveFailures;
        string? _lastFailure;

        /// <summary>
        /// Renders the image: the page's own element first; if its pixels are tainted (cross-origin without CORS), a
        /// copy reloaded with crossOrigin "anonymous", then "use-credentials" (what the page itself shows only when the
        /// server allows it), then the extension background's fetch (<see cref="ImageRelay"/>: hosts that send no CORS).
        /// When none can be read, the LAST image that rendered here, if it has the same aspect ratio. Returns false when
        /// nothing usable exists.
        /// </summary>
        /// <remarks>
        /// The fallback is the store version's (vjs/anglyphoholv3 3.0.11: "Images can now fallback to the last valid
        /// usable image (fixes some search engine result images)"): a search engine's image preview shows a readable
        /// thumbnail first, then swaps in the full-size original from a host that sends no CORS headers. The background
        /// fetch now reads most of those originals; the fallback still covers a host that refuses it too (hotlink
        /// protection). Same aspect ratio within 3% only: a carousel that swaps in a DIFFERENT picture must not show the
        /// old one.
        /// </remarks>
        async Task<bool> RenderImage()
        {
            int w = FrameWidth, h = FrameHeight;
            if (!_imageTainted)
            {
                try
                {
                    LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(ImageElement!, w, h, OverlayRenderer!, Mode3D, Level3D, Focus3D, video: false, videoLevel: 0);
                    RememberGoodSource(ImageElement!.CurrentSrc, ImageElement.CrossOrigin, w, h);
                    return true;
                }
                catch (Exception ex) when (IsTaintedError(ex))
                {
                    _imageTainted = true;
                }
            }
            var src = ImageElement?.CurrentSrc;
            if (_usableImage == null && !_usableImageTried)
            {
                _usableImageTried = true;
                (_usableImage, _usableImageMode) = await LoadReadableCopy(src, w, h);
            }
            if (_usableImage != null)
            {
                LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(_usableImage, w, h, OverlayRenderer!, Mode3D, Level3D, Focus3D, video: false, videoLevel: 0);
                RememberGoodSource(src, _usableImageMode, w, h);
                return true;
            }
            // nothing readable: the last image that rendered here, if it is the same picture's shape
            if (_lastGoodSrc == null || w <= 0 || h <= 0 || Math.Abs((double)w / h - _lastGoodAspect) > _lastGoodAspect * 0.03)
            {
                // a different picture: no fallback (and none still marked from an earlier swap)
                ReleaseBitmap(ref _fallbackImage);
                try { Element.RemoveAttribute(FallbackAttributeName); } catch { }
                return false;
            }
            _fallbackImage ??= await LoadReadable(_lastGoodSrc, _lastGoodLoadMode);
            if (_fallbackImage == null) return false;   // the old source is gone too (a revoked blob URL)
            LastFrame = await TrackedMedia.ThreeDRenderer.RenderAsync(_fallbackImage, (int)_fallbackImage.Width, (int)_fallbackImage.Height,
                OverlayRenderer!, Mode3D, Level3D, Focus3D, video: false, videoLevel: 0);
            try { Element.SetAttribute(FallbackAttributeName, "1"); } catch { }
            return true;
        }

        /// <summary>A source just rendered: it is the fallback from now on, and any fallback in use is dropped.</summary>
        void RememberGoodSource(string? src, string? loadMode, int width, int height)
        {
            if (!string.IsNullOrEmpty(src) && width > 0 && height > 0)
            {
                _lastGoodSrc = src;
                _lastGoodLoadMode = loadMode;
                _lastGoodAspect = (double)width / height;
            }
            if (_fallbackImage != null)
            {
                ReleaseBitmap(ref _fallbackImage);
                try { Element.RemoveAttribute(FallbackAttributeName); } catch { }
            }
        }

        static void ReleaseBitmap(ref ImageBitmap? bitmap)
        {
            if (bitmap == null) return;
            try { bitmap.Close(); } catch { }   // frees the decoded pixels now, not at GC
            bitmap.Dispose();
            bitmap = null;
        }

        static bool IsTaintedError(Exception ex)
        {
            var m = ex.Message;
            return m.Contains("SecurityError", StringComparison.OrdinalIgnoreCase)
                || m.Contains("tainted", StringComparison.OrdinalIgnoreCase)
                || m.Contains("cross-origin", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A readable copy of <paramref name="src"/> at the element's size: CORS reloads first (the page's own request,
        /// no extension involved), then the extension background (<see cref="ImageRelay"/>, for hosts that send no CORS).
        /// </summary>
        async Task<(ImageBitmap? image, string? mode)> LoadReadableCopy(string? src, int width, int height)
        {
            if (string.IsNullOrEmpty(src)) return (null, null);
            foreach (var mode in new[] { "anonymous", "use-credentials", RelayLoadMode })
            {
                var copy = await LoadReadable(src, mode);
                if (copy == null) continue;
                if (copy.Width == width && copy.Height == height) return (copy, mode);
                ReleaseBitmap(ref copy);
            }
            return (null, null);
        }

        /// <summary>
        /// <paramref name="src"/> as a READABLE bitmap: loaded with that crossOrigin mode (null = none), or through the
        /// extension background for <see cref="RelayLoadMode"/>. Null when it cannot be read (refused, tainted, gone).
        /// </summary>
        async Task<ImageBitmap?> LoadReadable(string src, string? mode)
        {
            if (mode == RelayLoadMode) return await ImageRelay.FetchAsync(JS, TrackedMedia.BrowserExtensionService, src);
            HTMLImageElement? image = null;
            try
            {
                // with a crossOrigin mode the load itself fails unless the host allows CORS; with none (mode null) this
                // only reloads a source that already rendered from the element, so the bitmap is readable either way
                image = await HTMLImageElement.CreateFromImageAsync(src, mode);
                return await window.CreateImageBitmap(image);
            }
            catch { return null; }   // the server refused CORS for this mode, or the source is gone
            finally { image?.Dispose(); }
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
                lines.Add($"Depth: {f.DepthWidth}x{f.DepthHeight} (level {DepthLevel + 1}/{DepthService.VideoLevels}) {f.Model}");
                lines.Add($"GPU: depth {f.DepthMs:0.0} ms, 3D {f.RenderMs:0.0} ms (avg {AverageFrameCostMs:0.0} / {FrameBudgetMs:0} ms)");
            }
            return lines;
        }

        CanvasRenderingContext2D? _statsCtx;
        HTMLCanvasElement? _statsCtxCanvas;
        int _statsMeasuredLength = -1, _statsMeasuredFont, _statsMeasuredCanvasWidth, _statsFontSize = 20, _statsBoxWidth;

        void DrawTextLines(List<string> lines)
        {
            if (lines.Count == 0 || OverlayCanvasElement == null) return;
            // The canvas renderer presents through the canvas's 2d context, so text drawn now - in the same task as the
            // present, with no await between them (ThreeDRenderer.RenderAsync) - lands on top of the frame.
            if (!ReferenceEquals(_statsCtxCanvas, OverlayCanvasElement))
            {
                _statsCtx?.Dispose();
                _statsCtx = OverlayCanvasElement.Get2DContext();
                _statsCtxCanvas = OverlayCanvasElement;
                _statsMeasuredLength = -1;
            }
            var ctx = _statsCtx!;
            // 20 px AS DISPLAYED: the canvas is the frame's size (1280x720 shown 530 px wide on Twitch's front page
            // drew ~8 px text), so scale by frame px per displayed px.
            var preferredFont = (int)Math.Round(20 * Math.Max(1d, _displayWidth > 0 ? OverlayCanvasElement.Width / _displayWidth : 1d));
            var boxBorderSize = 2;
            int canvasWidth = OverlayCanvasElement.Width;
            // Measured only when the longest line's length (or the size it is drawn at) changes, not every frame (the
            // numbers change, the width barely). A box wider than the canvas shrinks the font to fit (MEASURED: on a
            // 320 px preview the stats ran past the video's right edge); the text measures linearly in font size.
            var longestLine = lines.MaxBy(l => l.Length)!;
            if (longestLine.Length != _statsMeasuredLength || preferredFont != _statsMeasuredFont || canvasWidth != _statsMeasuredCanvasWidth)
            {
                ctx.Font = $"{preferredFont}px serif";
                using var textSize = ctx.MeasureText(longestLine);
                double available = canvasWidth - 2 * preferredFont - boxBorderSize * 2;
                double scale = available > 0 && textSize.Width > available ? available / textSize.Width : 1d;
                _statsFontSize = Math.Max(6, (int)Math.Floor(preferredFont * scale));
                _statsBoxWidth = (int)Math.Ceiling(textSize.Width * _statsFontSize / preferredFont + boxBorderSize * 2);
                _statsMeasuredLength = longestLine.Length;
                _statsMeasuredFont = preferredFont;
                _statsMeasuredCanvasWidth = canvasWidth;
            }
            var fontSize = _statsFontSize;
            var y = fontSize;
            var x = fontSize;
            var textHeight = fontSize;
            ctx.Font = $"{fontSize}px serif";
            ctx.FillStyle = "#ffffff60";
            var boxWidth = _statsBoxWidth;
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
        // depth-level hysteresis (see the adaptive step in Redraw)
        int _overBudgetSeconds, _underBudgetSeconds, _levelCeiling = int.MaxValue;
        long _levelCeilingUntilMs;
        // seconds with a measured cost; the first StartupSeconds climb fast (little history to lose yet)
        int _measuredSeconds, _secondsAtLevel, _lastLevel = -1;
        const int StartupSeconds = 6;
        bool _isPrimaryVideo;
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
            // the only accurate offset found: the element's bounding rect minus its parent's
            using var parentRect = parent.GetBoundingClientRect();
            var zIndex = vStyle["z-index"] ?? "";
            zIndex = !float.TryParse(zIndex, out var zIndexFloat) ? zIndex : (zIndexFloat + 1).ToString();
            var display = _OverlayVisible ? "" : "none";
            var vObjectFit = vStyle["object-fit"];
            vObjectFit = string.IsNullOrEmpty(vObjectFit) ? "contain" : vObjectFit;
            double boxLeft, boxTop, boxWidth, boxHeight;
            int backingWidth, backingHeight;
            string canvasFit;
            _dimencoScreen = null;
            if (IsDimencoScreenMode())
            {
                // Fullscreen Dimenco: the canvas covers the whole VIEWPORT (= the screen) and the frame is composed into it
                // where the video actually shows (ThreeDKernels.TwoDZScreenKernel), so the display's halves line up whatever
                // the layout: pillarbox bars, a video that does not fill the screen (TJ 2026-10-03).
                double vw = window.InnerWidth, vh = window.InnerHeight, dpr = window.DevicePixelRatio;
                boxLeft = 0; boxTop = 0; boxWidth = vw; boxHeight = vh;
                backingWidth = Math.Max(2, (int)Math.Round(vw * dpr));
                backingHeight = Math.Max(1, (int)Math.Round(vh * dpr));
                canvasFit = "fill";
                var (cx, cy, cw, ch) = ContentRect(vRect.Left, vRect.Top, vRect.Width, vRect.Height, vObjectFit, frameWidth, frameHeight);
                _dimencoScreen = new DimencoScreen(backingWidth, backingHeight, (float)(cx * dpr), (float)(cy * dpr), (float)(cw * dpr), (float)(ch * dpr));
            }
            else
            {
                boxLeft = vRect.Left; boxTop = vRect.Top; boxWidth = vRect.Width; boxHeight = vRect.Height;
                backingWidth = frameWidth; backingHeight = frameHeight;
                canvasFit = vObjectFit;
            }
            // FRACTIONAL px: rounded (208px over a 208.5px image) left half a pixel of the element uncovered, and its
            // state border showed through as a line (MEASURED on Google Images). Invariant culture: CSS wants "208.5px".
            _displayWidth = boxWidth;
            DisplayArea = vRect.Width * vRect.Height;
            // ⚠️ CSSStyleDeclaration's indexer is getPropertyValue/setProperty: property names MUST be kebab-case.
            // camelCase ("objectFit") silently sets nothing.
            OverlayStyle["aspect-ratio"] = FormattableString.Invariant($"{boxWidth} / {boxHeight}");
            SetStyle("object-fit", canvasFit);
            SetStyle("display", display);
            SetStyle("top", Px(boxTop - parentRect.Top));
            SetStyle("left", Px(boxLeft - parentRect.Left));
            SetStyle("z-index", zIndex);
            SetStyle("width", Px(boxWidth));
            SetStyle("height", Px(boxHeight));
            if (OverlayCanvasElement.Width != backingWidth) OverlayCanvasElement.Width = backingWidth;
            if (OverlayCanvasElement.Height != backingHeight) OverlayCanvasElement.Height = backingHeight;
            return true;
        }

        /// <summary>Set when this video renders fullscreen Dimenco 2D+Z (see <see cref="IsDimencoScreenMode"/>).</summary>
        DimencoScreen? _dimencoScreen;

        /// <summary>A video in Dimenco mode inside the fullscreen element: the 2D+Z frame must be the whole screen.</summary>
        bool IsDimencoScreenMode()
        {
            if (!IsHTMLVideoElement || !TrackedMedia.DimencoFullscreen) return false;
            using var fullscreenElement = document.FullscreenElement;
            return fullscreenElement != null && fullscreenElement.Contains(Element);
        }

        /// <summary>Re-measure the overlay on the next frame (a mode or fullscreen change can move it).</summary>
        public void InvalidateGeometry() => checkFrameSize = true;

        /// <summary>
        /// Where a frame of <paramref name="frameW"/> x <paramref name="frameH"/> is drawn inside an element box under
        /// object-fit (centred: object-position is the default 50% 50% on video players).
        /// </summary>
        static (double X, double Y, double W, double H) ContentRect(double boxX, double boxY, double boxW, double boxH, string objectFit, int frameW, int frameH)
        {
            if (frameW <= 0 || frameH <= 0 || objectFit == "fill") return (boxX, boxY, boxW, boxH);
            double contain = Math.Min(boxW / frameW, boxH / frameH);
            double scale = objectFit switch
            {
                "cover" => Math.Max(boxW / frameW, boxH / frameH),
                "none" => 1d,
                "scale-down" => Math.Min(1d, contain),
                _ => contain,   // "contain", the video default
            };
            double w = frameW * scale, h = frameH * scale;
            return (boxX + (boxW - w) / 2, boxY + (boxH - h) / 2, w, h);
        }
        /// <summary>The element's displayed area (CSS px^2), for picking the page's primary video.</summary>
        public double DisplayArea { get; private set; }
        /// <summary>When this video last started or finished a redraw (Environment.TickCount64): a playing video redraws once per frame.</summary>
        public long LastVideoRedrawMs { get; private set; } = long.MinValue / 2;
        static string Px(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
        /// <summary>The overlay's displayed CSS width (px), for scaling the stats text to what the viewer sees.</summary>
        double _displayWidth;
        void SetStyle(string name, string value)
        {
            if (OverlayStyle![name] != value) OverlayStyle[name] = value;
        }
        public event Action<TrackedMediaElement, bool> RequestRedraw = default!;
        /// <summary>
        /// ONE frame callback per video at a time. Each redraw ends by requesting the next frame, so a redraw from any
        /// other trigger (the stats toggle, a mode change, mouseenter) used to start a SECOND callback chain that never
        /// died out: MEASURED in Firefox, 85 renders in 4 s for 72 new video frames, 29 of them a repeat of the frame
        /// just rendered while other frames were never rendered.
        /// </summary>
        bool _frameCallbackPending;

        void OnFrameCallback()
        {
            _frameCallbackPending = false;
            UpdateFrame();
        }

        void RequestVideoFrameCallback()
        {
            if (IsDisposed || _frameCallback == null) return;
            if (supportsRequestVideoFrameCallback == true && VideoElement != null)
            {
                if (_frameCallbackPending) return;
                _frameCallbackPending = true;
                VideoElement.RequestVideoFrameCallback(_frameCallback);
            }
            else if (supportsWindowRequestAnimationFrame == true)
            {
                if (_frameCallbackPending) return;
                _frameCallbackPending = true;
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
                VideoElement.OnSeeked -= VideoElement_OnSeeked;
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
            TrackedMedia.ThreeDRenderer.ReleaseVideo(this);   // this video's temporal history
            OverlayRenderer?.Dispose();
            OverlayRenderer = null;
            _statsCtx?.Dispose();
            _statsCtx = null;
            _statsCtxCanvas = null;
            ReleaseBitmap(ref _usableImage);
            ReleaseBitmap(ref _fallbackImage);
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
            // a new image: forget the previous one's CORS copy / taint verdict (the last GOOD source stays: the fallback)
            ReleaseBitmap(ref _usableImage);
            _usableImageMode = null;
            _usableImageTried = false;
            _imageTainted = false;
            checkFrameSize = true;
            UpdateFrame();
        }
        void VideoElement_OnSeeked()
        {
            TrackedMedia.ThreeDRenderer.ResetVideo(this);
            _frameCallbackPending = false;   // a callback a browser dropped must not stall the video for good
        }
        void VideoElement_OnLoadedData()
        {
            // New data = a new source or a reload: no temporal history carries over.
            TrackedMedia.ThreeDRenderer.ResetVideo(this);
            _frameCallbackPending = false;   // (see VideoElement_OnSeeked)
            checkFrameSize = true;
            UpdateFrame();
        }
    }
}
