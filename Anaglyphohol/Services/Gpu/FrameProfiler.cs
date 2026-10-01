using SpawnDev.ILGPU.ML.Graph;
using SpawnDev.ILGPU.WebGPU.Backend;
using System.Diagnostics;
using System.Text;

namespace Anaglyphohol.Services.Gpu
{
    /// <summary>
    /// DIAGNOSTIC: where one rendered frame's host time goes, using the profiling counters SpawnDev.ILGPU (WebGPUBackend)
    /// and SpawnDev.ILGPU.ML (GraphExecutor) already keep - the same split as ILGPU.ML's DirectForwardProfile test, but
    /// measured in the real extension on a real video frame. Only runs when a profile is requested (Stats mode, the
    /// <c>anaglyphohol-profile-request</c> attribute set by <c>_tools/profile-frame.js</c>); normal frames never touch it.
    /// </summary>
    public sealed class FrameProfiler
    {
        readonly bool _ops;
        readonly bool _prevDispatchProfiling;
        readonly int _gc0, _gc1, _gc2;
        readonly long _alloc0;
        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly StringBuilder _sb = new();
        double _last;
        double _lastSyncMs;
        long _lastSyncCount;

        /// <param name="ops">Also time every graph node by op type (adds a stopwatch per node, so the frame runs slower).</param>
        public FrameProfiler(bool ops)
        {
            _ops = ops;
            _prevDispatchProfiling = WebGPUBackend.EnableDispatchProfiling;
            WebGPUBackend.EnableDispatchProfiling = true;
            WebGPUBackend.ResetDispatchProfiling();
            if (ops) GraphExecutor.OpProfile = new();
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _alloc0 = GC.GetTotalAllocatedBytes(false);
        }

        /// <summary>Records the host time since the previous mark under <paramref name="phase"/>.</summary>
        public void Mark(string phase)
        {
            double now = _sw.Elapsed.TotalMilliseconds;
            _sb.Append($"{phase}={now - _last:F1}ms");
            // GPU sync waits inside this phase (the backend counts every awaited SynchronizeAsync)
            long syncN = WebGPUBackend.ProfileSyncWaitCount - _lastSyncCount;
            if (syncN > 0) _sb.Append($"(sync {WebGPUBackend.ProfileSyncWaitMs - _lastSyncMs:F1}ms x{syncN})");
            _sb.Append(' ');
            _lastSyncMs = WebGPUBackend.ProfileSyncWaitMs;
            _lastSyncCount = WebGPUBackend.ProfileSyncWaitCount;
            _last = now;
        }

        /// <summary>Stops profiling and returns the report (one line per fact, '|' separated for an HTML attribute).</summary>
        public string Finish()
        {
            double total = _sw.Elapsed.TotalMilliseconds;
            WebGPUBackend.EnableDispatchProfiling = _prevDispatchProfiling;
            var lines = new List<string> { $"PHASES {_sb}total={total:F1}ms" };
            long n = WebGPUBackend.ProfileCpuDispatchCount;
            double cpu = WebGPUBackend.ProfileCpuShaderResolveMs + WebGPUBackend.ProfileCpuArgBuildMs + WebGPUBackend.ProfileCpuBindGroupMs + WebGPUBackend.ProfileCpuEncodeMs;
            lines.Add($"DISPATCH n={n} cpu={cpu:F1}ms ({(n > 0 ? cpu * 1000 / n : 0):F0}us each) = shader {WebGPUBackend.ProfileCpuShaderResolveMs:F1} + args {WebGPUBackend.ProfileCpuArgBuildMs:F1} + bindGroup {WebGPUBackend.ProfileCpuBindGroupMs:F1} (create {WebGPUBackend.ProfileCpuBindGroupCreateMs:F1}) + encode {WebGPUBackend.ProfileCpuEncodeMs:F1}");
            lines.Add($"WAITS sync={WebGPUBackend.ProfileSyncWaitMs:F1}ms x{WebGPUBackend.ProfileSyncWaitCount} readback={WebGPUBackend.ProfileReadbackWaitMs:F1}ms x{WebGPUBackend.ProfileReadbackWaitCount} submit={WebGPUBackend.ProfileBatchSubmitMs:F1}ms x{WebGPUBackend.ProfileBatchSubmitCount}");
            lines.Add($"EXEC total={GraphExecutor.LastRunTotalMs:F1}ms folded={GraphExecutor.LastRunFoldedNodes} [{GraphExecutor.LastRunFoldState}] drains={GraphExecutor.LastRunSyncDrainCount} ({GraphExecutor.LastRunSyncDrainMs:F1}ms, byte-cap {GraphExecutor.LastRunSyncDrainByBytesCount}, peak pending {GraphExecutor.LastRunPeakPendingReleaseBytes / 1048576.0:F1} MiB, queue-ordered {GraphExecutor.QueueOrderedDrains}) readbacks={GraphExecutor.LastRunReadbackCount} ({GraphExecutor.LastRunReadbackMs:F1}ms) [{string.Join(", ", GraphExecutor.LastRunReadbackNames)}]");
            lines.Add($"GC gen0/1/2={GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2} alloc={(GC.GetTotalAllocatedBytes(false) - _alloc0) / 1048576.0:F2}MiB (RunKernel {WebGPUBackend.ProfileCpuAllocBytes / 1048576.0:F2}MiB)");
            if (_ops && GraphExecutor.OpProfile is { } prof)
            {
                GraphExecutor.OpProfile = null;
                var ph = GraphExecutor.OpPhaseMs;
                lines.Add($"NODEPHASES {string.Join(" + ", GraphExecutor.OpPhaseNames.Select((n, i) => $"{n} {ph[i]:F1}"))} ms");
                var asp = WebGPUBackend.ProfileCpuArgsSplitMs;
                lines.Add($"ARGS expand+manifest {asp[0]:F1} + views {asp[1]:F1} + scalars {asp[2]:F1} ms");
                foreach (var (op, e) in prof.OrderByDescending(kv => kv.Value.WallMs).Take(15))
                    lines.Add($"OP {op} n={e.Count} wall={e.WallMs:F1}ms ({e.WallMs * 1000 / e.Count:F0}us/node) dispatches={e.Dispatches} readbacks={e.Readbacks} drains={e.Drains}");
            }
            return string.Join(" | ", lines);
        }
    }
}
