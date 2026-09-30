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

    /// <summary>What one rendered frame cost, for the stats overlay and the FPS-adaptive depth scale.</summary>
    public readonly record struct FrameStats(int Width, int Height, int DepthWidth, int DepthHeight, double DepthMs, double RenderMs);

    /// <summary>
    /// One frame, source element -> screen, on one accelerator:
    /// <c>IExternalImageCopier</c> (element -> packed RGBA buffer, GPU-side) -> <c>DepthEstimationPipeline.EstimateGpuRawAsync</c>
    /// (raw depth at frame resolution, stays on the device) -> <see cref="ThreeDKernels"/> -> <c>ICanvasRenderer.PresentAsync</c>.
    /// The only GPU->CPU traffic is the pipeline's 8-byte depth min/max.
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
        MemoryBuffer1D<float, Stride1D.Dense>? _profiles;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float, float, float, int>? _anaglyph;
        Action<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float>? _twoDZ;

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
        /// <exception cref="JSException">The source is tainted (cross-origin without CORS) - the browser refuses its pixels.</exception>
        public async Task<FrameStats> RenderAsync(GPUCopyExternalImageSource source, int width, int height, ICanvasRenderer target,
            ThreeDMode mode, float level3D, float focus3D, bool video, float depthScale)
        {
            var accelerator = await EnsureAcceleratorAsync();
            var pipeline = await Depth.GetPipelineAsync();
            var model = Depth.Model;
            int pixels = width * height;

            if (_frame == null || _frame.Length < pixels)
            {
                _frame?.Dispose();
                _frame = accelerator.Allocate1D<int>(pixels);
            }
            var frameView = _frame.View.SubView(0, pixels);
            Gpu.GetCopier(accelerator).CopyToBuffer(source, width, height, frameView);

            var sw = Stopwatch.StartNew();
            if (model == DepthModelKind.DAv3Small) pipeline.ProcessResolution = DepthService.ProcessResolution(video, depthScale);
            var (inputW, inputH) = pipeline.ModelInputSize(width, height);
            var (rawDepth, minDepth, maxDepth, depthW, depthH) = await pipeline.EstimateGpuRawAsync(frameView, width, height, width, height);
            double depthMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            try
            {
                if (depthW != width || depthH != height)
                    throw new InvalidOperationException($"depth map is {depthW}x{depthH}, frame is {width}x{height}");
                if (_output == null || _output.Extent.X != width || _output.Extent.Y != height)
                {
                    _output?.Dispose();
                    _output = accelerator.Allocate2DDenseX<int>(new Index2D(width, height));
                }
                ArrayView1D<int, Stride1D.Dense> outputView = _output.View.BaseView;
                bool direct = DepthService.IsDirectDepth(model);
                var (a, b) = ThreeDKernels.DisparityScaleBias(minDepth, maxDepth, direct);
                if (mode == ThreeDMode.Dimenco2DZ)
                {
                    _twoDZ!(new Index2D(width, height), frameView, rawDepth.View, outputView, width, direct ? 1 : 0, a, b);
                }
                else
                {
                    int profile = mode == ThreeDMode.GreenMagenta ? AnaglyphProfiles.GreenMagenta : AnaglyphProfiles.RedCyan;
                    float separationPx = SepMax * Math.Clamp(level3D, 0f, 1f) * width;
                    _anaglyph!(new Index2D(width, height), frameView, rawDepth.View, _profiles!.View, outputView,
                        width, direct ? 1 : 0, a, b, separationPx, Math.Clamp(focus3D, 0f, 1f), profile * ThreeDKernels.ProfileStride);
                }
                // PresentAsync submits the pending kernels before its render pass reads the output.
                await target.PresentAsync(_output);
            }
            finally
            {
                // Safe after PresentAsync: the kernels that read it are already submitted.
                rawDepth.Dispose();
            }
            return new FrameStats(width, height, inputW, inputH, depthMs, sw.Elapsed.TotalMilliseconds);
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
                _anaglyph = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float, float, float, int>(ThreeDKernels.AnaglyphKernel);
                _twoDZ = accelerator.LoadAutoGroupedStreamKernel<Index2D, ArrayView1D<int, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<int, Stride1D.Dense>, int, int, float, float>(ThreeDKernels.TwoDZKernel);
            }
            return accelerator;
        }

        void ReleaseDeviceResources()
        {
            // After a device loss these are already dead; Dispose must not throw either way.
            try { _frame?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
            try { _profiles?.Dispose(); } catch { }
            _frame = null; _output = null; _profiles = null;
            _anaglyph = null; _twoDZ = null;
            _accelerator = null;
        }

        public void Dispose() => ReleaseDeviceResources();
    }
}
