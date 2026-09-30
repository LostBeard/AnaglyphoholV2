@echo off

REM Publishes the extension. SpawnDev.SpawnJS.BrowserExtension writes one unpacked extension per
REM wwwroot\manifest.{platform}.json partial: bin\PublishDebug\chrome and firefox (+ .zip each).
REM Pass "nopause" as the first argument when running from a script.

set configuration=Debug
set outputPath=%~dp0bin\Publish%configuration%

echo "Creating %configuration% publish build"
rmdir /Q /S "%outputPath%"
dotnet publish "%~dp0Anaglyphohol.csproj" --nologo --configuration %configuration% --output "%outputPath%"
if errorlevel 1 (
    echo "Build FAILED."
    if /I not "%~1"=="nopause" pause
    exit /b 1
)

echo "Build complete."
if /I not "%~1"=="nopause" pause
