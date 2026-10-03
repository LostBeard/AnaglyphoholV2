# Downloads the depth models Anaglyphohol bundles into Anaglyphohol\wwwroot\models (served to the extension as
# app/models/...): DAv3 Small for images, Video Depth Anything Small for video. Both are Apache-2.0; their notices ship
# in wwwroot\licenses. They are too big for git: DAv3 model.onnx_data is 104.7 MB, over GitHub's 100 MB file limit.
#
# Source: hub.spawndev.com (caches HuggingFace, answers with CORS, keeps us out of HF's rate limiter). Nothing in
# Anaglyphohol requests huggingface.co directly - not at build time, not at runtime.
#
# Usage:  powershell -ExecutionPolicy Bypass -File _tools\fetch-models.ps1 [-Force]
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$hub = 'https://hub.spawndev.com:44365/hf'
$modelsRoot = Join-Path $PSScriptRoot '..\Anaglyphohol\wwwroot\models'
$files = @(
    @{ Repo = 'onnx-community/depth-anything-v3-small'; Path = 'onnx/model.onnx';      Dir = 'depth-anything-v3-small' },
    @{ Repo = 'onnx-community/depth-anything-v3-small'; Path = 'onnx/model.onnx_data'; Dir = 'depth-anything-v3-small' }
)

# Video Depth Anything Small - the VIDEO model (TJ 2026-10-03), bundled inside the extension exactly like DAv3.
# The file is OUR ONNX export of its streaming step (SpawnDev.ILGPU.ML tools/vda-export --kv: the K/V-cache stream,
# exact and cheaper per frame), so there is no ready-made file to download. Sources, the first that exists wins:
#   1. VDA_ONNX, or the local export SpawnDev.ILGPU.ML\_research\vda-export\vda_small_stream_kv.onnx
#   2. a fresh export: the Small weights through the hub (VDA_WEIGHTS to use a local copy), the Video-Depth-Anything
#      code at $vdaCommit (VDA_REPO, else cloned into _tools\.cache), and SpawnDev.ILGPU.ML's export script
#      (ILGPU_ML_REPO). Needs Python with torch, onnx, onnxruntime, numpy, einops (VDA_PYTHON, default `py -3.13`).
# ⚠️ ONLY Video-Depth-Anything-SMALL is Apache-2.0. Base/Large are CC-BY-NC-4.0: never bundle them.
$projects = Join-Path $PSScriptRoot '..\..\..'
$cache = Join-Path $PSScriptRoot '.cache'
$vdaCommit = '4f5ae23172ba60fd7bc11ef671cca678842c7072'
$vdaDest = Join-Path $modelsRoot 'video-depth-anything-small\model.onnx'
New-Item -ItemType Directory -Force -Path (Split-Path $vdaDest) | Out-Null
$vdaSrc = if ($env:VDA_ONNX) { $env:VDA_ONNX } else { Join-Path $projects 'SpawnDev.ILGPU.ML\_research\vda-export\vda_small_stream_kv.onnx' }
if (Test-Path $vdaSrc) {
    if ($Force -or -not (Test-Path $vdaDest) -or (Get-Item $vdaDest).Length -ne (Get-Item $vdaSrc).Length) {
        Copy-Item -Force $vdaSrc $vdaDest
        Write-Host "copied    video-depth-anything-small/model.onnx ($((Get-Item $vdaDest).Length) bytes) from $vdaSrc"
    } else { Write-Host "ok        video-depth-anything-small/model.onnx" }
} elseif (-not $Force -and (Test-Path $vdaDest) -and (Get-Item $vdaDest).Length -gt 0) {
    Write-Host "ok        video-depth-anything-small/model.onnx (no export source; keeping the bundled copy)"
} else {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
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
    $mlRepo = if ($env:ILGPU_ML_REPO) { $env:ILGPU_ML_REPO } else { Join-Path $projects 'SpawnDev.ILGPU.ML\SpawnDev.ILGPU.ML' }
    $exportScript = Join-Path $mlRepo 'tools\vda-export\export_vda_stream.py'
    if (-not (Test-Path $exportScript)) { throw "VDA export script not found at $exportScript (set ILGPU_ML_REPO)" }
    # ⚠️ assign the argument ARRAY directly: `$a = if (...) { $x[1..1] }` unrolls to a bare string, and splatting a
    # string passes its CHARACTERS ("-3.13" arrived as "- 3 . 1 3" and the launcher picked the wrong Python).
    if ($env:VDA_PYTHON) { $pyExe = $env:VDA_PYTHON; $pyArgs = @() } else { $pyExe = 'py'; $pyArgs = @('-3.13') }
    $tmp = "$vdaDest.export.onnx"
    Write-Host "export    video-depth-anything-small/model.onnx (K/V stream, onnxruntime parity check)"
    $env:VDA_REPO = $vdaRepo
    & $pyExe @pyArgs $exportScript $weights $tmp --kv --check-frames 6
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $tmp)) { throw "VDA export failed (exit $LASTEXITCODE)" }
    Move-Item -Force $tmp $vdaDest
    Write-Host "saved     video-depth-anything-small/model.onnx ($((Get-Item $vdaDest).Length) bytes)"
}

foreach ($f in $files) {
    $url = "$hub/$($f.Repo)/$($f.Path)"
    $dest = Join-Path $modelsRoot (Join-Path $f.Dir $f.Path)
    New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
    $head = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing
    $expected = [int64]($head.Headers['Content-Length'] | Select-Object -First 1)
    if (-not $Force -and (Test-Path $dest) -and ((Get-Item $dest).Length -eq $expected)) {
        Write-Host "ok        $($f.Dir)/$($f.Path) ($expected bytes)"
        continue
    }
    Write-Host "download  $url"
    $tmp = "$dest.part"
    Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
    $got = (Get-Item $tmp).Length
    if ($got -ne $expected) { throw "size mismatch for $url : got $got, expected $expected" }
    Move-Item -Force $tmp $dest
    Write-Host "saved     $($f.Dir)/$($f.Path) ($got bytes)"
}
