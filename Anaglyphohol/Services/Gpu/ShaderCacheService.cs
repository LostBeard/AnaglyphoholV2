using System.Diagnostics;
using SpawnDev.ILGPU;
using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.BrowserExtension;
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
        // the background warm-up's outcome, one line: a page reports it (Firefox shows no background console easily)
        const string WarmupKey = "ilgpuShaderWarmup";
        // a page's FIRST conversion in progress (unix ms when it started, 0 = none): the warm-up gives way to it
        const string PageBusyKey = "anaglyphoholPageBusy";
        /// <summary>A page busy mark older than this is stale (a page closed mid-way, a first frame that failed).</summary>
        public static readonly TimeSpan PageBusyStale = TimeSpan.FromSeconds(20);
        // the background warm-up in progress (unix ms when it started, 0 = none): a page with an EMPTY store waits for
        // the warm-up's first save instead of compiling the same kernels next to it
        const string WarmupActiveKey = "ilgpuShaderWarmupActive";
        // unix ms of the store's last save: a waiting page polls this small key, not the ~3 MB store
        const string StoreRevKey = "ilgpuShaderCacheRev";
        /// <summary>How long a page with an empty store waits for an active warm-up's first save.</summary>
        public static readonly TimeSpan WaitForWarmupStore = TimeSpan.FromSeconds(6);
        /// <summary>A warm-up active mark older than this is stale (a background unloaded mid-way).</summary>
        static readonly TimeSpan WarmupActiveStale = TimeSpan.FromSeconds(90);

        /// <summary>Time a page waited for the warm-up's store (<see cref="WaitForWarmupStore"/>), ms; 0 = did not wait.</summary>
        public double WaitedForWarmupMs { get; private set; }
        readonly SpawnJSRuntime JS;
        readonly BrowserExtensionService BrowserExtensionService;
        Task? _import;
        int _storedCount;            // exportable kernels the store holds (as far as this runtime knows)
        bool _saving, _saveQueued;
        bool _logged;
        bool _pageBusySet;
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
        /// <summary>The last background warm-up's outcome as stored (see <see cref="SaveWarmupNoteAsync"/>), if any.</summary>
        public string? WarmupNote { get; private set; }

        /// <summary>Registers the stored shaders, once per runtime. Await it before the first kernel loads.</summary>
        public Task ImportAsync() => _import ??= ImportCoreAsync();

        async Task ImportCoreAsync()
        {
            try
            {
                var local = BrowserExtensionService.Browser?.Storage?.Local;
                if (local == null) return;
                bool page = BrowserExtensionService.ExtensionMode == ExtensionMode.Content;
                var sw = Stopwatch.StartNew();
                long rev = await local.Get<long>(StoreRevKey, 0L);
                var json = await local.Get<string?>(StorageKey, null);
                if (!string.IsNullOrEmpty(json)) ImportedCount = ShaderArtifactSerializer.ImportCache(json);
                if (page && ImportedCount == 0)
                {
                    // Right after an install / update the store holds nothing this build can use while the background
                    // warm-up compiles it. MEASURED 2026-10-05: every first page after an install compiled 118-122 kernels
                    // NEXT TO the warm-up compiling the same 120 (and the warm-up saved only after all 4 runs); every
                    // pipeline goes through Chrome's one GPU process. Now the warm-up saves after each run (bumping
                    // StoreRevKey), and a page waits - bounded - for that save and imports it instead.
                    long active = await local.Get<long>(WarmupActiveKey, 0L);
                    if (active > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - active < WarmupActiveStale.TotalMilliseconds)
                    {
                        while (ImportedCount == 0 && sw.Elapsed < WaitForWarmupStore)
                        {
                            await Task.Delay(200);
                            long now = await local.Get<long>(StoreRevKey, 0L);   // small key: the 3 MB store is read only on a new save
                            if (now == rev) continue;
                            rev = now;
                            json = await local.Get<string?>(StorageKey, null);
                            if (!string.IsNullOrEmpty(json)) ImportedCount = ShaderArtifactSerializer.ImportCache(json);
                        }
                        WaitedForWarmupMs = sw.Elapsed.TotalMilliseconds;
                    }
                }
                if (page)
                {
                    // From here this page loads a model and registers/compiles kernels for its first frame: the warm-up
                    // waits (WaitWhilePageBusyAsync) instead of competing. Set only now, AFTER any wait above - a page
                    // waiting for the warm-up must not also hold the warm-up back.
                    _pageBusySet = true;
                    _ = local.Set(PageBusyKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                }
                WarmupNote = await local.Get<string?>(WarmupKey, null);
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
                if (_pageBusySet)
                {
                    _pageBusySet = false;
                    _ = BrowserExtensionService.Browser?.Storage?.Local?.Set(PageBusyKey, 0L);
                }
                JS.Log($"Anaglyphohol: kernel shaders: {ImportedCount} restored ({ImportMs:0} ms{(WaitedForWarmupMs > 0 ? $", {WaitedForWarmupMs:0} of them waiting for the warm-up's store" : "")}), {ShaderArtifactCache.IrSkippedHits} reused, {ShaderArtifactCache.Misses} compiled. Warm-up: {WarmupNote ?? "none recorded"}.");
            }
            // a compile is a miss: check the store only when the miss count moved (not a snapshot per frame)
            long misses = ShaderArtifactCache.Misses;
            if (misses == _missesSeen) return;
            _missesSeen = misses;
            if (ExportableCount() > _storedCount) _ = SaveSoonAsync();
        }

        /// <summary>
        /// Records the background warm-up's state (one line, with when, for this SpawnDev.ILGPU build) for pages to report
        /// and for <see cref="WarmupDoneForThisBuildAsync"/>.
        /// </summary>
        public async Task SaveWarmupNoteAsync(string note)
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            if (local != null) await local.Set(WarmupKey, $"{note} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}, ILGPU {ShaderArtifactSerializer.LibraryVersion})");
        }

        /// <summary>
        /// True when a warm-up was ATTEMPTED for this SpawnDev.ILGPU build (finished, failed or cut short). Only a fresh
        /// build or runtime.onInstalled runs it again: a warm-up that cannot finish here must not reload both models on
        /// every background wake (pages still add the kernels they compile).
        /// </summary>
        public async Task<bool> WarmupDoneForThisBuildAsync()
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            if (local == null) return true;   // nowhere to store: nothing to warm
            var note = await local.Get<string?>(WarmupKey, null);
            return note != null && note.EndsWith($"ILGPU {ShaderArtifactSerializer.LibraryVersion})", StringComparison.Ordinal);
        }

        /// <summary>Marks the background warm-up active (pages with an empty store then wait for its first save) or done.</summary>
        public async Task SetWarmupActiveAsync(bool active)
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            if (local != null) await local.Set(WarmupActiveKey, active ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0L);
        }

        /// <summary>
        /// The background warm-up's yield: returns once no page is in its first conversion (busy mark cleared or older
        /// than <see cref="PageBusyStale"/>), or after <paramref name="maxWait"/>. Returns how long it waited.
        /// </summary>
        public async Task<TimeSpan> WaitWhilePageBusyAsync(TimeSpan maxWait)
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            var sw = Stopwatch.StartNew();
            if (local == null) return sw.Elapsed;
            while (sw.Elapsed < maxWait)
            {
                long since = await local.Get<long>(PageBusyKey, 0L);
                if (since <= 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since > PageBusyStale.TotalMilliseconds) break;
                await Task.Delay(250);
            }
            return sw.Elapsed;
        }

        /// <summary>Writes this runtime's exportable shaders to the store now (the background's warm-up).</summary>
        public async Task SaveAsync()
        {
            var local = BrowserExtensionService.Browser?.Storage?.Local;
            if (local == null) return;
            int count = ExportableCount();
            var json = ShaderArtifactSerializer.ExportCache();
            await local.Set(StorageKey, json);
            await local.Set(StoreRevKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
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

        /// <summary>Kernels <see cref="ShaderArtifactSerializer.ExportCache"/> would write (the same rule).</summary>
        static int ExportableCount()
        {
            int n = 0;
            foreach (var e in ShaderArtifactCache.Snapshot())
                if (e.Artifact.Source != null && ShaderArtifactSerializer.IsExportable(e.KernelId)) n++;
            return n;
        }
    }
}
