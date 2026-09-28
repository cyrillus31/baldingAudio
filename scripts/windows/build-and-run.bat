@echo off
setlocal

rem Usage:
rem   build-and-run.bat [demo-flood|demo|selftest]

set "MODE=%~1"
if "%MODE%"=="" set "MODE=demo-flood"

set "PROJECT_ROOT=X:\Projects"
set "PROJECT_NAME=baldingAudio"
set "REPO=%PROJECT_ROOT%\%PROJECT_NAME%"

if not exist "%REPO%\src\BaldingAudio.App\BaldingAudio.App.csproj" (
  echo ERROR: Repo not found at %REPO%
  echo Expected: X:\Projects\baldingAudio
  exit /b 1
)

where dotnet >nul 2>nul
if errorlevel 1 (
  echo ERROR: dotnet not found on PATH.
  echo Install the .NET SDK on Windows (dotnet --list-sdks should work).
  exit /b 1
)

set "CSProj=%REPO%\src\BaldingAudio.App\BaldingAudio.App.csproj"
set "OUT=%REPO%\artifacts\win-x64"

echo Publishing Windows x64 into %OUT% ...
dotnet publish "%CSProj%" -c Release -r win-x64 --self-contained true -o "%OUT%"

set "EXE=%OUT%\baldingAudio.exe"
if not exist "%EXE%" (
  echo ERROR: Build finished but exe not found: %EXE%
  exit /b 1
)

set "ARG="
if "%MODE%"=="demo" set "ARG=--demo"
if "%MODE%"=="demo-flood" set "ARG=--demo-flood"
if "%MODE%"=="selftest" set "ARG=--selftest"

echo Running: %EXE% %ARG%
"%EXE%" %ARG%

endlocal
