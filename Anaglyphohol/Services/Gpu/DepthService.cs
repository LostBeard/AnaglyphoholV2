using Action = System.Action;
using SpawnDev.ILGPU.ML.Pipelines;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension.Services;
using SpawnDev.SpawnJS.JSObjects;
using SpawnDev.SpawnJS.Toolbox;

namespace Anaglyphohol.Services.Gpu
{
    public enum DepthModelKind
    {
        /// <summary>Depth Anything V3 Small: metric DEPTH (high = far), multi-view model, native-aspect input.</summary>
        /// <remarks>The only REQUIRED bundled model since 2026-10-02 (TJ): DAv2 Small was dropped (-95 MB from the extension).</remarks>
        DAv3Small,
        /// <summary>
        /// Video Depth Anything Small (Apache-2.0, ByteDance), STREAMING: relative DISPARITY (high = near) that is
        /// temporally consistent by construction - each frame attends to cached hidden states of up to 31 earlier ones
        /// (<see cref="SpawnDev.ILGPU.ML.Pipelines.VideoDepthAnythingStream"/>). Our ONNX export of the streaming step
        /// (SpawnDev.ILGPU.ML/_research/vda-export); optional - loaded only when present in app/models.
        /// </summary>
        VdaSmall,
    }

    /// <summary>
    /// Depth estimation on <see cref="GpuService"/>'s accelerator via SpawnDev.ILGPU.ML. The model files ship INSIDE the
    /// extension (app/models/..., fetched at build time by _tools/fetch-models.ps1), so every site loads the same local
    /// copy: no network, and no per-site copy (a content script's OPFS / cache belongs to the HOST page's origin).
    /// </summary>
    /// <remarks>
    /// Weights stream fetch -> Blob -> <see cref="BlobStream"/> (a JS-backed IJSReadStream) -> GPU; they never enter the
    /// .NET heap.
    /// </remarks>
    public sealed class DepthService : IDisposable
    {
        readonly SpawnJSRuntime JS;
        readonly GpuService Gpu;
        readonly BrowserExtensionService BrowserExtensionService;
        Task<DepthEstimationPipeline>? _pipelineTask;
        DepthModelKind _pipelineModel;

        /// <summary>DAv3's reference input long side, and the square the session is bound at.</summary>
        public const int DAv3BindSize = 504;
        /// <summary>DAv3 long side for still images: 672 (48 patches) recovers detail 504 loses and runs on every path.</summary>
        public const int DAv3ImageResolution = 672;
        /// <summary>
        /// DAv3 long side for video at the top level. Video steps in whole 56 px (4 patch) levels from
        /// <see cref="MinVideoResolution"/> up to this: every adaptive step is exactly one new input shape, and no step
        /// is a no-op. Each input shape the session has not seen costs a recompile + a first forward
        /// (ILGPU.ML caches executors per shape), so the level set is kept small on purpose.
        /// </summary>
        public const int DAv3VideoResolution = 504;
        public const int VideoResolutionStep = 56;
        public const int MinVideoResolution = 168;
        /// <summary>Number of video depth levels: 168, 224, ..., 504.</summary>
        public const int VideoLevels = (DAv3VideoResolution - MinVideoResolution) / VideoResolutionStep + 1;
        /// <summary>Where a new video starts: 224, cheap enough for a first frame on any GPU; the cost loop climbs from there.</summary>
        public const int DefaultVideoLevel = 1;

        public DepthService(SpawnJSRuntime js, GpuService gpu, BrowserExtensionService browserExtensionService)
        {
            JS = js;
            Gpu = gpu;
            BrowserExtensionService = browserExtensionService;
            Gpu.OnDeviceLost += () => _pipelineTask = null;   // its weights died with the device
        }

        /// <summary>Which model to use. Changing it loads the other model on the next frame.</summary>
        public DepthModelKind Model { get; set; } = DepthModelKind.DAv3Small;

        /// <summary>True while a model is loading.</summary>
        public bool Loading { get; private set; }
        /// <summary>Load progress 0-100 while <see cref="Loading"/>, else null.</summary>
        public float? LoadProgress { get; private set; }
        /// <summary>Last load error.</summary>
        public string? Error { get; private set; }
        /// <summary>Raised when <see cref="Loading"/> / <see cref="LoadProgress"/> / <see cref="Error"/> change.</summary>
        public event Action? OnStateChange;

        /// <summary>True when the loaded model outputs depth (high = far) rather than disparity (high = near).</summary>
        public static bool IsDirectDepth(DepthModelKind kind) => kind == DepthModelKind.DAv3Small;

        /// <summary>True when the model keeps temporal state across video frames (reset it at a cut, seek or new video).</summary>
        public static bool IsStreaming(DepthModelKind kind) => kind == DepthModelKind.VdaSmall;

        /// <summary>The pipeline for <see cref="Model"/>, loading it on first use.</summary>
        public Task<DepthEstimationPipeline> GetPipelineAsync()
        {
            if (_pipelineTask != null && _pipelineModel != Model)
            {
                var old = _pipelineTask;
                _pipelineTask = null;
                _ = DisposeWhenLoaded(old);
            }
            if (_pipelineTask == null)
            {
                _pipelineModel = Model;
                _pipelineTask = LoadAsync(Model);
            }
            return _pipelineTask;
        }

        /// <summary>Model input long side for a frame: still images at full detail, video at its adaptive level.</summary>
        public static int ProcessResolution(bool video, int videoLevel)
        {
            if (!video) return DAv3ImageResolution;
            return MinVideoResolution + VideoResolutionStep * Math.Clamp(videoLevel, 0, VideoLevels - 1);
        }

        async Task<DepthEstimationPipeline> LoadAsync(DepthModelKind kind)
        {
            SetState(true, 0, null);
            try
            {
                var accelerator = await Gpu.GetAcceleratorAsync();
                void Progress(string stage, int percent) => SetState(true, percent, null);
                DepthEstimationPipeline pipeline;
                if (kind == DepthModelKind.VdaSmall)
                {
                    // Single-file model (~117 MB). Only pixel_values is bound: the cache inputs' dims follow the frame and
                    // the stream's window (the pipeline drives them; see VideoDepthAnythingStream).
                    using var vda = await OpenModelFile("video-depth-anything-small/model.onnx");
                    pipeline = await DepthEstimationPipeline.CreateFromStreamsAsync(accelerator, vda, null, Progress,
                        new Dictionary<string, int[]> { ["pixel_values"] = new[] { 1, 1, 3, DAv3BindSize, DAv3BindSize } });
                    if (!pipeline.IsStreaming) throw new InvalidDataException("video-depth-anything-small/model.onnx is not the streaming step graph.");
                }
                else
                {
                    // External-data model: model.onnx is the ~640 KB graph, model.onnx_data the ~105 MB weights.
                    using var model = await OpenModelFile("depth-anything-v3-small/onnx/model.onnx");
                    using var weights = await OpenModelFile("depth-anything-v3-small/onnx/model.onnx_data");
                    pipeline = await DepthEstimationPipeline.CreateFromStreamsAsync(accelerator, model, weights, Progress,
                        new Dictionary<string, int[]> { ["pixel_values"] = new[] { 1, 1, 3, DAv3BindSize, DAv3BindSize } });
                }
                // NativeAspect: the tensor follows the picture's aspect (no letterbox pad). Measured more accurate for
                // DAv3 than a square letterbox, which it reads as picture content.
                pipeline.ResizeMode = DepthResizeMode.NativeAspect;
                pipeline.ProcessResolution = DAv3VideoResolution;
                // Plain forward only - no graph capture/replay (TJ 2026-09-30: the uncaptured forward must be fast on
                // its own; ILGPU.ML 5.2.38+ makes it so). Capture also re-records per new input shape, and the adaptive
                // video resolution changes the shape.
                pipeline.EnableGraphCapture = false;
                SetState(false, null, null);
                return pipeline;
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: depth model {kind} failed to load: {ex.Message}");
                _pipelineTask = null;   // allow a retry
                SetState(false, null, ex.Message);
                throw;
            }
        }

        /// <summary>A bundled model file as a JS-backed stream (the bytes stay in JS until they reach the GPU).</summary>
        async Task<OwnedBlobStream> OpenModelFile(string path)
        {
            var url = BrowserExtensionService.GetURL($"models/{path}");
            using var response = await JS.Fetch(url);
            if (!response.Ok) throw new FileNotFoundException($"Bundled model file missing: {url} (HTTP {response.Status}). Run _tools/fetch-models.ps1 and rebuild.");
            var blob = await response.Blob();
            return new OwnedBlobStream(blob);
        }

        void SetState(bool loading, float? progress, string? error)
        {
            Loading = loading;
            LoadProgress = progress;
            Error = error;
            OnStateChange?.Invoke();
        }

        static async Task DisposeWhenLoaded(Task<DepthEstimationPipeline> task)
        {
            try { (await task).Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (_pipelineTask != null) _ = DisposeWhenLoaded(_pipelineTask);
            _pipelineTask = null;
        }

        /// <summary><see cref="BlobStream"/> that also releases its <see cref="Blob"/>.</summary>
        sealed class OwnedBlobStream : BlobStream
        {
            readonly Blob _blob;
            public OwnedBlobStream(Blob blob) : base(blob) { _blob = blob; }
            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                if (disposing) _blob.Dispose();
            }
        }
    }
}
