@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Process 'Easy Red 2' -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '*\server_instance\Easy Red 2.exe' } | ForEach-Object { Write-Host ('Stopping Headless Server PID ' + $_.Id); Stop-Process -Id $_.Id -Force }"
