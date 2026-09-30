using ILGPU;
using ILGPU.Runtime;
using SpawnDev.ILGPU;
using SpawnDev.ILGPU.ML;
using SpawnDev.ILGPU.Rendering;
using SpawnDev.ILGPU.WebGPU;
using SpawnDev.SpawnJS;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// Owns the ONE ILGPU accelerator the content script uses. Depth estimation (ILGPU.ML), the 3D kernels and the
    /// canvas presentation all share it, so a frame never leaves the device between them - the thing Transformers.js
    /// could not do (its ONNX runtime would not share its GPUDevice).
    /// </summary>
    /// <remarks>
    /// Created lazily by the first frame that needs it (a page with Anaglyphohol disabled never touches the GPU).
    /// WebGPU is required: it is the only browser backend with a GPU-side image copy
    /// (<see cref="WebGPUExternalImageCopier"/>) and the speed video needs.
    /// </remarks>
    public sealed class GpuService : IDisposable
    {
        readonly SpawnJSRuntime JS;
        Context? _context;
        Task<WebGPUAccelerator>? _acceleratorTask;
        IExternalImageCopier? _copier;

        /// <summary>Raised after the GPU device was lost and everything built on it must be rebuilt.</summary>
        public event Action? OnDeviceLost;

        public GpuService(SpawnJSRuntime js)
        {
            JS = js;
        }

        /// <summary>The accelerator, if it has been created and is alive.</summary>
        public WebGPUAccelerator? Accelerator { get; private set; }

        /// <summary>Last initialization error (no WebGPU adapter, device creation failed, ...).</summary>
        public string? Error { get; private set; }

        /// <summary>Creates (once) and returns the WebGPU accelerator.</summary>
        public Task<WebGPUAccelerator> GetAcceleratorAsync()
        {
            if (Accelerator != null && Accelerator.IsDeviceLost) HandleDeviceLost();
            return _acceleratorTask ??= CreateAsync();
        }

        async Task<WebGPUAccelerator> CreateAsync()
        {
            try
            {
                if (_context == null)
                {
                    var builder = MLContext.Create();
                    await builder.AllAcceleratorsAsync();
                    _context = builder.ToContext();
                }
                var devices = _context.GetDevices<WebGPUILGPUDevice>();
                if (devices.Count == 0) throw new NotSupportedException("WebGPU is not available in this browser (no adapter).");
                var accelerator = (WebGPUAccelerator)await devices[0].CreateAcceleratorAsync(_context);
                Accelerator = accelerator;
                Error = null;
                return accelerator;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _acceleratorTask = null;   // allow a later retry
                throw;
            }
        }

        /// <summary>The frame copier for the current accelerator (source element -> packed RGBA device buffer).</summary>
        public IExternalImageCopier GetCopier(WebGPUAccelerator accelerator) => _copier ??= ExternalImageCopier.Create(accelerator);

        /// <summary>Drops the lost device and everything tied to it; the next <see cref="GetAcceleratorAsync"/> rebuilds.</summary>
        public void HandleDeviceLost()
        {
            JS.Log("Anaglyphohol: WebGPU device lost - rebuilding on the next frame");
            _copier?.Dispose(); _copier = null;
            // The lost accelerator's resources are gone with the device; do not touch them further.
            Accelerator = null;
            _acceleratorTask = null;
            OnDeviceLost?.Invoke();
        }

        public void Dispose()
        {
            _copier?.Dispose(); _copier = null;
            Accelerator?.Dispose(); Accelerator = null;
            _context?.Dispose(); _context = null;
        }
    }
}
