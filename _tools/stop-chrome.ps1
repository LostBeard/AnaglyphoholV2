# Stops what launch-chrome.ps1 started - by the PIDs it recorded in _tools\.pids, with their child processes.
# Never by image name: TJ's own Chrome and other agents' test browsers must stay up.
$ErrorActionPreference = 'Continue'
$pidFile = Join-Path $PSScriptRoot '.pids'
if (-not (Test-Path $pidFile)) { Write-Host 'nothing to stop (no .pids file)'; return }
foreach ($line in Get-Content $pidFile) {
    $name, $id = $line -split '=', 2
    if (-not $id) { continue }
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p) {
        taskkill /PID $id /T /F | Out-Null
        Write-Host "stopped $name (PID $id)"
    } else {
        Write-Host "$name (PID $id) already gone"
    }
}
Remove-Item $pidFile
