@echo off
setlocal EnableExtensions
cd /d "%~dp0"

call build_release_x86.cmd
if errorlevel 1 exit /b %ERRORLEVEL%

set "OUT=%CD%\bin\x86\Release\net40"
set "DIST=%CD%\dist"
set "PKG=%DIST%\astue_mps_replacement_v0_3_2_4_win7_win11_x86"
set "ZIP=%DIST%\astue_mps_replacement_v0_3_2_4_win7_win11_x86.zip"

if not exist "%OUT%\AstueMpsReplacement.exe" (
  echo ERROR: AstueMpsReplacement.exe not found in %OUT%
  exit /b 20
)
if not exist "%OUT%\AstueMpsOpcDaServer.exe" (
  echo ERROR: AstueMpsOpcDaServer.exe not found in %OUT%
  echo Check C++/ATL/Windows SDK/OPC Classic headers installation.
  exit /b 22
)

if exist "%PKG%" rmdir /s /q "%PKG%"
if not exist "%DIST%" mkdir "%DIST%"
mkdir "%PKG%"

xcopy "%OUT%\*" "%PKG%\" /E /I /Y >nul
copy /Y "docs\TEST_PLAN_0_3_2_4.md" "%PKG%\TEST_PLAN_0_3_2_4.md" >nul
copy /Y "docs\CHANGELOG_0_3_2_4.md" "%PKG%\CHANGELOG_0_3_2_4.md" >nul
copy /Y "docs\OPC_DA_0_3_2_4.md" "%PKG%\OPC_DA_0_3_2_4.md" >nul
copy /Y "docs\TEMPLATE_AUDIT_0_3_2_0.md" "%PKG%\TEMPLATE_AUDIT_0_3_2_0.md" >nul
copy /Y "start_config_mode.cmd" "%PKG%\start_config_mode.cmd" >nul
copy /Y "start_work_mode.cmd" "%PKG%\start_work_mode.cmd" >nul

if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%PKG%\*' -DestinationPath '%ZIP%' -Force"
if errorlevel 1 (
  echo ERROR: packaging failed.
  exit /b 21
)

echo.
echo ===== PACKAGE OK =====
echo %ZIP%
dir "%ZIP%"
exit /b 0
