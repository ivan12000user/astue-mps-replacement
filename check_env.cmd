@echo off
setlocal
echo ===== ASTUE MPS Replacement - build environment =====
echo.

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found.
  exit /b 10
)

echo ===== VISUAL STUDIO =====
"%VSWHERE%" -latest -products * -property installationPath
echo.

echo ===== MSBUILD =====
for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%I"
if not defined MSBUILD (
  echo ERROR: MSBuild not found.
  exit /b 11
)
echo %MSBUILD%
"%MSBUILD%" -version
echo.

echo ===== .NET FRAMEWORK 4.8 TARGETING PACK =====
"%VSWHERE%" -latest -products * -requires Microsoft.Net.Component.4.8.TargetingPack -property installationPath
if errorlevel 1 (
  echo WARNING: vswhere returned an error while checking .NET Framework 4.8 Targeting Pack.
)
echo.

echo ===== GIT =====
git --version
echo.

echo ===== DOTNET =====
dotnet --version
echo.

echo Environment check finished.
exit /b 0
