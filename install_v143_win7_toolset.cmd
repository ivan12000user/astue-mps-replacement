@echo off
setlocal EnableExtensions

net session >nul 2>&1
if not "%ERRORLEVEL%"=="0" (
  echo ERROR: run this CMD as Administrator.
  exit /b 5
)

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
set "SETUP=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\setup.exe"
if not exist "%VSWHERE%" (
  echo ERROR: vswhere.exe not found.
  exit /b 10
)
if not exist "%SETUP%" (
  echo ERROR: Visual Studio setup.exe not found.
  exit /b 11
)

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -property installationPath`) do set "VSROOT=%%I"
if not defined VSROOT (
  echo ERROR: Visual Studio installation not found.
  exit /b 12
)

echo Visual Studio: %VSROOT%
echo Installing Win7-capable v143 14.44 x86/x64 + ATL...
echo.

"%SETUP%" modify --installPath "%VSROOT%" ^
  --add Microsoft.VisualStudio.Component.VC.14.44.17.14.x86.x64 ^
  --add Microsoft.VisualStudio.Component.VC.14.44.17.14.ATL ^
  --passive --norestart

set "RC=%ERRORLEVEL%"
echo.
echo Installer exit code: %RC%
echo If 3010 is returned, reboot Z270 before building.
exit /b %RC%
