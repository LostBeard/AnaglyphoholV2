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

NEXT (needs the GPU - shared with other agents' PMT sweeps; coordinate via _DevComms/board.md)
- WebGPU run of the copier: SpawnDev.ILGPU `BackendTestBase.ExternalImageCopy.cs` (PMT_FILTER=ExternalImageCopy), then
  commit the copier (uncommitted in SpawnDev.ILGPU until then).
- Test page end to end: images reach `anaglyph`, pixel probe shows the channel split for both profiles, Dimenco 2D+Z +
  header canvas, video renders continuously. WGSL of ThreeDKernels is only proven by this run.
- Real sites: YouTube, Google Images (screenshots with run-tagged names for TJ's by-eye verdict).
- Measure DAv3 vs DAv2 (cold start to first 3D image, video FPS); compare with `D:\users\tj\Projects\vjs\anglyphoholv3`.

OPEN QUESTIONS FOR TJ
- Bundle both models (~204 MB) or DAv3 only (~105 MB)? Decide with the measurements above.
- `all_frames: true` boots the .NET app in every iframe (ads included), as the BlazorJS build did. Keep?
- WebWorkers: should `main.*.js` be produced for Blazor-runtime apps too (Trip's 2.1.19 turns it off for the Blazor SDK)?
- Manifest version 4.0.0 (store has the JS build at 3.0.x) - confirm at release.
