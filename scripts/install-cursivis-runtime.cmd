@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%install-cursivis-runtime.ps1" %*
if errorlevel 1 (
  echo.
  echo Cursivis setup failed. You can close this window after reading the error above.
  pause
  exit /b 1
)
echo.
echo Cursivis setup complete.
pause
