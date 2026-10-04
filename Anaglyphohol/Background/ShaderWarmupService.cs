using System.Diagnostics;
using Anaglyphohol.Services.Gpu;
using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU;
using SpawnDev.SpawnJS;

namespace Anaglyphohol.Background
{
    /// <summary>
    /// Compiles, once, the kernels the content script will need and stores their shaders (<see cref="ShaderCacheService"/>),
    /// so even the first page after an install or update loads them instead of compiling them. Runs in the background
    /// (Chrome service worker / Firefox background page) on runtime.onInstalled - TJ 2026-10-04: "pre-build the kernel
    /// code during the installed event ... and store it in the extensions local store ... only the code they need is
    /// generated". It runs both depth models on this device at the input sizes pages use: DAv3 for images (672) and for
    /// secondary videos, VDA for the main video (at the start level and the top level). A kernel it misses is compiled
    /// by the first page that needs it and added to the store then.
    /// </summary>
    public sealed class ShaderWarmupService
    {
        readonly SpawnJSRuntime JS;
        readonly GpuService Gpu;
        readonly DepthService Depth;
        readonly ShaderCacheService ShaderCache;
        Task? _running;

        public ShaderWarmupService(SpawnJSRuntime js, GpuService gpu, DepthService depth, ShaderCacheService shaderCache)
        {
            JS = js;
            Gpu = gpu;
            Depth = depth;
            ShaderCache = shaderCache;
        }

        /// <summary>Warms and stores the kernel shaders (once per background start; later calls share the run).</summary>
        public Task WarmAsync(string reason) => _running ??= WarmCoreAsync(reason);

        async Task WarmCoreAsync(string reason)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var accelerator = await Gpu.GetAcceleratorAsync();   // registers what the store already holds first
                // a 16:9 frame: the shape of most video and of the common image; its pixels do not matter
                const int frameW = 1280, frameH = 720;
                using var frame = accelerator.Allocate1D<int>(frameW * frameH);
                frame.MemSetToZero();
                using var minMax = accelerator.Allocate1D<float>(2);
                var runs = new (DepthModelKind Model, bool Video, int Level)[]
                {
                    (DepthModelKind.DAv3Small, false, 0),
                    (DepthModelKind.DAv3Small, true, DepthService.DefaultVideoLevel),
                    (DepthModelKind.VdaSmall, true, DepthService.DefaultVideoLevel),
                    (DepthModelKind.VdaSmall, true, DepthService.VideoLevels - 1),
                };
                foreach (var run in runs)
                {
                    var pipeline = await Depth.GetPipelineAsync(run.Model);
                    pipeline.ProcessResolution = DepthService.ProcessResolution(run.Video, run.Level);
                    var (inputW, inputH) = pipeline.ModelInputSize(frameW, frameH);
                    if (pipeline.IsStreaming) pipeline.ResetStream();
                    using var depth = accelerator.Allocate1D<float>(inputW * inputH);
                    await pipeline.EstimateGpuRawAsync(frame.View, frameW, frameH, depth.View, minMax.View, inputW, inputH);
                    // the forward only SUBMITS (Session.SkipCompletionWait): finish it before its buffer is released
                    await accelerator.SynchronizeAsync();
                }
                Depth.Dispose();   // the models' GPU memory: nothing else in the background uses them
                await ShaderCache.SaveAsync();
                JS.Log($"Anaglyphohol: kernel shaders prepared ({reason}): {ShaderArtifactCache.Count} stored, {ShaderArtifactCache.Misses} compiled, in {sw.Elapsed.TotalMilliseconds:0} ms.");
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: kernel shader warm-up skipped ({ex.Message}); pages compile and store them instead.");
            }
        }
    }
}
