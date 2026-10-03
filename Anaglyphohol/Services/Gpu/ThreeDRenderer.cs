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
    public readonly record struct FrameStats(int Width, int Height, int DepthWidth, int DepthHeight, double DepthMs, double RenderMs, double RecompileMs);

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
        MemoryBuffer1D<float, Stride1D.Dense>? _rangeSmooth;   // video: the range the 3D kernels use, smoothed over frames
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float>? _smoothRange;
        bool _rangeHasState;
        int _rangeW, _rangeH;

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
        MemoryBuffer1D<float, Stride1D.Dense>? _modelDepth, _u, _uFiltered, _ring, _unitRange, _depthUnfiltered;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int>? _disparityK;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _ringWriteK;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float, float, float, float>? _filterK;
        Action<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>? _upsampleK;
        int _ringHead, _ringFilled, _ringW, _ringH;
        bool _oeKindSwitched = true;

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
        MemoryBuffer1D<float, Stride1D.Dense>? _oeX, _oeDx;
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
        MemoryBuffer1D<float, Stride1D.Dense>? _profiles;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>? _anaglyph;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _twoDZ;

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

        /// <summary>Maximum stereo separation as a fraction of the frame width, at Level3D = 1 (MultiView's SepMax).</summary>
        public float SepMax { get; set; } = 0.025f;

        public ThreeDRenderer(GpuService gpu, DepthService depth)
        {
            Gpu = gpu;
            Depth = depth;
            Gpu.OnDeviceLost += ReleaseDeviceResources;
        }

        object? _videoOwner;
        object? _videoPipeline;
        bool _videoResetPending;

        /// <summary>
        /// The video of <paramref name="owner"/> jumped (seek, new source): its next frame starts the temporal state over.
        /// Ignored when another video owns the state (its next frame resets anyway).
        /// </summary>
        public void ResetVideo(object owner)
        {
            if (ReferenceEquals(owner, _videoOwner)) _videoResetPending = true;
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
        /// <exception cref="JSException">The source is tainted (cross-origin without CORS) - the browser refuses its pixels.</exception>
        public async Task<FrameStats> RenderAsync(GPUCopyExternalImageSource source, int width, int height, ICanvasRenderer target,
            ThreeDMode mode, float level3D, float focus3D, bool video, int videoLevel, FrameProfiler? profiler = null, object? videoOwner = null)
        {
            var accelerator = await EnsureAcceleratorAsync();
            var (pipeline, model) = await Depth.GetPipelineForAsync(video);
            int pixels = width * height;
            if (!video || !ReferenceEquals(videoOwner, _videoOwner) || _videoResetPending || !ReferenceEquals(pipeline, _videoPipeline))
            {
                // A still image, another video, a seek, or a different model: no history carries over.
                pipeline.ResetStream();
                _ringFilled = 0;
                _rangeHasState = false;
                _videoResetPending = false;
            }
            _videoOwner = video ? videoOwner : null;
            _videoPipeline = pipeline;

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
            int depthW, depthH;
            if (temporal)
            {
                // Model-resolution depth: the filter runs on ~16k-145k pixels instead of the video's millions.
                int modelPixels = inputW * inputH;
                if (_modelDepth == null || _modelDepth.Length < modelPixels)
                {
                    _modelDepth?.Dispose();
                    _modelDepth = accelerator.Allocate1D<float>(modelPixels);
                }
                var (mw, mh) = await pipeline.EstimateGpuRawAsync(frameView, width, height,
                    _modelDepth.View.SubView(0, modelPixels), _minMax!.View, inputW, inputH);
                depthW = width; depthH = height;   // upsampled below
                _modelW = mw; _modelH = mh;
            }
            else
            {
                (depthW, depthH) = await pipeline.EstimateGpuRawAsync(frameView, width, height, depthView, _minMax!.View, width, height);
            }
            double depthMs = sw.Elapsed.TotalMilliseconds;
            double recompileMs = pipeline.Session.LastRecompileMs;
            profiler?.Mark("depth");
            sw.Restart();
            if (depthW != width || depthH != height)
                throw new InvalidOperationException($"depth map is {depthW}x{depthH}, frame is {width}x{height}");
            // The range the 3D kernels (and the flicker probe) normalize by: smoothed over frames for video.
            var rangeView = _minMax!.View;
            if (video && SmoothDepthRange)
            {
                bool reset = !_rangeHasState || _rangeW != width || _rangeH != height;
                _smoothRange!(1, _minMax.View, _rangeSmooth!.View, reset ? 1 : 0, RangeGrow, RangeShrink);
                _rangeHasState = true; _rangeW = width; _rangeH = height;
                rangeView = _rangeSmooth.View;
            }
            else _rangeHasState = false;
            if (temporal)
            {
                RunTemporalFilter(accelerator, rangeView, direct, depthView, width, height);
                // _depth now holds DISPARITY in [0,1]: the 3D kernels read it through the unit range, directDepth = 0.
                rangeView = _unitRange!.View;
                direct = 0;
            }
            else _ringFilled = 0;
            if (_output == null || _output.Extent.X != width || _output.Extent.Y != height)
            {
                _output?.Dispose();
                _output = accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
            }
            ArrayView1D<int, Stride1D.Dense> outputView = _output.View.BaseView;
            if (mode == ThreeDMode.Dimenco2DZ)
            {
                _twoDZ!(new Index2D(width, height), frameView, depthView, outputView, rangeView, width, direct);
            }
            else
            {
                int profile = mode == ThreeDMode.GreenMagenta ? AnaglyphProfiles.GreenMagenta : AnaglyphProfiles.RedCyan;
                float separationPx = SepMax * Math.Clamp(level3D, 0f, 1f) * width;
                _anaglyph!(new Index2D(width, height), frameView, depthView, _profiles!.View, outputView, rangeView,
                    width, direct, separationPx, Math.Clamp(focus3D, 0f, 1f), profile * ThreeDKernels.ProfileStride);
            }
            // PresentAsync submits the pending kernels before its render pass reads the output.
            await target.PresentAsync(_output);
            if (FlickerProbe)
            {
                // Paired arms on the same frames: with the temporal filter, SHOWN = filtered and the other arm = the
                // unfiltered disparity (upsampled the same way); without it, SHOWN = the shown range vs each frame's raw range.
                if (temporal)
                {
                    EnsureFrameBuffer(ref _depthUnfiltered, accelerator, pixels);
                    _upsampleK!(new Index2D(width, height), _u!.View, _modelW, _modelH, _depthUnfiltered!.View, width, height);
                    await ProbeFlickerAsync(accelerator, depthView, _depthUnfiltered.View.SubView(0, pixels), frameView, rangeView, _unitRange!.View, pixels, 0,
                        sameDepth: false, sameRange: true);
                }
                else await ProbeFlickerAsync(accelerator, depthView, depthView, frameView, rangeView, _minMax!.View, pixels, direct,
                    sameDepth: true, sameRange: !(video && SmoothDepthRange));
            }
            else _flickerPixels = 0;
            if (profiler != null)
            {
                profiler.Mark("render+present");
                await accelerator.SynchronizeAsync();
                profiler.Mark("gpuTail");
            }
            return new FrameStats(width, height, inputW, inputH, depthMs, sw.Elapsed.TotalMilliseconds, recompileMs);
        }

        int _modelW, _modelH;

        static void EnsureFrameBuffer(ref MemoryBuffer1D<float, Stride1D.Dense>? buffer, WebGPUAccelerator accelerator, int length)
        {
            if (buffer != null && buffer.Length >= length) return;
            buffer?.Dispose();
            buffer = accelerator.Allocate1D<float>(length);
        }

        /// <summary>Model depth -> disparity (u) -> ring -> TemporalFilterKernel -> bilinear into <paramref name="depthOut"/>.</summary>
        void RunTemporalFilter(WebGPUAccelerator accelerator, ArrayView1D<float, Stride1D.Dense> rangeView, int direct,
            ArrayView1D<float, Stride1D.Dense> depthOut, int width, int height)
        {
            int w = _modelW, h = _modelH, plane = w * h;
            _disparityK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int>(TemporalDepthKernels.DisparityKernel);
            _ringWriteK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(TemporalDepthKernels.RingWriteKernel);
            _filterK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, int, int, float, float, float, float, float, float, float>(TemporalDepthKernels.TemporalFilterKernel);
            _upsampleK ??= accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<float, Stride1D.Dense>, int, int, ArrayView1D<float, Stride1D.Dense>, int, int>(TemporalDepthKernels.UpsampleKernel);
            EnsureFrameBuffer(ref _u, accelerator, plane);
            EnsureFrameBuffer(ref _uFiltered, accelerator, plane);
            if (_ringW != w || _ringH != h || _ring == null)
            {
                // A new model grid (video size or depth level changed): the history is a different picture - start over.
                _ring?.Dispose();
                _ring = accelerator.Allocate1D<float>(TemporalDepthKernels.RingFrames * plane);
                _ring.MemSetToZero();
                _ringW = w; _ringH = h; _ringHead = 0; _ringFilled = 0;
            }
            else if (_ringFilled == 0)
            {
                _ring.MemSetToZero();   // restarting (e.g. after stills or a filter toggle): no stale frames
                _ringHead = 0;
            }
            _disparityK(plane, _modelDepth!.View.SubView(0, plane), rangeView, _u!.View.SubView(0, plane), direct);
            if (FilterKind == TemporalFilterKind.OneEuro)
            {
                _oneEuroK ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float, float>(TemporalDepthKernels.OneEuroKernel);
                bool reset = _oeX == null || _oeX.Length < plane || _ringFilled == 0 || _oeKindSwitched;
                EnsureFrameBuffer(ref _oeX, accelerator, plane);
                EnsureFrameBuffer(ref _oeDx, accelerator, plane);
                _oeKindSwitched = false;
                _oneEuroK(plane, _u.View.SubView(0, plane), _oeX!.View.SubView(0, plane), _oeDx!.View.SubView(0, plane),
                    _uFiltered!.View.SubView(0, plane), reset ? 1 : 0, OneEuroMinCutoff, OneEuroBeta, OneEuroDCutoff);
                if (_ringFilled < TemporalDepthKernels.RingFrames) _ringFilled++;   // frame count since the (re)start
            }
            else
            {
                _oeKindSwitched = true;
                _ringWriteK(plane, _u.View.SubView(0, plane), _ring.View, _ringHead, plane);
                _ringHead = (_ringHead + 1) % TemporalDepthKernels.RingFrames;
                if (_ringFilled < TemporalDepthKernels.RingFrames) _ringFilled++;
                _filterK(plane, _ring.View, _uFiltered!.View.SubView(0, plane), _ringHead, _ringFilled, w, h,
                    FilterEdgeThreshold, FilterMotionThreshold, FilterTemporalDecay, FilterSimilarityDelta, FilterSimilaritySigma,
                    FilterSpatialRadius, FilterMaxDeviation);
            }
            _upsampleK(new Index2D(width, height), _uFiltered.View.SubView(0, plane), w, h, depthOut, width, height);
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
                _rangeSmooth = accelerator.Allocate1D<float>(2);
                _unitRange = accelerator.Allocate1D<float>(2);
                _unitRange.CopyFromCPU(new[] { 0f, 1f });
                _rangeHasState = false;
                _smoothRange = accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, float, float>(ThreeDKernels.SmoothRangeKernel);
                _anaglyph = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>(ThreeDKernels.AnaglyphKernel);
                _twoDZ = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ThreeDKernels.TwoDZKernel);
            }
            return accelerator;
        }

        void ReleaseDeviceResources()
        {
            // After a device loss these are already dead; Dispose must not throw either way.
            try { _frame?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _profiles?.Dispose(); } catch { }
            try { _depth?.Dispose(); } catch { }
            try { _minMax?.Dispose(); } catch { }
            try { _flickerPrev?.Dispose(); } catch { }
            try { _flickerAcc?.Dispose(); } catch { }
            try { _rangeSmooth?.Dispose(); } catch { }
            foreach (var b in new[] { _modelDepth, _u, _uFiltered, _ring, _unitRange, _depthUnfiltered })
                try { b?.Dispose(); } catch { }
            _modelDepth = _u = _uFiltered = _ring = _unitRange = _depthUnfiltered = null;
            try { _oeX?.Dispose(); } catch { }
            try { _oeDx?.Dispose(); } catch { }
            _oeX = _oeDx = null; _oneEuroK = null; _oeKindSwitched = true;
            _disparityK = null; _ringWriteK = null; _filterK = null; _upsampleK = null;
            _ringFilled = 0; _ringW = _ringH = 0;
            _rangeSmooth = null; _smoothRange = null; _rangeHasState = false;
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
        }

        public void Dispose() => ReleaseDeviceResources();
    }
}
