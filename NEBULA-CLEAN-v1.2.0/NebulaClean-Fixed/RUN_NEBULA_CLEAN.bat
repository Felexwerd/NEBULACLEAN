@echo off
setlocal
title NEBULA CLEAN - Build and Launch
cd /d "%~dp0"
echo.
echo  ==========================================
echo          NEBULA CLEAN - LAUNCHER
echo  ==========================================
echo.
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] .NET SDK not found.
  echo Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  echo Then run this file again.
  echo.
  pause
  exit /b 1
)
echo [1/2] Building NEBULA CLEAN...
dotnet build "NebulaClean.csproj" -c Release
if errorlevel 1 (
  echo.
  echo [ERROR] Build failed. Read the errors above.
  echo.
  pause
  exit /b 1
)
set "APP=%~dp0bin\Release\net10.0-windows\NebulaClean.exe"
if not exist "%APP%" (
  echo [ERROR] Executable not found: "%APP%"
  echo.
  pause
  exit /b 1
)
echo.
echo [2/2] Starting NEBULA CLEAN...
start "" "%APP%"
exit /b 0
