# Stops what launch-chrome.ps1 started - by the PIDs it recorded in _tools\.pids, with their child processes.
# Never by image name: TJ's own Chrome and other agents' test browsers must stay up.
#
# Chrome is closed GRACEFULLY first (CDP Browser.close), then force-stopped only if it is still alive, and the result
# is VERIFIED. 2026-09-30: the old `taskkill /T /F` reported "stopped" while the browser kept running, and the
# force-kill that followed left the profile's service-worker registrations broken - the extension's background
# worker then failed with "Service worker registration failed. Status code: 2" until a graceful restart.
$ErrorActionPreference = 'Continue'
$pidFile = Join-Path $PSScriptRoot '.pids'
$cdpPort = if ($env:CDP_PORT) { $env:CDP_PORT } else { 9224 }
if (-not (Test-Path $pidFile)) { Write-Host 'nothing to stop (no .pids file)'; return }

function Wait-Exit([int]$id, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Process -Id $id -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    return -not (Get-Process -Id $id -ErrorAction SilentlyContinue)
}

$failed = $false
foreach ($line in Get-Content $pidFile) {
    $name, $id = $line -split '=', 2
    if (-not $id) { continue }
    $id = [int]$id
    if (-not (Get-Process -Id $id -ErrorAction SilentlyContinue)) { Write-Host "$name (PID $id) already gone"; continue }
    if ($name -eq 'chrome') {
        try {
            $ver = Invoke-RestMethod "http://localhost:$cdpPort/json/version" -TimeoutSec 3
            $ws = [System.Net.WebSockets.ClientWebSocket]::new()
            $ws.ConnectAsync([Uri]$ver.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait(3000) | Out-Null
            $msg = [Text.Encoding]::UTF8.GetBytes('{"id":1,"method":"Browser.close"}')
            $ws.SendAsync([ArraySegment[byte]]::new($msg), 'Text', $true, [Threading.CancellationToken]::None).Wait(3000) | Out-Null
        } catch { Write-Host "  (graceful close over CDP failed: $($_.Exception.Message))" }
        if (Wait-Exit $id 15) { Write-Host "stopped chrome (PID $id) gracefully"; continue }
        Write-Host "  chrome (PID $id) still alive after Browser.close - forcing"
    }
    taskkill /PID $id /T /F 2>&1 | Out-Null
    if (-not (Wait-Exit $id 5)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    if (Wait-Exit $id 5) { Write-Host "stopped $name (PID $id)" }
    else { Write-Host "FAILED to stop $name (PID $id)"; $failed = $true }
}
if ($failed) { Write-Host '.pids kept - something is still running'; exit 1 }
Remove-Item $pidFile
