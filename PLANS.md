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
- OPEN: shape thrash is REAL. The test page needs 4 shapes (video + 3 image aspects) and ILGPU.ML keeps 3 executors:
  img-lazy (672x266, same shape as img-wide) recompiled again (120 ms) after eviction. Options (ILGPU.ML): configurable
  MaxShapeExecutors, coarser NativeAspect aspect buckets. Measure GPU memory per executor first.
- Dimenco 2D+Z + header canvas and continuous video on the test page.
- Real sites: YouTube, Google Images (screenshots with run-tagged names for TJ's by-eye verdict).
- Measure DAv3 vs DAv2 (cold start to first 3D image, video FPS); compare with `D:\users\tj\Projects\vjs\anglyphoholv3`.

OPEN QUESTIONS FOR TJ
- Bundle both models (~204 MB) or DAv3 only (~105 MB)? Decide with the measurements above.
- `all_frames: true` boots the .NET app in every iframe (ads included), as the BlazorJS build did. Keep?
- WebWorkers: should `main.*.js` be produced for Blazor-runtime apps too (Trip's 2.1.19 turns it off for the Blazor SDK)?
- Manifest version 4.0.0 (store has the JS build at 3.0.x) - confirm at release.
