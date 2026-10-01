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
        MemoryBuffer1D<float, Stride1D.Dense>? _profiles;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int, float, float, int>? _anaglyph;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, int, int>? _twoDZ;

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
            if (_output == null || _output.Extent.X != width || _output.Extent.Y != height)
            {
                _output?.Dispose();
                _output = accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
            }
            ArrayView1D<int, Stride1D.Dense> outputView = _output.View.BaseView;
            int direct = DepthService.IsDirectDepth(model) ? 1 : 0;
            if (mode == ThreeDMode.Dimenco2DZ)
            {
                _twoDZ!(new Index2D(width, height), frameView, depthView, outputView, _minMax.View, width, direct);
            }
            else
            {
                int profile = mode == ThreeDMode.GreenMagenta ? AnaglyphProfiles.GreenMagenta : AnaglyphProfiles.RedCyan;
                float separationPx = SepMax * Math.Clamp(level3D, 0f, 1f) * width;
                _anaglyph!(new Index2D(width, height), frameView, depthView, _profiles!.View, outputView, _minMax.View,
                    width, direct, separationPx, Math.Clamp(focus3D, 0f, 1f), profile * ThreeDKernels.ProfileStride);
            }
            // PresentAsync submits the pending kernels before its render pass reads the output.
            await target.PresentAsync(_output);
            if (profiler != null)
            {
                profiler.Mark("render+present");
                await accelerator.SynchronizeAsync();
                profiler.Mark("gpuTail");
            }
            return new FrameStats(width, height, inputW, inputH, depthMs, sw.Elapsed.TotalMilliseconds, recompileMs);
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
            _frame = null; _output = null; _profiles = null; _depth = null; _minMax = null;
            _anaglyph = null; _twoDZ = null;
            _accelerator = null;
        }

        public void Dispose() => ReleaseDeviceResources();
    }
}
