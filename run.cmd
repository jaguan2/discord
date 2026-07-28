@echo off
REM Double-clickable wrapper so you don't have to deal with execution policy.
setlocal

set "GAME=%~1"
if "%GAME%"=="" set /p "GAME=Game name: "
if "%GAME%"=="" exit /b 1

set "MINUTES=%~2"
if "%MINUTES%"=="" set "MINUTES=15"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0QuestLauncher.ps1" "%GAME%" -Minutes %MINUTES%

echo.
pause
