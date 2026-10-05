# Starts the Anaglyphohol debug environment, hidden, and records every PID it starts in _tools\.pids:
#   - a static server for _tools\testpage on http://localhost:8765/
#   - installed Chrome (NOT Playwright's bundled Chromium: that one renders WebGPU in software) on CDP port 9224 with
#     its OWN profile and the PUBLISHED extension (bin\PublishRelease\chrome) loaded
# Stop everything with stop-chrome.ps1 (kills by the recorded PIDs, never by image name - TJ's own Chrome stays up).
#   powershell -ExecutionPolicy Bypass -File _tools\launch-chrome.ps1 [-Url <start url>] [-Build Release|Debug]
param(
    [string]$Url = 'http://localhost:8765/',
    [ValidateSet('Release', 'Debug')][string]$Build = 'Release'
)
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$pidFile = Join-Path $tools '.pids'
if (Test-Path $pidFile) { throw "$pidFile exists - run stop-chrome.ps1 first (a previous session may still be running)." }

$ext = Resolve-Path (Join-Path $tools "..\Anaglyphohol\bin\Publish$Build\chrome")
$profileDir = 'C:\Users\TJ\anaglyphohol-debug-profile'
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$port = 9224

$busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($busy) { throw "port $port is already listening (PID $($busy.OwningProcess)) - not attaching to someone else's browser." }

function Start-Hidden([string]$commandLine, [string]$workDir) {
    # ShowWindow = 0: no console / window left on TJ's desktop
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine; CurrentDirectory = $workDir; ProcessStartupInformation = $startup }
    if ($r.ReturnValue -ne 0) { throw "failed to start: $commandLine (WMI $($r.ReturnValue))" }
    return [int]$r.ProcessId
}

$serverPid = Start-Hidden "python -m http.server 8765 --bind 127.0.0.1 --directory `"$tools\testpage`"" $tools
$chromeArgs = @(
    "--remote-debugging-port=$port",
    "--user-data-dir=`"$profileDir`"",
    "--load-extension=`"$ext`"",
    '--disable-features=DisableLoadExtensionCommandLineSwitch',
    '--no-first-run', '--no-default-browser-check',
    # The window is often covered (or launched hidden): without these Chrome marks the page "hidden", defers media
    # loading (the test video sat at readyState 0) and throttles timers + video-frame callbacks - poison for timing.
    '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding', '--disable-background-timer-throttling',
    $Url
) -join ' '
$chromePid = Start-Hidden "`"$chrome`" $chromeArgs" $tools
Set-Content -Path $pidFile -Value "server=$serverPid`nchrome=$chromePid"
# Chrome 151 IGNORES --load-extension (MEASURED 2026-10-05: a fresh profile had no Anaglyphohol). Install over CDP too;
# on a profile that already has it this just reloads the same unpacked dir.
for ($i = 0; $i -lt 40 -and -not (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
node (Join-Path $tools 'load-unpacked.mjs') $ext
Write-Host "server PID $serverPid (http://localhost:8765/), chrome PID $chromePid (CDP $port), extension $ext"
