# Anaglyphohol - plans and test loop

Living tracker for the .NET 10 / SpawnJS / ILGPU.ML port (branch `spawnjs-ilgpu`). Approved plan: 2026-09-30 (Geordi).

## Architecture (after the port)

| Piece | What |
|---|---|
| Host | `SpawnJSAppBuilder` (no Blazor JS runtime). One WASM app, three contexts by `BrowserExtensionService.GetExtensionMode()`: content script, background worker, extension pages |
| UI | `SpawnDev.SpawnJS.RazorRenderer`: `ContentOverlay` in its own OPEN shadow root (content); `ExtensionPageApp` picks the page from `?$=` (extension pages) |
| Depth | `Services/Gpu/DepthService` - SpawnDev.ILGPU.ML `DepthEstimationPipeline`, DAv3 Small (default, NativeAspect) or DAv2 Small. Models BUNDLED in `wwwroot/models` (gitignored; `_tools/fetch-models.ps1`) |
| 3D | `Services/Gpu/ThreeDKernels` - ILGPU port of MultiView's GLSL (viewColor2DZ search, Dubois anaglyph, 2D+Z). One accelerator for depth + kernels + presentation (`GpuService`) |
| Frame in / out | `IExternalImageCopier` (SpawnDev.ILGPU 5.2.25, copyExternalImageToTexture -> buffer) / `WebGPUCanvasRenderer` |
| Dimenco | `Philips2DZHeader` (ported, byte-identical to MultiView.Dimenco) + `DimencoHeaderService` singleton |

## Test loop (all tooling in `_tools/`)

1. `powershell -ExecutionPolicy Bypass -File _tools\fetch-models.ps1` (once; the build fails loudly without models)
2. `Anaglyphohol\_buildRelease.bat nopause` -> `Anaglyphohol\bin\PublishRelease\chrome`
   ⚠️ AOT is the DEFAULT since 2026-10-01 (TJ): a Release publish AOT-compiles (over an hour, one core - launch it
   outside the agent shell). Fast dev loop: `dotnet publish -c Release -p:AnaglyphoholAot=false` (interpreter, ~1 min).
   Ship builds are AOT.
3. `powershell -ExecutionPolicy Bypass -File _tools\launch-chrome.ps1` - installed Chrome, port **9224**, profile
   `C:\Users\TJ\anaglyphohol-debug-profile`, test page server on http://localhost:8765/. Stop: `_tools\stop-chrome.ps1`.
   ⚠️ Chrome 151 IGNORES `--load-extension`. TJ loaded the unpacked extension into that profile once (2026-09-30,
   id `ffhohkfijjpeecdmjdcbbflphkdmlmho`); after that:
4. `dotnet run _tools/cdp.cs "chrome://extensions" file:_tools/reload-ext.js` after every publish
5. Probes: `cdp.cs <urlSubstr> file:probe-overlay.js` (overlay shadow root, toggles, tracked states, overlay canvases),
   `cdp.cs background.worker.js file:probe-sw.js` (held events released), `cdp.cs chrome://extensions file:ext-errors.js`
   (runtime errors; clear with `developerPrivate.deleteExtensionErrors`),
   `dotnet run _tools/cdp-console.cs <urlSubstr> <seconds> [reload]` (console + exceptions of EVERY context, including
   the content script's isolated world)
6. `_tools/testpage/blank.html` has no media: overlay / background checks that never touch the GPU.
7. `dotnet run -c Release --project _tools/kernel-tests` - kernels vs a GLSL-literal reference + Philips header vs the
   Dimenco oracle (CPU; `-- -gpu` adds CUDA/OpenCL). Proves math/indexing, NOT WGSL.

## Status

DONE / VERIFIED
- Port builds; content overlay renders in its shadow root with all toggles; extension page `?$=SystemInfo` renders;
  background worker wakes on a runtime message and releases held events; extension error list empty (2026-09-30).
- kernel-tests 38/38 (red-checked).
- Library fixes found by the port (all tested + red-checked, pushed, staged `-local`):
  SpawnJS.WebWorkers 08f3322 (csproj opt-out ignored -> no main.*.js), SpawnJS c2d6da6 (derived POCO marshalled as its
  base -> PBKDF2 salt dropped), SpawnJS ac3a6ae + BlazorJS 756120a (WebGPU colorSpace null / number), SpawnJS 0145a38 +
  BlazorJS cf1835a (flipY).

- 2026-09-30 evening: on RELEASED SpawnDev.SpawnJS 3.0.0 + SpawnDev.ILGPU 5.3.0 + SpawnDev.ILGPU.ML 5.3.0 (the
  uncaptured-forward speedups: DAv3 518 ~100 ms, DAv2 ~60 ms in the ML profile test). Test page: 4/4 media reach
  `anaglyph`, pixel probe shows the red/cyan split, background worker releases held events, extension errors empty.
  kernel-tests 38/38 on ILGPU 5.3.0. Graph capture/replay is OFF (`DepthService`: plain forward only, TJ's bar).
  No `-local` pins left: SpawnDev.SpawnJS.WebWorkers 2.2.0 (on SpawnJS 3.0.0, includes the opt-out fix) re-verified
  from a clean bin/obj - main.*.js emitted, 4/4 `anaglyph`, pixel split, SW releases held events, SystemInfo renders.
  The one entry in the extension error list is Chrome's own WARN (powerPreference ignored on Windows, crbug 369219127).
- Tooling: `stop-chrome.ps1` now closes Chrome GRACEFULLY (CDP Browser.close) and verifies - a force-kill broke the
  profile's service-worker registrations ("Service worker registration failed. Status code: 2", even for a trivial
  worker) until a graceful restart. `cdp-console.cs` marks the reload (Runtime.enable REPLAYS old console lines).
  `sw-catch.mjs` captures a service worker's startup exceptions; `clear-ext-errors.js` clears the error list.

NEXT (needs the GPU - shared with other agents' PMT sweeps; coordinate via _DevComms/board.md)
- Timing only when no peer CPU-heavy job is running (TJ 2026-09-30).
- DONE 2026-10-01: depth min/max on the GPU. The 3D kernels read [min, max] from a 2-float device view (kernel-tests
  44/44, both kernels red-checked); ThreeDRenderer owns the depth + min/max buffers and calls ILGPU.ML 5.3.1-local.4's
  `EstimateGpuRawAsync(rgba, w, h, rawDepthOut, minMaxOut, w, h)` (ML cfdd7597): no per-frame readback, no per-frame
  allocation. Verified in Chrome: 4/4 images `anaglyph`, red/cyan split, video renders 3D frames; page console = Chrome's
  powerPreference WARN + the test server's favicon 404 only (extension error list NOT re-checked this run).
- DONE 2026-10-01: video depth steps in whole 56 px LEVELS (168..504, 7 levels); a frame that recompiled a shape
  (`Session.LastRecompileMs > 0`) is left out of the cost average. Cost attribute carries `recompile=`.
  Timing tools: `_tools/sample-cost.js` (start; page-world MutationObserver, no CDP traffic in the window) +
  `_tools/read-cost.js` (summary).
- MEASURED 2026-10-01 (RTX 4070, Chrome, DAv3, plain forward, no peer load, 60 s window, 913 frames): video 640x360
  at the FLOOR level 168x98 = depth 46.2 ms median (p10 43.5, p90 50), 3D kernels 0.5 ms, 14.2 FPS (a 47 ms frame
  misses every other 30 fps video frame). Images at 672: ~194 ms warm + 100-120 ms per new-shape recompile.
  ⚠️ An earlier 45 s window ran into the free-tier 30 s video limit (the stats re-wrote the last frame's cost after
  it); localhost is now a SUPPORTED host (TrackedMedia.SupportedHosts, no limit, no overlay link - TJ 2026-10-01) and
  the cost attribute is written only for rendered frames, with seq=N.
- PROFILED 2026-10-01 (`_tools/profile-frame.js`, FrameProfiler; profiled frames run ~58 ms vs 46): the floor is
  HOST-bound, not GPU-bound. Per frame: 789 dispatches (same count as at 518 - fixed cost), dispatch CPU ~21 ms
  (26 us each; argument building ~15 ms of it: views 6-8, scalars 4-6, expand+manifest 4-5), ~1,900 executed graph
  nodes (prelude/inputs/shapes/rent/post bookkeeping), 15 submits ~3.6 ms, and GraphExecutor.RunAsync's FINAL
  SynchronizeAsync ~5.5 ms = the whole forward's GPU time, waited on every frame (no host/GPU overlap). 0 readbacks,
  0 allocations, GPU tail after present ~0.7 ms. Top ops by dispatches: Mul 156, Slice 113, Add 92, Concat 65,
  LayerNorm 48, FusedLinear 47, Neg 33 (the Mul/Slice/Neg/Concat/Add mix looks like rotate-half RoPE - NOT traced).
  All four levers are library work (ILGPU WebGPU arg build; ILGPU.ML executor per-node cost, elementwise fusion, the
  final sync) - coordinate with Tuvok, who holds ILGPU.ML.
- ORT-web / Transformers.js COMPARISON (2026-10-01, TJ: studying them is mandatory - memory fb-study-ort-web-and-transformersjs).
  Source read: onnxruntime-web 1.22 JSEP (vjs/anglyphoholv3/node_modules/onnxruntime-web/lib/wasm/jsep). Measured: TJS
  4.3.0 DAv3 with a WebGPU call counter (copy of ILGPU.ML tools/dav3/dav3-tjs.mjs), RTX 4070, system Chrome, per warm
  forward: 1,229 dispatchWorkgroups / createBindGroup / uniform writeBuffer, 986 beginComputePass, 1,054 clearBuffer,
  150 submits, 5 mapAsync readbacks. Warm 98x168 = 43 ms median (39 min), 518 = 57 ms (both under a concurrent AOT
  build - counts exact, times slightly high). Ours at 98x168: 789 dispatches, ~45 ms.
  => at the video floor we are at PARITY; ORT has the same ~40 ms fixed floor; ORT makes MORE calls but spends less host
  time per call (~35 vs ~57 us): their executor is C++ compiled to WASM, ours is interpreted .NET.
  => at 518 we are ~2x slower (91-125 vs 57 ms): there it is GPU kernel time, not orchestration.
  Their design: one pass for up to 16 dispatches then flush; whole-buffer bindings (no offsets); immediate encode;
  program cache keyed by shapes; fused contrib kernels (RotaryEmbedding, SkipLayerNorm, MHA, FastGelu, BiasAdd).
- AOT (2026-10-01): now the DEFAULT (TJ: ship AOT) = RunAOTCompilation + WasmStripILAfterAOT=false; opt out with
  `-p:AnaglyphoholAot=false`.
  ILGPU compiles its kernels from the KEPT IL - 4/4 images anaglyph, video renders, no errors. The old "AOT strips the
  IL" blocker was one switch. MEASURED, same build source, depth level PINNED to 168x98, 60-frame unprofiled sweeps,
  two brackets each, quiet machine (RTX 4070, Chrome):
  | sweep (ms median)            | interpreter | AOT  |
  | plain (full frame depth)     | 43.8        | 21.7 |
  | noexec (bookkeeping only)    | 9.8         | 4.7  |
  | nodispatch (no WebGPU work)  | 20.8        | 11.5 |
  | jsnodispatch (writes only)   | 38.7        | 17.5 |
  | jsnosubmit (no JS submit)    | 32.6        | 13.3 |
  Transformers.js 4.3.0 on the same machine, quiet: 98x168 = 42 ms, 518 = 58 ms. => AOT = 2.0x the interpreter and
  ~1.9x FASTER than Transformers.js at the video floor size.
  Costs: dotnet.native.wasm 55 MB (vs 3 MB) and a ~70-80 min single-core build (SpawnDev.ILGPU.ML.dll dominates).
  Under AOT the JS submit is ~40% of the frame: bind group + encode ~4.2 ms, per-dispatch scalar writeBuffer ~4.2 ms
  -> next cuts: per-batch scalar arena (one write per submit), bind-group reuse.
  Single compute pass per run of dispatches: ~1 ms with the interpreter, ~0 with AOT.
- SCALAR ARENAS + BIND-GROUP REUSE (2026-10-02, SpawnDev.ILGPU 5.3.2-local.2, ILGPU 7a203c0). AOT build (65 min, 35.8 MB
  dotnet.native.wasm), 168x98 pinned, 60-frame sweeps, quiet machine, RTX 4070. Sweep modes noarena / noreuse = the A/B arms
  in the SAME build:
  | sweep (ms median)              | 5.3.1 AOT | 5.3.2-local.2 AOT        |
  | plain (arena + reuse)          | 21.7      | 20.2 / 21.0 / 20.6       |
  | noreuse (arena, no bg cache)   | -         | 20.6 / 22.1              |
  | noarena (pooled, no bg cache)  | -         | 22.0 / 21.7              |
  | jsnodispatch                   | 17.5      | 16.7                     |
  | jsnosubmit                     | 13.3      | 12.5                     |
  => ~1.5 ms (7%). Smaller than hoped, and the probes say why:
  - JS time INSIDE submitBatch is only ~2.5 ms/frame now (probe: 15 calls, 15 queue submits, 789 dispatches, 108 copies,
    14 uploads per frame). plain - jsnosubmit (~8 ms) is mostly the GPU work itself, which jsnosubmit never runs.
  - The bind-group cache hits ~45% here (two generations of 8,192): under AOT the ML buffer pool hands out buffers in a
    rotation that cycles through ~55.7k distinct bind groups even at one input size (interpreter: ~3k - frames are slower,
    so buffers come back before the next frame asks). Generations of 65,536 -> 75-87% hits but only ~0.5 ms; no visible
    memory change. Kept 8,192. A deterministic per-frame buffer assignment in ILGPU.ML would make the keys repeat.
  - => the host cost left is C#: executor bookkeeping ~4.7 ms (noexec) + per-dispatch RunKernel argument build ~6.8 ms
    (nodispatch - noexec, ~8.6 us x 789). That is the next lever, not the JS submit.
- AOT BUILD TIME vs TrimMode (TJ 2026-10-02: try TrimMode=full; keep clean builds + full native optimization).
  TrimMode=full needs the APP rooted (TrimmerRootAssembly Anaglyphohol): trimmed, its DynamicComponent / LayoutView UI
  (ContentOverlay, ExtensionPageApp - the IL2110/IL2111 warnings, hidden by the Blazor SDK's default
  SuppressTrimAnalysisWarnings=true) came up collapsed and 3D never started. Rooted, it works (AOT build verified in Chrome).
  | build                  | managed wasm | assemblies | AOT build | dotnet.native.wasm |
  | TrimMode=partial       | 17.4 MB      | 51         | 65 min    | 35.8 MB            |
  | TrimMode=full + root   | 15.9 MB      | 49         | 65 min    | 35.5 MB            |
  Full only drops what partial left untouched (mostly SpawnDev.Phonemizer, 1.5 MB, unused here); ML / SpawnDev.ILGPU /
  ILGPU come out byte-identical. And the build time is NOT the assemblies: obj timestamps show every per-assembly .bc
  done in the first minute, the .o compiles 76 s (parallel), link + wasm-opt ~1 min, and aot-instances.dll.bc (Mono's
  DEDUPLICATED generic instantiations, one module, single-threaded) 08:35 -> 09:37 = 62 of the 65 min. That module is fed
  by the generic-heavy ILGPU / ILGPU.ML code; trimming does not touch it. Levers: fewer generic instantiations in ML/ILGPU,
  or WasmDedup=false (instances compiled per assembly, in parallel, at the cost of a bigger dotnet.native.wasm) - untried.
- OPEN: shape thrash is REAL. The test page needs 4 shapes (video + 3 image aspects) and ILGPU.ML keeps 3 executors:
  img-lazy (672x266, same shape as img-wide) recompiled again (120 ms) after eviction. Options (ILGPU.ML): configurable
  MaxShapeExecutors, coarser NativeAspect aspect buckets. Measure GPU memory per executor first.
- Dimenco 2D+Z + header canvas and continuous video on the test page.
- REAL-SITE PASS (2026-10-03, AOT build, VDA video / DAv3 images, Chrome 151, RTX 4070; screenshots _tools/_shots/rs1_*, rs2_*, rs3_*):
  | site | result |
  | YouTube (Big Buck Bunny) | 3D at the video's rate, depth ~22 ms at 336x196 (level 4/7); canvas exactly on the video; seek and 480p->1080p switch handled |
  | Twitch front page + channel | 3D works; the per-site toggle persists across loads; the front page plays 3 videos at once (all rendered, ~15 FPS each) |
  | Google Images | 54/54 then 163 after scrolling (lazy loads picked up, ~10 images/s); first 3D image ~9 s after load (cold DAv3 load per page) |
  | Yahoo Images | 60/60 |
  | Tubi live | 3D works, depth ~17-19 ms |
  | Pluto TV | DRM stream (MediaKeys set): the browser refuses its frames - a platform limit, nothing an extension can do |
  | Bing Images | not testable here: bing.com is blocked in this machine's hosts file |
  Fixed from the pass (TrackedMediaElement, page.css):
  - A failing video (Pluto) retried EVERY frame and logged every failure (1,720 lines in 8 s): now one log per distinct
    error (with a DRM hint) and, after 3 failures in a row, one retry per second (still recovers on its own).
  - Canvas left at a STALE size when an image changed while queued (Google cold start: zoomed top-left crops). Repro:
    _tools/testpage/swap.html (`window.__swapCheck()`): 350x140 canvas for a 1400x560 image before, exact after.
  - The state BORDER resized fixed-width images by 2 px (host-page layout shift + a visible green sliver): now an inset
    OUTLINE (no layout effect). Canvas geometry fractional + invariant culture: edges within 0.016 px (54 + 21 checked).
  - Stats text scaled to the displayed size (was ~8 px on Twitch's 530 px-wide player).
  OPEN from the pass: several videos playing at once (Twitch front page) alternate VDA's single temporal state (each
  owner switch resets it); the stats box can overflow a narrow video; first image per page waits ~9 s for the model.
- Measure DAv3 vs DAv2 (cold start to first 3D image, video FPS); compare with `D:\users\tj\Projects\vjs\anglyphoholv3`.

OPEN QUESTIONS FOR TJ
- Bundle both models (~204 MB) or DAv3 only (~105 MB)? Decide with the measurements above.
- `all_frames: true` boots the .NET app in every iframe (ads included), as the BlazorJS build did. Keep?
- WebWorkers: should `main.*.js` be produced for Blazor-runtime apps too (Trip's 2.1.19 turns it off for the Blazor SDK)?
- Manifest version 4.0.0 (store has the JS build at 3.0.x) - confirm at release.
