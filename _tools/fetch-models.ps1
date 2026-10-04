# Prepares the depth models Anaglyphohol bundles into Anaglyphohol\wwwroot\models (served to the extension as
# app/models/...): DAv3 Small for images, Video Depth Anything Small for video. Both are Apache-2.0; their notices ship
# in wwwroot\licenses. The build runs this when a model is missing (Anaglyphohol.csproj).
#
# The bundled files store their weights as FP16 (TJ 2026-10-04: "go with fp16 bundled for both"): each FP32 weight W
# becomes W__fp16 + a Cast(to=FLOAT) (SpawnDev.ILGPU.ML tools/onnx-weights-fp16.py), so the compute stays FP32 - the
# engine folds that Cast at load and upcasts each weight once on the GPU (ILGPU.ML 5.3.2-local.13+). Half the bytes:
# MEASURED ~226 MB -> ~129 MB deflated for the whole package, under addons.mozilla.org's 200 MB limit; depth moves by
# relRMS 6.9e-5 (DAv3) / ~8e-4 (VDA). The FP32 originals stay in _tools\.cache and never ship.
#
# Source: hub.spawndev.com (caches HuggingFace, answers with CORS, keeps us out of HF's rate limiter). Nothing in
# Anaglyphohol requests huggingface.co directly - not at build time, not at runtime.
#
# Needs Python with onnx + numpy (the FP16 conversion; VDA_PYTHON, default `py -3.13`) and SpawnDev.ILGPU.ML next to
# this repo (ILGPU_ML_REPO). A fresh VDA export also needs torch, onnxruntime, einops (see below).
#
# Usage:  powershell -ExecutionPolicy Bypass -File _tools\fetch-models.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$hub = 'https://hub.spawndev.com:44365/hf'
$projects = Join-Path $PSScriptRoot '..\..\..'
$cache = Join-Path $PSScriptRoot '.cache'
$modelsRoot = Join-Path $PSScriptRoot '..\Anaglyphohol\wwwroot\models'
$mlRepo = if ($env:ILGPU_ML_REPO) { $env:ILGPU_ML_REPO } else { Join-Path $projects 'SpawnDev.ILGPU.ML\SpawnDev.ILGPU.ML' }
$converter = Join-Path $mlRepo 'tools\onnx-weights-fp16.py'
if (-not (Test-Path $converter)) { throw "FP16 weight converter not found at $converter (set ILGPU_ML_REPO)" }
# ⚠️ assign the argument ARRAY directly: `$a = if (...) { $x[1..1] }` unrolls to a bare string, and splatting a
# string passes its CHARACTERS ("-3.13" arrived as "- 3 . 1 3" and the launcher picked the wrong Python).
if ($env:VDA_PYTHON) { $pyExe = $env:VDA_PYTHON; $pyArgs = @() } else { $pyExe = 'py'; $pyArgs = @('-3.13') }
New-Item -ItemType Directory -Force -Path $cache | Out-Null

# FP32 source -> bundled FP16-weight file. A marker in the CACHE (never shipped) records which source a bundled file
# was made from, so an unchanged source is not converted again.
function Convert-ToFp16Weights([string]$src, [string]$dest, [string]$label) {
    $marker = Join-Path $cache (($label -replace '[\\/]', '_') + '.converted')
    $want = "fp16-weights from $([IO.Path]::GetFileName($src)) $((Get-Item $src).Length) bytes"
    if (-not $Force -and (Test-Path $dest) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $want)) {
        Write-Host "ok        $label ($((Get-Item $dest).Length) bytes, FP16 weights)"
        return
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    $tmp = "$dest.tmp.onnx"
    Write-Host "convert   $label -> FP16 weights"
    & $pyExe @pyArgs $converter $src $tmp
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tmp)) { throw "FP16 weight conversion failed for $label (exit $LASTEXITCODE)" }
    Move-Item -Force $tmp $dest
    Set-Content -Path $marker -Value $want -Encoding ASCII
    Write-Host "saved     $label ($((Get-Item $dest).Length) bytes)"
}

# ── Depth Anything 3 Small (images): the hub's FP32 files into the cache, then one FP16-weight file to bundle ──
$dav3Dir = Join-Path $cache 'depth-anything-v3-small\onnx'
New-Item -ItemType Directory -Force -Path $dav3Dir | Out-Null
foreach ($name in @('model.onnx', 'model.onnx_data')) {
    $url = "$hub/onnx-community/depth-anything-v3-small/onnx/$name"
    $dest = Join-Path $dav3Dir $name
    try {
        $head = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
        $expected = [int64]($head.Headers['Content-Length'] | Select-Object -First 1)
    } catch {
        if (Test-Path $dest) { Write-Host "offline   $name (hub unreachable; using the cached copy)"; continue }
        throw
    }
    if (-not $Force -and (Test-Path $dest) -and ((Get-Item $dest).Length -eq $expected)) {
        Write-Host "ok        depth-anything-v3-small/onnx/$name ($expected bytes, cached)"
        continue
    }
    Write-Host "download  $url"
    Invoke-WebRequest -Uri $url -OutFile "$dest.part" -UseBasicParsing
    $got = (Get-Item "$dest.part").Length
    if ($got -ne $expected) { throw "size mismatch for $url : got $got, expected $expected" }
    Move-Item -Force "$dest.part" $dest
    Write-Host "saved     depth-anything-v3-small/onnx/$name ($got bytes)"
}
Convert-ToFp16Weights (Join-Path $dav3Dir 'model.onnx') (Join-Path $modelsRoot 'depth-anything-v3-small\model.onnx') 'depth-anything-v3-small/model.onnx'
# the old FP32 layout (onnx\model.onnx + onnx\model.onnx_data) must not ship alongside
$oldDav3 = Join-Path $modelsRoot 'depth-anything-v3-small\onnx'
if (Test-Path $oldDav3) { Remove-Item -Recurse -Force $oldDav3; Write-Host "removed   depth-anything-v3-small/onnx (the FP32 layout)" }

# ── Video Depth Anything Small (video) ──
# OUR ONNX export of its streaming step (SpawnDev.ILGPU.ML tools/vda-export --kv: the K/V-cache stream, exact and
# cheaper per frame), so there is no ready-made file to download. FP32 source, the first that exists wins:
#   1. VDA_ONNX, or the local export SpawnDev.ILGPU.ML\_research\vda-export\vda_small_stream_kv.onnx
#   2. a previous fresh export in _tools\.cache
#   3. a fresh export: the Small weights through the hub (VDA_WEIGHTS to use a local copy), the Video-Depth-Anything
#      code at $vdaCommit (VDA_REPO, else cloned into _tools\.cache), and SpawnDev.ILGPU.ML's export script.
#      Needs Python with torch, onnx, onnxruntime, numpy, einops.
# ⚠️ ONLY Video-Depth-Anything-SMALL is Apache-2.0. Base/Large are CC-BY-NC-4.0: never bundle them.
$vdaCommit = '4f5ae23172ba60fd7bc11ef671cca678842c7072'
$vdaSrc = if ($env:VDA_ONNX) { $env:VDA_ONNX } else { Join-Path $projects 'SpawnDev.ILGPU.ML\_research\vda-export\vda_small_stream_kv.onnx' }
$vdaCached = Join-Path $cache 'vda_small_stream_kv.onnx'
if (-not (Test-Path $vdaSrc)) { $vdaSrc = $vdaCached }
if (-not (Test-Path $vdaSrc)) {
    # weights (Apache-2.0, 116 MB) through the hub, never huggingface.co
    $weights = if ($env:VDA_WEIGHTS) { $env:VDA_WEIGHTS } else { Join-Path $cache 'video_depth_anything_vits.pth' }
    if (-not (Test-Path $weights)) {
        $url = "$hub/depth-anything/Video-Depth-Anything-Small/video_depth_anything_vits.pth"
        Write-Host "download  $url"
        Invoke-WebRequest -Uri $url -OutFile "$weights.part" -UseBasicParsing
        Move-Item -Force "$weights.part" $weights
    }
    # the model code, pinned: the export patches its forward passes (tools/vda-export/vda_dynamic_patches.py)
    $vdaRepo = if ($env:VDA_REPO) { $env:VDA_REPO } else { Join-Path $cache 'Video-Depth-Anything' }
    if (-not (Test-Path $vdaRepo)) {
        git clone --quiet https://github.com/DepthAnything/Video-Depth-Anything.git $vdaRepo
        if ($LASTEXITCODE -ne 0) { throw "git clone of Video-Depth-Anything failed" }
    }
    git -C $vdaRepo checkout --quiet $vdaCommit
    if ($LASTEXITCODE -ne 0) { throw "Video-Depth-Anything: cannot check out $vdaCommit in $vdaRepo" }
    $exportScript = Join-Path $mlRepo 'tools\vda-export\export_vda_stream.py'
    if (-not (Test-Path $exportScript)) { throw "VDA export script not found at $exportScript (set ILGPU_ML_REPO)" }
    $tmp = "$vdaCached.tmp.onnx"
    Write-Host "export    vda_small_stream_kv.onnx (K/V stream, onnxruntime parity check)"
    $env:VDA_REPO = $vdaRepo
    & $pyExe @pyArgs $exportScript $weights $tmp --kv --check-frames 6
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tmp)) { throw "VDA export failed (exit $LASTEXITCODE)" }
    Move-Item -Force $tmp $vdaCached
    $vdaSrc = $vdaCached
}
Convert-ToFp16Weights $vdaSrc (Join-Path $modelsRoot 'video-depth-anything-small\model.onnx') 'video-depth-anything-small/model.onnx'
