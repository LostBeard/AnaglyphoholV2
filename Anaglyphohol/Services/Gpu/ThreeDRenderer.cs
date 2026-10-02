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
        MemoryBuffer1D<float, Stride1D.Dense>? _profiles;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>? _anaglyph;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _twoDZ;

        // DIAGNOSTIC flicker probe (the "flicker" sweep): see ThreeDKernels.FlickerKernel.
        MemoryBuffer1D<float, Stride1D.Dense>? _flickerPrev, _flickerPrevRaw, _flickerAcc;
        MemoryBuffer1D<int, Stride1D.Dense>? _flickerPrevFrame;
        Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _flicker;
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
        /// <exception cref="JSException">The source is tainted (cross-origin without CORS) - the browser refuses its pixels.</exception>
        public async Task<FrameStats> RenderAsync(GPUCopyExternalImageSource source, int width, int height, ICanvasRenderer target,
            ThreeDMode mode, float level3D, float focus3D, bool video, int videoLevel, FrameProfiler? profiler = null)
        {
            var accelerator = await EnsureAcceleratorAsync();
            var pipeline = await Depth.GetPipelineAsync();
            var model = Depth.Model;
            int pixels = width * height;

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
            if (model == DepthModelKind.DAv3Small) pipeline.ProcessResolution = DepthService.ProcessResolution(video, videoLevel);
            var (inputW, inputH) = pipeline.ModelInputSize(width, height);
            var depthView = _depth!.View.SubView(0, pixels);
            var (depthW, depthH) = await pipeline.EstimateGpuRawAsync(frameView, width, height, depthView, _minMax!.View, width, height);
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
            if (_output == null || _output.Extent.X != width || _output.Extent.Y != height)
            {
                _output?.Dispose();
                _output = accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
            }
            ArrayView1D<int, Stride1D.Dense> outputView = _output.View.BaseView;
            int direct = DepthService.IsDirectDepth(model) ? 1 : 0;
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
            if (FlickerProbe) await ProbeFlickerAsync(accelerator, depthView, frameView, rangeView, pixels, DepthService.IsDirectDepth(model) ? 1 : 0);
            else _flickerPixels = 0;
            if (profiler != null)
            {
                profiler.Mark("render+present");
                await accelerator.SynchronizeAsync();
                profiler.Mark("gpuTail");
            }
            return new FrameStats(width, height, inputW, inputH, depthMs, sw.Elapsed.TotalMilliseconds, recompileMs);
        }

        async Task ProbeFlickerAsync(WebGPUAccelerator accelerator, ArrayView1D<float, Stride1D.Dense> depthView,
            ArrayView1D<int, Stride1D.Dense> frameView, ArrayView1D<float, Stride1D.Dense> rangeView, int pixels, int direct)
        {
            _flicker ??= accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>(ThreeDKernels.FlickerKernel);
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
            _flicker(pixels, depthView, rangeView, _minMax!.View, _flickerPrev.View.SubView(0, pixels),
                _flickerPrevRaw!.View.SubView(0, pixels), frameView, _flickerPrevFrame!.View.SubView(0, pixels),
                _flickerAcc.View, direct, hasPrev ? 1 : 0);
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
            _rangeSmooth = null; _smoothRange = null; _rangeHasState = false;
            try { _flickerPrevFrame?.Dispose(); } catch { }
            try { _flickerPrevRaw?.Dispose(); } catch { }
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
