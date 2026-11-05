@echo off
echo [INFO] Stopping Easy Red 2 Headless Dedicated Server (leaving Vanilla Client untouched)...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter \"Name = 'Easy Red 2.exe'\" | Where-Object { $_.ExecutablePath -like '*server_instance*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"
echo [INFO] Headless server stopped.
