@echo off
set "ROOT=F:\workspace_git\FH6HUD"
set "EXE=%ROOT%\src\ForzaHud\bin\Debug\net10.0-windows\win-x64\ForzaHud.exe"

cd /d "%ROOT%" || exit /b 1

if "%~1"=="" (
    echo Usage: replay-hud.bat sessions\filename.fzh
    dir /b "%ROOT%\sessions\*.fzh"
    pause
    exit /b 1
)

"%EXE%" --replay .\sessions\20260829-145508.fzh --speed 1.0 --config "%ROOT%\config\hud.json"