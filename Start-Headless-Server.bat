@echo off
setlocal
cd /d "%~dp0"

if not exist "server_instance\Easy Red 2.exe" (
    echo [INFO] server_instance not found. Running build.ps1 -SetupServerInstance...
    powershell -ExecutionPolicy Bypass -File "build.ps1" -SetupServerInstance
    if errorlevel 1 (
        echo [ERROR] Failed to set up server_instance.
        pause
        exit /b 1
    )
)

echo [INFO] Launching Easy Red 2 Headless Dedicated Server...
start "" "%~dp0server_instance\Easy Red 2.exe" -screen-fullscreen 0 -screen-width 540 -screen-height 300
echo [INFO] Server process launched! Check server_instance\BepInEx\LogOutput.log for live status.
