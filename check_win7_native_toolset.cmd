@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found.
  exit /b 10
)

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "VSROOT=%%I"
if not defined VSROOT (
  echo ERROR: Visual Studio with MSBuild not found.
  exit /b 11
)

set "VERFILE=%VSROOT%\VC\Auxiliary\Build\Microsoft.VCToolsVersion.v143.default.txt"
if not exist "%VERFILE%" (
  echo ERROR: MSVC v143 toolset is not installed in:
  echo   %VSROOT%
  echo.
  echo Required for Windows 7 SP1 native OPC DA build.
  echo Install these Visual Studio components:
  echo   Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64
  echo   Microsoft.VisualStudio.Component.VC.14.44.17.14.ATL
  exit /b 12
)

set /p V143VER=<"%VERFILE%"
if not defined V143VER (
  echo ERROR: cannot read v143 toolset version from %VERFILE%
  exit /b 13
)

set "ATL=%VSROOT%\VC\Tools\MSVC\%V143VER%\atlmfc\include\atlbase.h"
if not exist "%ATL%" (
  echo ERROR: ATL for v143 is not installed.
  echo Expected:
  echo   %ATL%
  echo Install component:
  echo   Microsoft.VisualStudio.Component.VC.14.44.17.14.ATL
  exit /b 14
)

echo ===== WIN7 NATIVE TOOLSET OK =====
echo VS:       %VSROOT%
echo v143:     %V143VER%
echo ATL:      %ATL%
exit /b 0
