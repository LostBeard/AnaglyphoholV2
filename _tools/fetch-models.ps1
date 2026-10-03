# Downloads the depth models Anaglyphohol bundles into Anaglyphohol\wwwroot\models (served to the extension as
# app/models/...). They are too big for git: DAv3 model.onnx_data is 104.7 MB, over GitHub's 100 MB file limit.
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

# Video Depth Anything Small - the VIDEO model (TJ 2026-10-03). Our ONNX export of its streaming step
# (SpawnDev.ILGPU.ML tools/vda-export) is NOT hosted anywhere yet, so it is copied from the local export.
# Without it the extension still works: video falls back to DAv3 (DepthService.GetPipelineForAsync).
# TODO(TJ): host it (e.g. on the hub) and replace this copy with a download like the entries above.
$vdaSrc = if ($env:VDA_ONNX) { $env:VDA_ONNX } else { Join-Path $PSScriptRoot '..\..\..\SpawnDev.ILGPU.ML\_research\vda-export\vda_small_stream.onnx' }
$vdaDest = Join-Path $modelsRoot 'video-depth-anything-small\model.onnx'
if (Test-Path $vdaSrc) {
    New-Item -ItemType Directory -Force -Path (Split-Path $vdaDest) | Out-Null
    if ($Force -or -not (Test-Path $vdaDest) -or (Get-Item $vdaDest).Length -ne (Get-Item $vdaSrc).Length) {
        Copy-Item -Force $vdaSrc $vdaDest
        Write-Host "copied    video-depth-anything-small/model.onnx ($((Get-Item $vdaDest).Length) bytes) from $vdaSrc"
    } else { Write-Host "ok        video-depth-anything-small/model.onnx" }
} else {
    Write-Warning "Video Depth Anything model not found at $vdaSrc (set VDA_ONNX). Video will fall back to DAv3."
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
