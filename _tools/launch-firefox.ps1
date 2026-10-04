# Starts the Anaglyphohol FIREFOX debug environment, hidden, and records every PID in _tools\.pids (stop-chrome.ps1 stops
# them too):
#   - a static server for _tools\testpage on http://localhost:8765/
#   - installed Firefox with its OWN profile (C:\Users\TJ\anaglyphohol-firefox-profile) and WebDriver BiDi on port 9225
# The extension is NOT loaded by the command line: install it as a temporary add-on over BiDi -
#   dotnet run _tools/bidi.cs install Anaglyphohol/bin/PublishRelease/firefox
# Content-script console output goes to _tools\firefox-console.log (devtools.console.stdout.content).
#   powershell -ExecutionPolicy Bypass -File _tools\launch-firefox.ps1 [-Url <start url>]
param([string]$Url = 'http://localhost:8765/')
$ErrorActionPreference = 'Stop'
$tools = $PSScriptRoot
$pidFile = Join-Path $tools '.pids'
if (Test-Path $pidFile) { throw "$pidFile exists - run stop-chrome.ps1 first (a previous session may still be running)." }
$profileDir = 'C:\Users\TJ\anaglyphohol-firefox-profile'
$firefox = 'C:\Program Files\Mozilla Firefox\firefox.exe'
$port = 9225
$busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if ($busy) { throw "port $port is already listening (PID $($busy.OwningProcess)) - not attaching to someone else's browser." }

New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
# Profile prefs: no first-run / default-browser / crash-restore UI, autoplay allowed (muted test videos must play),
# content-script console to stdout so the log file sees it.
@'
user_pref("browser.shell.checkDefaultBrowser", false);
user_pref("browser.aboutwelcome.enabled", false);
user_pref("datareporting.policy.dataSubmissionEnabled", false);
user_pref("browser.sessionstore.resume_from_crash", false);
user_pref("toolkit.startup.max_resumed_crashes", -1);
user_pref("media.autoplay.default", 0);
user_pref("media.autoplay.blocking_policy", 0);
user_pref("devtools.console.stdout.content", true);
user_pref("dom.webgpu.enabled", true);
'@ | Set-Content -Path (Join-Path $profileDir 'user.js') -Encoding ASCII

function Start-Hidden([string]$commandLine, [string]$workDir) {
    $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]0 }
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine; CurrentDirectory = $workDir; ProcessStartupInformation = $startup }
    if ($r.ReturnValue -ne 0) { throw "failed to start: $commandLine (WMI $($r.ReturnValue))" }
    return [int]$r.ProcessId
}

$serverPid = Start-Hidden "python -m http.server 8765 --bind 127.0.0.1 --directory `"$tools\testpage`"" $tools
$log = Join-Path $tools 'firefox-console.log'
# cmd /c so stdout (the console) lands in the log; the cmd PID is recorded and stopping it with /T stops Firefox too
$ffPid = Start-Hidden "cmd /c `"`"$firefox`" -no-remote -new-instance -profile `"$profileDir`" --remote-debugging-port $port $Url > `"$log`" 2>&1`"" $tools
Set-Content -Path $pidFile -Value "server=$serverPid`nfirefox=$ffPid"
Write-Host "server PID $serverPid (http://localhost:8765/), firefox (cmd) PID $ffPid (BiDi $port), console log $log"
