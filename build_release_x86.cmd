@echo off
setlocal EnableExtensions
cd /d "%~dp0"

call check_win7_native_toolset.cmd
if errorlevel 1 exit /b %ERRORLEVEL%

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%I"
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "VSROOT=%%I"
if not defined MSBUILD (
  echo ERROR: MSBuild not found.
  exit /b 11
)

echo ===== RESTORE + BUILD Release x86 / Win7 SP1 native target =====
echo MSBuild: %MSBUILD%
echo.

"%MSBUILD%" "ASTUE_MPS_Replacement.sln" /restore /m /t:Rebuild /p:Configuration=Release /p:Platform=x86 /v:minimal
if errorlevel 1 (
  echo.
  echo BUILD FAILED, errorlevel=%ERRORLEVEL%
  exit /b %ERRORLEVEL%
)

set "OUT=%CD%\bin\x86\Release\net40"
set "APP=%OUT%\AstueMpsReplacement.exe"
set "APPCFG=%OUT%\AstueMpsReplacement.exe.config"
set "OPC=%OUT%\AstueMpsOpcDaServer.exe"
if not exist "%APP%" (
  echo ERROR: managed application not found: %APP%
  exit /b 29
)
if not exist "%APPCFG%" (
  echo ERROR: managed application config not found: %APPCFG%
  exit /b 28
)

echo.
echo ===== MANAGED WIN7 / .NET 4.0 AUDIT =====
findstr /I /C:".NETFramework,Version=v4.0" "%APPCFG%" >nul
if errorlevel 1 (
  echo ERROR: application config is not targeting .NET Framework 4.0.
  type "%APPCFG%"
  exit /b 27
)
echo PASS: AstueMpsReplacement targets .NET Framework 4.0 Full.

if not exist "%OPC%" (
  echo ERROR: native OPC server not found: %OPC%
  exit /b 30
)

echo.
echo ===== WIN7 IMPORT AUDIT =====
call "%VSROOT%\VC\Auxiliary\Build\vcvars32.bat" >nul
if errorlevel 1 (
  echo ERROR: vcvars32.bat failed.
  exit /b 31
)

set "IMPORTS=%OUT%\AstueMpsOpcDaServer.imports.txt"
dumpbin /imports "%OPC%" > "%IMPORTS%"
if errorlevel 1 (
  echo ERROR: dumpbin /imports failed.
  exit /b 32
)
findstr /I /C:"GetSystemTimePreciseAsFileTime" "%IMPORTS%" >nul
if not errorlevel 1 (
  echo ERROR: Win7-incompatible static import detected: GetSystemTimePreciseAsFileTime
  echo See: %IMPORTS%
  exit /b 33
)

echo PASS: GetSystemTimePreciseAsFileTime is NOT statically imported.
dumpbin /headers "%OPC%" | findstr /I /C:"subsystem version"

echo.
echo ===== BUILD OK =====
echo Output folder:
echo %OUT%
echo.
dir /b "%OUT%" 2>nul
exit /b 0
