using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU.Rendering;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.SpawnJS.JSObjects;
using System.Diagnostics;
using GPUCopyExternalImageSource = SpawnDev.SpawnJS.Union<SpawnDev.SpawnJS.JSObjects.ImageBitmap, SpawnDev.SpawnJS.JSObjects.ImageData, SpawnDev.SpawnJS.JSObjects.HTMLImageElement, SpawnDev.SpawnJS.JSObjects.HTMLVideoElement, SpawnDev.SpawnJS.JSObjects.VideoFrame, SpawnDev.SpawnJS.JSObjects.HTMLCanvasElement, SpawnDev.SpawnJS.JSObjects.OffscreenCanvas>;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>3D output modes, in the order of the overlay's "3D Mode" toggle (persisted as an int).</summary>
    public enum ThreeDMode
    {
        RedCyan = 0,
        GreenMagenta = 1,
        Dimenco2DZ = 2,
    }

    /// <summary>What one rendered frame cost, for the stats overlay and the cost-adaptive video depth level.</summary>
    /// <param name="RecompileMs">
    /// Time the depth session spent compiling an executor for an input shape it had not cached (0 on a cache hit).
    /// A frame with a recompile also paid that shape's first forward, so it says nothing about steady-state cost.
    /// </param>
    /// <summary>
    /// A fullscreen Dimenco 2D+Z target (<see cref="ThreeDKernels.TwoDZScreenKernel"/>): the output is the whole screen,
    /// <paramref name="Width"/> x <paramref name="Height"/> device pixels, and the video's displayed content (object-fit
    /// applied) is the rect <paramref name="RectX"/>, <paramref name="RectY"/>, <paramref name="RectW"/> x <paramref name="RectH"/>
    /// in those pixels.
    /// </summary>
    public readonly record struct DimencoScreen(int Width, int Height, float RectX, float RectY, float RectW, float RectH);

    /// <param name="StreamFrame">A streaming model's frame index in its window (frames since its last reset), -1 otherwise.</param>
    public readonly record struct FrameStats(int Width, int Height, int DepthWidth, int DepthHeight, double DepthMs, double RenderMs, double RecompileMs,
        DepthModelKind Model, long StreamFrame);

    /// <summary>
    /// One frame, source element -> screen, on one accelerator:
    /// <c>IExternalImageCopier</c> (element -> packed RGBA buffer, GPU-side) -> <c>DepthEstimationPipeline.EstimateGpuRawAsync</c>
    /// (raw depth at frame resolution + its min/max, both written into buffers this class owns) -> <see cref="ThreeDKernels"/>
    /// -> <c>ICanvasRenderer.PresentAsync</c>. Nothing crosses back to the CPU, not even the depth min/max: the kernels read
    /// it from the device, so a frame never waits on a GPU->CPU round trip.
    /// </summary>
    /// <remarks>
    /// Frames are rendered one at a time (TrackedMedia's serial queue), so the frame and output buffers are shared by
    /// every tracked element; each element owns only its <see cref="ICanvasRenderer"/>.
    /// </remarks>
    /// <summary>Video depth temporal filters (see <see cref="ThreeDRenderer.FilterKind"/>).</summary>
    public enum TemporalFilterKind
    {
        /// <summary>Per-pixel One Euro adaptive low-pass (two floats of state per pixel).</summary>
        OneEuro,
        /// <summary>GMZPlayer's DepthRollingWindow port (20-frame ring, edge/motion/keyframe weighting).</summary>
        RollingWindow,
    }

    public sealed class ThreeDRenderer : IDisposable
    {
        readonly GpuService Gpu;
        readonly DepthService Depth;
        WebGPUAccelerator? _accelerator;
        MemoryBuffer1D<int, Stride1D.Dense>? _frame;
        MemoryBuffer2D<int, Stride2D.DenseX>? _output;
        MemoryBuffer1D<float, Stride1D.Dense>? _depth;
        MemoryBuffer1D<float, Stride1D.Dense>? _minMax;   // [min, max] of _depth, written by the pipeline's GPU reduction
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float>? _smoothRange;

        /// <summary>
        /// Video: normalize depth by a range smoothed over frames instead of each frame's own min/max
        /// (<see cref="ThreeDKernels.SmoothRangeKernel"/>). Still images always use their exact range. Default on.
        /// </summary>
        public bool SmoothDepthRange { get; set; } = true;
        /// <summary>How far the smoothed range moves toward a WIDER raw range per frame.</summary>
        public float RangeGrow { get; set; } = 0.5f;
        /// <summary>How far the smoothed range moves toward a NARROWER raw range per frame.</summary>
        public float RangeShrink { get; set; } = 0.05f;

        // Video temporal filter (TemporalDepthKernels): model-resolution depth -> disparity (u units) -> ring -> filter ->
        // bilinear upsample into _depth as DISPARITY, which the 3D kernels then read with a [0,1] range, directDepth = 0.
        // Per-frame SCRATCH, shared by every video (one GPU queue orders their frames); the HISTORY is per video (VideoState).
        MemoryBuffer1D<float, Stride1D.Dense>? _modelDepth, _u, _uFiltered, _unitRange, _depthUnfiltered, _guide;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int>? _disparityK;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _ringWriteK;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float, float, float, float>? _filterK;
        Action<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>? _upsampleK;
        Action<Index1D, ArrayView1D<int, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>? _guideK;
        Action<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float>? _jbuK;

        /// <summary>
        /// Depth model resolution -> frame resolution EDGE-AWARE (joint bilateral upsampling guided by the frame's colours,
        /// <see cref="DepthUpsampleKernels"/>), so depth edges land on the object outlines instead of a bilinear halo.
        /// Off = plain bilinear (the A/B arm). Images and video both. Default on.
        /// </summary>
        public bool EdgeAwareUpsample { get; set; } = true;
        /// <summary>Edge-aware upsampling: spatial sigma in DEPTH-MAP pixels.</summary>
        public float UpsampleSigmaSpatial { get; set; } = 1f;
        /// <summary>Edge-aware upsampling: colour sigma (RGB distance, channels in [0,1]). Smaller = sharper edges, more texture copying.</summary>
        public float UpsampleSigmaRange { get; set; } = 0.1f;

        /// <summary>Video: temporal per-pixel depth filter on. Default on; <see cref="FilterKind"/> picks which.</summary>
        public bool TemporalFilter { get; set; } = true;
        /// <summary>Which temporal filter (both run at model resolution on the disparity map).</summary>
        public TemporalFilterKind FilterKind { get; set; } = TemporalFilterKind.OneEuro;
        /// <summary>One Euro: cutoff for a still pixel, cycles per frame (lower = smoother, more lag on slow change).</summary>
        public float OneEuroMinCutoff { get; set; } = 0.015f;
        /// <summary>One Euro: how fast the cutoff rises with the rate of change (higher = less lag on motion).</summary>
        public float OneEuroBeta { get; set; } = 0.02f;
        /// <summary>One Euro: cutoff of the derivative estimate, cycles per frame.</summary>
        public float OneEuroDCutoff { get; set; } = 0.1f;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float, float>? _oneEuroK;
        /// <summary>Filter knobs, in percent of the depth range (u units) - DepthRollingWindow's meanings.</summary>
        public float FilterEdgeThreshold { get; set; } = 5f;
        public float FilterMotionThreshold { get; set; } = 5f;
        public float FilterTemporalDecay { get; set; } = 6.5f;
        public float FilterSimilarityDelta { get; set; } = 2f;
        public float FilterSimilaritySigma { get; set; } = 3f;
        public float FilterSpatialRadius { get; set; } = 2f;
        /// <summary>Anti-ghosting: the filtered value may move at most this far (percent of range) from the current
        /// frame. GMZPlayer used 0.02 in DAv2's raw units (~0.1% of range), which left nothing to filter; the measured
        /// DAv3 jitter is ~1.5% per frame, so the clamp has to allow more than that.</summary>
        public float FilterMaxDeviation { get; set; } = 3f;

        /// <summary>
        /// Video frames whose GPU work may still be running before the next frame waits (backpressure; see RenderAsync).
        /// 3: Firefox reports GPU completion on a ~100 ms poll (MEASURED 2026-10-04), so 30 fps needs 3 in flight there;
        /// on Chrome the fence resolves within the GPU time and nothing waits unless the GPU really falls behind.
        /// </summary>
        public int MaxVideoFramesInFlight { get; set; } = 3;
        readonly Queue<Task> _videoInFlight = new();
        MemoryBuffer1D<float, Stride1D.Dense>? _profiles;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>? _anaglyph;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _twoDZ;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float>? _twoDZScreen;
        MemoryBuffer2D<int, Stride2D.DenseX>? _screenOutput;

        // DIAGNOSTIC flicker probe (the "flicker" sweep): see ThreeDKernels.FlickerKernel.
        MemoryBuffer1D<float, Stride1D.Dense>? _flickerPrev, _flickerPrevRaw, _flickerAcc;
        MemoryBuffer1D<int, Stride1D.Dense>? _flickerPrevFrame;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int>? _flicker;
        MemoryBuffer1D<float, Stride1D.Dense>? _probePlaceholder;   // bound where an arm shares the other's buffer
        int _flickerPixels;
        readonly float[] _flickerSlots = new float[5 * ThreeDKernels.FlickerSlots];
        /// <summary>DIAGNOSTIC: measure the displayed disparity's frame-to-frame change (one 4 KB readback per frame).</summary>
        public bool FlickerProbe { get; set; }
        /// <summary>Mean |delta disparity| per pixel for each probed frame (disparity in [0,1]).</summary>
        public List<double> FlickerSamples { get; } = new();
        /// <summary>Mean |delta disparity| over STATIC pixels (color unchanged) for each probed frame: the model's own jitter.</summary>
        public List<double> FlickerStaticSamples { get; } = new();
        /// <summary>The same two measures for each frame's RAW range, on the same frames (the paired A/B arm).</summary>
        public List<double> FlickerRawSamples { get; } = new();
        public List<double> FlickerRawStaticSamples { get; } = new();

        /// <summary>
        /// Maximum stereo separation IN PIXELS of the frame at Level3D = 1: the 3.x store build's sepMax 0.02 times its
        /// 900 / outWidth width normalization = 18 px whatever the frame size (9 px at its default level 0.5). The
        /// MultiView-derived 2.5% of the frame WIDTH used before gave 38 px at 1080p - TJ judged 3.x's look cleaner.
        /// </summary>
        public float SepMaxPx { get; set; } = 0.02f * 900f;

        public ThreeDRenderer(GpuService gpu, DepthService depth)
        {
            Gpu = gpu;
            Depth = depth;
            Gpu.OnDeviceLost += ReleaseDeviceResources;
        }

        /// <summary>
        /// One video's temporal HISTORY: the smoothed depth range and the temporal filter's state. Kept per video so that
        /// several videos on a page (Twitch's front page plays three) no longer reset one shared history on every frame.
        /// Small - model resolution, a few MB at most. The streaming model's own window is NOT here: it is the pipeline's,
        /// and it follows ONE video (the caller's primary), see <see cref="_streamOwner"/>.
        /// </summary>
        sealed class VideoState : IDisposable
        {
            public MemoryBuffer1D<float, Stride1D.Dense>? RangeSmooth;   // the range the 3D kernels use, smoothed over frames
            public bool RangeHasState;
            public int RangeW, RangeH;
            public MemoryBuffer1D<float, Stride1D.Dense>? Ring;          // rolling-window filter frames
            public int RingHead, RingFilled, RingW, RingH;
            public MemoryBuffer1D<float, Stride1D.Dense>? OeX, OeDx;     // One Euro state
            public bool OeKindSwitched = true;
            public bool ResetPending;
            public object? Pipeline;   // the history is in this model's units (VDA: disparity, DAv3: depth)

            public void Dispose()
            {
                foreach (var b in new[] { RangeSmooth, Ring, OeX, OeDx })
                    try { b?.Dispose(); } catch { }
                RangeSmooth = Ring = OeX = OeDx = null;
            }
        }
        readonly Dictionary<object, VideoState> _videoStates = new();
        static readonly object AnonymousVideo = new();
        object? _streamOwner;           // the video the streaming model's window currently follows
        bool _streamResetPending;

        /// <summary>
        /// The video of <paramref name="owner"/> jumped (seek, new source): its next frame starts its temporal history over,
        /// and the streaming model's window too when it follows this video.
        /// </summary>
        public void ResetVideo(object owner)
        {
            if (_videoStates.TryGetValue(owner, out var state)) state.ResetPending = true;
            if (ReferenceEquals(owner, _streamOwner)) _streamResetPending = true;
        }

        /// <summary>The video of <paramref name="owner"/> is gone: free its history.</summary>
        public void ReleaseVideo(object owner)
        {
            if (_videoStates.Remove(owner, out var state)) state.Dispose();
            if (ReferenceEquals(owner, _streamOwner)) _streamOwner = null;
        }

        /// <summary>A presenter for an overlay canvas, on the shared accelerator. The caller owns and disposes it.</summary>
        public async Task<ICanvasRenderer> CreateCanvasRendererAsync(HTMLCanvasElement canvas)
        {
            var accelerator = await EnsureAcceleratorAsync();
            var renderer = CanvasRendererFactory.Create(accelerator);
            renderer.AttachCanvas(canvas);
            return renderer;
        }

        /// <summary>
        /// Renders the current pixels of <paramref name="source"/> (natural size <paramref name="width"/> x
        /// <paramref name="height"/>) in 3D to <paramref name="target"/>.
        /// </summary>
        /// <param name="profiler">DIAGNOSTIC: when set, marks each phase of this frame and waits for the GPU at the end so
        /// the GPU tail shows separately from the host time (see <see cref="FrameProfiler"/>).</param>
        /// <param name="videoOwner">Video only: who the frame belongs to (the tracked element). The temporal state - a
        /// streaming model's frame cache, the temporal filter, the smoothed range - follows ONE video; a frame from another
        /// owner starts it over, so two videos on a page never feed each other's history.</param>
        /// <param name="dimencoScreen">Dimenco mode, fullscreen video: render the whole screen as 2D+Z
        /// (<see cref="DimencoScreen"/>) instead of the frame's own halves.</param>
        /// <exception cref="JSException">The source is tainted (cross-origin without CORS) - the browser refuses its pixels.</exception>
        public async Task<FrameStats> RenderAsync(GPUCopyExternalImageSource source, int width, int height, ICanvasRenderer target,
            ThreeDMode mode, float level3D, float focus3D, bool video, int videoLevel, FrameProfiler? profiler = null, object? videoOwner = null,
            DimencoScreen? dimencoScreen = null, bool primaryVideo = true)
        {
            var accelerator = await EnsureAcceleratorAsync();
            // The streaming model (VDA) keeps a ~MB-per-frame window (~500 MB at the top level) and follows ONE video: only
            // the PRIMARY video uses it; any other video gets the per-frame model, so nothing thrashes the window.
            var (pipeline, model) = await Depth.GetPipelineForAsync(video, allowStreaming: primaryVideo);
            // Video BACKPRESSURE: the depth forward no longer awaits GPU completion (Session.SkipCompletionWait), so
            // bound the frames whose GPU work may still be running. A frame waits only when MaxVideoFramesInFlight
            // are unfinished; that wait counts as frame cost, so a GPU that falls behind lowers the depth level.
            double backpressureMs = 0;
            if (video)
            {
                var waitSw = Stopwatch.StartNew();
                while (_videoInFlight.Count >= Math.Max(1, MaxVideoFramesInFlight))
                    await _videoInFlight.Dequeue();
                backpressureMs = waitSw.Elapsed.TotalMilliseconds;
            }
            int pixels = width * height;
            VideoState? state = null;
            if (video)
            {
                var key = videoOwner ?? AnonymousVideo;
                if (!_videoStates.TryGetValue(key, out state)) _videoStates[key] = state = new VideoState();
                if (state.ResetPending || !ReferenceEquals(state.Pipeline, pipeline))
                {
                    // a seek / new source, or this video switched models (its history is in the other model's units)
                    state.RingFilled = 0;
                    state.RangeHasState = false;
                    state.ResetPending = false;
                    state.Pipeline = pipeline;
                }
                if (pipeline.IsStreaming && (!ReferenceEquals(key, _streamOwner) || _streamResetPending))
                {
                    // the window follows a different video now (or this one jumped): it starts over
                    pipeline.ResetStream();
                    _streamOwner = key;
                    _streamResetPending = false;
                }
            }

            if (_frame == null || _frame.Length < pixels)
            {
                _frame?.Dispose();
                _depth?.Dispose();
                _frame = accelerator.Allocate1D<int>(pixels);
                _depth = accelerator.Allocate1D<float>(pixels);
            }
            var frameView = _frame.View.SubView(0, pixels);
            profiler?.Mark("setup");
            Gpu.GetCopier(accelerator).CopyToBuffer(source, width, height, frameView);
            profiler?.Mark("copy");

            var sw = Stopwatch.StartNew();
            pipeline.ProcessResolution = DepthService.ProcessResolution(video, videoLevel);
            var (inputW, inputH) = pipeline.ModelInputSize(width, height);
            var depthView = _depth!.View.SubView(0, pixels);
            int direct = DepthService.IsDirectDepth(model) ? 1 : 0;
            bool temporal = video && TemporalFilter;
            // Depth stays at MODEL resolution (16k-450k pixels, not the frame's millions): the temporal filter runs there,
            // and the one upsample to the frame (Upsample: edge-aware by default) is ours, for images and video alike.
            int modelPixels = inputW * inputH;
            if (_modelDepth == null || _modelDepth.Length < modelPixels)
            {
                _modelDepth?.Dispose();
                _modelDepth = accelerator.Allocate1D<float>(modelPixels);
            }
            var (mw, mh) = await pipeline.EstimateGpuRawAsync(frameView, width, height,
                _modelDepth.View.SubView(0, modelPixels), _minMax!.View, inputW, inputH);
            _modelW = mw; _modelH = mh;
            int depthW = width, depthH = height;   // upsampled below
            double depthMs = sw.Elapsed.TotalMilliseconds + backpressureMs;
            double recompileMs = pipeline.Session.LastRecompileMs;
            profiler?.Mark("depth");
            sw.Restart();
            if (depthW != width || depthH != height)
                throw new InvalidOperationException($"depth map is {depthW}x{depthH}, frame is {width}x{height}");
            // The range the 3D kernels (and the flicker probe) normalize by: smoothed over frames for video.
            var rangeView = _minMax!.View;
            if (state != null && SmoothDepthRange)
            {
                bool reset = !state.RangeHasState || state.RangeW != width || state.RangeH != height;
                state.RangeSmooth ??= accelerator.Allocate1D<float>(2);
                _smoothRange!(1, _minMax.View, state.RangeSmooth.View, reset ? 1 : 0, RangeGrow, RangeShrink);
                state.RangeHasState = true; state.RangeW = width; state.RangeH = height;
                rangeView = state.RangeSmooth.View;
            }
            else if (state != null) state.RangeHasState = false;
            int depthDirect = direct;
            if (temporal) RunTemporalFilter(accelerator, state!, rangeView, direct, frameView, depthView, width, height);
            else
            {
                if (state != null) state.RingFilled = 0;
                int plane = _modelW * _modelH;
                EnsureDepthKernels(accelerator);
                EnsureFrameBuffer(ref _u, accelerator, plane);
                _disparityK!(plane, _modelDepth!.View.SubView(0, plane), rangeView, _u!.View.SubView(0, plane), direct);
                Upsample(accelerator, _u.View.SubView(0, plane), frameView, depthView, width, height);
            }
            // _depth now holds DISPARITY in [0,1]: the 3D kernels read it through the unit range, directDepth = 0.
            rangeView = _unitRange!.View;
            direct = 0;
            var presented = dimencoScreen != null && mode == ThreeDMode.Dimenco2DZ ? null : EnsureOutput(ref _output, accelerator, width, height);
            if (presented == null)
            {
                // fullscreen Dimenco: the whole SCREEN as 2D+Z, so the display's halves line up whatever the layout
                var ds = dimencoScreen!.Value;
                presented = EnsureOutput(ref _screenOutput, accelerator, ds.Width, ds.Height);
                _twoDZScreen!(new Index2D(ds.Width, ds.Height), frameView, depthView, presented.View.BaseView, rangeView,
                    width, height, ds.Width, direct, ds.RectX, ds.RectY, ds.RectW, ds.RectH);
            }
            else if (mode == ThreeDMode.Dimenco2DZ)
            {
                _twoDZ!(new Index2D(width, height), frameView, depthView, presented.View.BaseView, rangeView, width, direct);
            }
            else
            {
                int profile = mode == ThreeDMode.GreenMagenta ? AnaglyphProfiles.GreenMagenta : AnaglyphProfiles.RedCyan;
                float separationPx = SepMaxPx * Math.Clamp(level3D, 0f, 1f);
                _anaglyph!(new Index2D(width, height), frameView, depthView, _profiles!.View, presented.View.BaseView, rangeView,
                    width, direct, separationPx, Math.Clamp(focus3D, 0f, 1f), profile * ThreeDKernels.ProfileStride);
            }
            if (FlickerProbe)
            {
                // Paired arms on the same frames: with the temporal filter, SHOWN = filtered and the other arm = the
                // unfiltered disparity (upsampled the same way); without it, SHOWN = the shown range vs each frame's raw range.
                int plane = _modelW * _modelH;
                EnsureFrameBuffer(ref _depthUnfiltered, accelerator, pixels);
                bool sameDepth = !temporal && !(video && SmoothDepthRange);
                if (temporal)
                    Upsample(accelerator, _u!.View.SubView(0, plane), frameView, _depthUnfiltered!.View.SubView(0, pixels), width, height);
                else if (!sameDepth)
                {
                    // the other arm: this frame normalized by its OWN raw range instead of the smoothed one
                    _disparityK!(plane, _modelDepth!.View.SubView(0, plane), _minMax!.View, _u!.View.SubView(0, plane), depthDirect);
                    Upsample(accelerator, _u.View.SubView(0, plane), frameView, _depthUnfiltered!.View.SubView(0, pixels), width, height);
                }
                await ProbeFlickerAsync(accelerator, depthView, _depthUnfiltered!.View.SubView(0, pixels), frameView, rangeView, _unitRange!.View, pixels, 0,
                    sameDepth: sameDepth, sameRange: true);
            }
            else _flickerPixels = 0;
            // PresentAsync submits the pending kernels before its render pass reads the output. Nothing may AWAIT between the
            // present and the return: the caller draws the stats text on top of this frame, and a yield in between lets the
            // browser composite the frame without it (the flicker probe's readback did: stats vanished for whole sweeps).
            await target.PresentAsync(presented);
            // this frame's GPU-done fence, NOT awaited here (it would yield before the stats draw; see above):
            // SynchronizeAsync runs synchronously up to its first await (flush + onSubmittedWorkDone) and returns the Task
            if (video) _videoInFlight.Enqueue(accelerator.SynchronizeAsync());
            if (profiler != null)
            {
                profiler.Mark("render+present");
                await accelerator.SynchronizeAsync();
                profiler.Mark("gpuTail");
            }
            return new FrameStats(width, height, inputW, inputH, depthMs, sw.Elapsed.TotalMilliseconds, recompileMs, model,
                pipeline.IsStreaming ? pipeline.StreamFrameIndex : -1);
        }

        int _modelW, _modelH;

        static MemoryBuffer2D<int, Stride2D.DenseX> EnsureOutput(ref MemoryBuffer2D<int, Stride2D.DenseX>? buffer, WebGPUAccelerator accelerator, int width, int height)
        {
            if (buffer == null || buffer.Extent.X != width || buffer.Extent.Y != height)
            {
                buffer?.Dispose();
                buffer = accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
            }
            return buffer;
        }

        static void EnsureFrameBuffer(ref MemoryBuffer1D<float, Stride1D.Dense>? buffer, WebGPUAccelerator accelerator, int length)
        {
            if (buffer != null && buffer.Length >= length) return;
            buffer?.Dispose();
            buffer = accelerator.Allocate1D<float>(length);
        }

        /// <summary>Model depth -> disparity (u) -> ring -> TemporalFilterKernel -> bilinear into <paramref name="depthOut"/>.</summary>
        void EnsureDepthKernels(WebGPUAccelerator accelerator)
        {
            _disparityK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int>(TemporalDepthKernels.DisparityKernel);
            _upsampleK ??= accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>(TemporalDepthKernels.UpsampleKernel);
        }

        /// <summary>Model-resolution u (1 + 100 * disparity) -> frame-resolution disparity: edge-aware or bilinear.</summary>
        void Upsample(WebGPUAccelerator accelerator, ArrayView1D<float, Stride1D.Dense> u, ArrayView1D<int, Stride1D.Dense> frame,
            ArrayView1D<float, Stride1D.Dense> depthOut, int width, int height)
        {
            int w = _modelW, h = _modelH, plane = w * h;
            EnsureDepthKernels(accelerator);
            if (!EdgeAwareUpsample)
            {
                _upsampleK!(new Index2D(width, height), u, w, h, depthOut, width, height);
                return;
            }
            _guideK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<int, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>(DepthUpsampleKernels.GuideKernel);
            _jbuK ??= accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float>(DepthUpsampleKernels.JointBilateralUpsampleKernel);
            EnsureFrameBuffer(ref _guide, accelerator, 3 * plane);
            var guide = _guide!.View.SubView(0, 3 * plane);
            _guideK(plane, frame, width, height, guide, w, h);
            _jbuK(new Index2D(width, height), u, w, h, guide, frame, depthOut, width, height, UpsampleSigmaSpatial, UpsampleSigmaRange);
        }

        void RunTemporalFilter(WebGPUAccelerator accelerator, VideoState state, ArrayView1D<float, Stride1D.Dense> rangeView, int direct,
            ArrayView1D<int, Stride1D.Dense> frame, ArrayView1D<float, Stride1D.Dense> depthOut, int width, int height)
        {
            int w = _modelW, h = _modelH, plane = w * h;
            EnsureDepthKernels(accelerator);
            _ringWriteK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(TemporalDepthKernels.RingWriteKernel);
            _filterK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float, float, float, float>(TemporalDepthKernels.TemporalFilterKernel);
            EnsureFrameBuffer(ref _u, accelerator, plane);
            EnsureFrameBuffer(ref _uFiltered, accelerator, plane);
            if (state.RingW != w || state.RingH != h || state.Ring == null)
            {
                // A new model grid (video size or depth level changed): the history is a different picture - start over.
                state.Ring?.Dispose();
                state.Ring = accelerator.Allocate1D<float>(TemporalDepthKernels.RingFrames * plane);
                state.Ring.MemSetToZero();
                state.RingW = w; state.RingH = h; state.RingHead = 0; state.RingFilled = 0;
            }
            else if (state.RingFilled == 0)
            {
                state.Ring.MemSetToZero();   // restarting (a seek, a model switch, a filter toggle): no stale frames
                state.RingHead = 0;
            }
            _disparityK!(plane, _modelDepth!.View.SubView(0, plane), rangeView, _u!.View.SubView(0, plane), direct);
            if (FilterKind == TemporalFilterKind.OneEuro)
            {
                _oneEuroK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float, float>(TemporalDepthKernels.OneEuroKernel);
                bool reset = state.OeX == null || state.OeX.Length < plane || state.RingFilled == 0 || state.OeKindSwitched;
                EnsureFrameBuffer(ref state.OeX, accelerator, plane);
                EnsureFrameBuffer(ref state.OeDx, accelerator, plane);
                state.OeKindSwitched = false;
                _oneEuroK(plane, _u.View.SubView(0, plane), state.OeX!.View.SubView(0, plane), state.OeDx!.View.SubView(0, plane),
                    _uFiltered!.View.SubView(0, plane), reset ? 1 : 0, OneEuroMinCutoff, OneEuroBeta, OneEuroDCutoff);
                if (state.RingFilled < TemporalDepthKernels.RingFrames) state.RingFilled++;   // frame count since the (re)start
            }
            else
            {
                state.OeKindSwitched = true;
                _ringWriteK(plane, _u.View.SubView(0, plane), state.Ring.View, state.RingHead, plane);
                state.RingHead = (state.RingHead + 1) % TemporalDepthKernels.RingFrames;
                if (state.RingFilled < TemporalDepthKernels.RingFrames) state.RingFilled++;
                _filterK(plane, state.Ring.View, _uFiltered!.View.SubView(0, plane), state.RingHead, state.RingFilled, w, h,
                    FilterEdgeThreshold, FilterMotionThreshold, FilterTemporalDecay, FilterSimilarityDelta, FilterSimilaritySigma,
                    FilterSpatialRadius, FilterMaxDeviation);
            }
            Upsample(accelerator, _uFiltered.View.SubView(0, plane), frame, depthOut, width, height);
        }

        async Task ProbeFlickerAsync(WebGPUAccelerator accelerator, ArrayView1D<float, Stride1D.Dense> depthView,
            ArrayView1D<float, Stride1D.Dense> otherDepthView,
            ArrayView1D<int, Stride1D.Dense> frameView, ArrayView1D<float, Stride1D.Dense> rangeView,
            ArrayView1D<float, Stride1D.Dense> otherRangeView, int pixels, int direct, bool sameDepth, bool sameRange)
        {
            _flicker ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int>(ThreeDKernels.FlickerKernel);
            _probePlaceholder ??= accelerator.Allocate1D<float>(4);
            if (sameDepth) otherDepthView = _probePlaceholder.View;
            if (sameRange) otherRangeView = _probePlaceholder.View;
            _flickerAcc ??= accelerator.Allocate1D<float>(5 * ThreeDKernels.FlickerSlots);
            bool hasPrev = _flickerPixels == pixels && _flickerPrev != null;
            if (_flickerPrev == null || _flickerPrev.Length < pixels)
            {
                _flickerPrev?.Dispose();
                _flickerPrevRaw?.Dispose();
                _flickerPrevFrame?.Dispose();
                _flickerPrev = accelerator.Allocate1D<float>(pixels);
                _flickerPrevRaw = accelerator.Allocate1D<float>(pixels);
                _flickerPrevFrame = accelerator.Allocate1D<int>(pixels);
            }
            _flickerAcc.MemSetToZero();
            _flicker(pixels, depthView, otherDepthView, rangeView, otherRangeView, _flickerPrev.View.SubView(0, pixels),
                _flickerPrevRaw!.View.SubView(0, pixels), frameView, _flickerPrevFrame!.View.SubView(0, pixels),
                _flickerAcc.View, direct, hasPrev ? 1 : 0, sameDepth ? 0 : 1, sameRange ? 0 : 1);
            _flickerPixels = pixels;
            if (!hasPrev) return;
            await _flickerAcc.CopyToHostAsync(_flickerSlots);
            double all = 0, stat = 0, statN = 0, allRaw = 0, statRaw = 0;
            const int S = ThreeDKernels.FlickerSlots;
            for (int i = 0; i < S; i++)
            {
                all += _flickerSlots[i];
                stat += _flickerSlots[S + i];
                statN += _flickerSlots[2 * S + i];
                allRaw += _flickerSlots[3 * S + i];
                statRaw += _flickerSlots[4 * S + i];
            }
            FlickerSamples.Add(all / pixels);
            FlickerRawSamples.Add(allRaw / pixels);
            if (statN > pixels * 0.05)   // need a real static region to say anything
            {
                FlickerStaticSamples.Add(stat / statN);
                FlickerRawStaticSamples.Add(statRaw / statN);
            }
        }

        async Task<WebGPUAccelerator> EnsureAcceleratorAsync()
        {
            var accelerator = await Gpu.GetAcceleratorAsync();
            if (_accelerator != accelerator)
            {
                ReleaseDeviceResources();
                _accelerator = accelerator;
                _profiles = accelerator.Allocate1D<float>(AnaglyphProfiles.Data.Length);
                _profiles.CopyFromCPU(AnaglyphProfiles.Data);   // 42 floats, once
                _minMax = accelerator.Allocate1D<float>(2);
                _unitRange = accelerator.Allocate1D<float>(2);
                _unitRange.CopyFromCPU(new[] { 0f, 1f });
                _smoothRange = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float>(ThreeDKernels.SmoothRangeKernel);
                _anaglyph = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>(ThreeDKernels.AnaglyphKernel);
                _twoDZ = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ThreeDKernels.TwoDZKernel);
                _twoDZScreen = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float>(ThreeDKernels.TwoDZScreenKernel);
            }
            return accelerator;
        }

        void ReleaseDeviceResources()
        {
            // After a device loss these are already dead; Dispose must not throw either way.
            try { _frame?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _screenOutput?.Dispose(); } catch { }
            _screenOutput = null; _twoDZScreen = null;
            try { _profiles?.Dispose(); } catch { }
            try { _depth?.Dispose(); } catch { }
            try { _minMax?.Dispose(); } catch { }
            try { _flickerPrev?.Dispose(); } catch { }
            try { _flickerAcc?.Dispose(); } catch { }
            foreach (var b in new[] { _modelDepth, _u, _uFiltered, _unitRange, _depthUnfiltered, _guide })
                try { b?.Dispose(); } catch { }
            _modelDepth = _u = _uFiltered = _unitRange = _depthUnfiltered = _guide = null;
            _guideK = null; _jbuK = null;
            foreach (var st in _videoStates.Values) st.Dispose();   // every video's history was on this device
            _videoStates.Clear();
            _streamOwner = null; _streamResetPending = false;
            _oneEuroK = null;
            _disparityK = null; _ringWriteK = null; _filterK = null; _upsampleK = null;
            _smoothRange = null;
            try { _flickerPrevFrame?.Dispose(); } catch { }
            try { _flickerPrevRaw?.Dispose(); } catch { }
            try { _probePlaceholder?.Dispose(); } catch { }
            _probePlaceholder = null;
            _flickerPrevRaw = null;
            _flickerPrevFrame = null;
            _flickerPrev = null; _flickerAcc = null; _flicker = null; _flickerPixels = 0;
            _frame = null; _output = null; _profiles = null; _depth = null; _minMax = null;
            _anaglyph = null; _twoDZ = null;
            _accelerator = null;
            _videoInFlight.Clear();   // fences of a lost / released device
        }

        public void Dispose() => ReleaseDeviceResources();
    }
}
