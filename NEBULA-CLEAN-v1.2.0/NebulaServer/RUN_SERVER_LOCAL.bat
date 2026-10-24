@echo off
setlocal
cd /d "%~dp0"
if "%NEBULA_ADMIN_EMAIL%"=="" set NEBULA_ADMIN_EMAIL=admin@example.com
if "%NEBULA_ADMIN_PASSWORD%"=="" (echo Set NEBULA_ADMIN_PASSWORD before launching. & pause & exit /b 1)
dotnet run --urls http://localhost:5080
pause
