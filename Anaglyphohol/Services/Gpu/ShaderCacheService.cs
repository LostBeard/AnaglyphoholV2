using System.Diagnostics;
using SpawnDev.ILGPU;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension.Services;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// Keeps the WebGPU kernel shaders ILGPU compiles (WGSL + dispatch metadata) in the extension's OWN storage
    /// (storage.local: one store for every site the content script runs on), so a page registers them instead of
    /// compiling them. A stored kernel loads as an early cache hit: no IR, no IR optimization, no WGSL generation.
    /// </summary>
    /// <remarks>
    /// MEASURED 2026-10-04 (cold start, Blazor AOT, CPU profile): ~770 ms of the ~2 s to a page's first 3D image was
    /// ILGPU compiling 119 kernels (DAv3); VDA + DAv3 is 131 kernels, ~3 MB of WGSL. Only kernels this device really
    /// ran are stored, for this device's capability profile (TJ: "only the code they need is generated"). The store is
    /// filled at install / update by the background's warm-up and topped up by any page that compiles a kernel it lacks.
    /// An export from a different SpawnDev.ILGPU build is ignored on import, and a profile that does not match this
    /// device never hits: the runtime compiler stays the fallback, so a stale or foreign store can only cost time.
    /// </remarks>
    public sealed class ShaderCacheService
    {
        const string StorageKey = "ilgpuShaderCache";
        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BrowserExtensionService;
        Task? _import;
        int _storedCount;            // exportable kernels the store holds (as far as this runtime knows)
        bool _saving, _saveQueued;
        bool _logged;
        long _missesSeen = -1;

        public ShaderCacheService(SpawnJSRuntime js, BrowserExtensionService browserExtensionService)
        {
            JS = js;
            BrowserExtensionService = browserExtensionService;
        }

        /// <summary>Kernels registered from the store by <see cref="ImportAsync"/>.</summary>
        public int ImportedCount { get; private set; }
        /// <summary>Time <see cref="ImportAsync"/> took (read + register), ms.</summary>
        public double ImportMs { get; private set; }

        /// <summary>Registers the stored shaders, once per runtime. Await it before the first kernel loads.</summary>
        public Task ImportAsync() => _import ??= ImportCoreAsync();

        async Task ImportCoreAsync()
        {
            try
            {
                var local = BrowserExtensionService.Browser?.Storage?.Local;
                if (local == null) return;
                var sw = Stopwatch.StartNew();
                var json = await local.Get<string?>(StorageKey, null);
                if (!string.IsNullOrEmpty(json)) ImportedCount = ShaderArtifactSerializer.ImportCache(json);
                _storedCount = ImportedCount;
                ImportMs = sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: kernel shader store not read ({ex.Message}); kernels compile as usual.");
            }
        }

        /// <summary>
        /// After a rendered frame: logs this page's shader reuse once, and saves the store when this runtime compiled
        /// kernels it lacks (debounced; a page usually adds none once the store is warm).
        /// </summary>
        public void FrameRendered()
        {
            if (!_logged)
            {
                _logged = true;
                JS.Log($"Anaglyphohol: kernel shaders: {ImportedCount} restored ({ImportMs:0} ms), {ShaderArtifactCache.IrSkippedHits} reused, {ShaderArtifactCache.Misses} compiled.");
            }
            // a compile is a miss: check the store only when the miss count moved (not a snapshot per frame)
            long misses = ShaderArtifactCache.Misses;
            if (misses == _missesSeen) return;
            _missesSeen = misses;
            if (ExportableCount() > _storedCount) _ = SaveSoonAsync();
        }

        /// <summary>Writes this runtime's exportable shaders to the store now (the background's warm-up).</summary>
        public async Task SaveAsync()
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            if (local == null) return;
            int count = ExportableCount();
            var json = ShaderArtifactSerializer.ExportCache();
            await local.Set(StorageKey, json);
            _storedCount = count;
        }

        async Task SaveSoonAsync()
        {
            if (_saving) { _saveQueued = true; return; }
            _saving = true;
            try
            {
                do
                {
                    _saveQueued = false;
                    await Task.Delay(3000);   // let a burst of first frames (more input sizes, a second model) finish
                    await SaveAsync();
                } while (_saveQueued);
            }
            catch (Exception ex)
            {
                JS.Log($"Anaglyphohol: kernel shader store not saved ({ex.Message}).");
            }
            finally { _saving = false; }
        }

        /// <summary>Kernels <see cref="ShaderArtifactSerializer.ExportCache"/> would write (WebGPU, not runtime-emitted).</summary>
        static int ExportableCount()
        {
            int n = 0;
            foreach (var e in ShaderArtifactCache.Snapshot())
                if (e.Artifact.Source != null && !e.KernelId.Contains(")@", StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
