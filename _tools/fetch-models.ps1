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
