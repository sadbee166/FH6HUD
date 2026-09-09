@echo off
set "ROOT=F:\workspace_git\FH6HUD"
set "EXE=%ROOT%\src\ForzaHud\bin\Debug\net10.0-windows\win-x64\ForzaHud.exe"

cd /d "%ROOT%" || exit /b 1

"%EXE%" --record --config "%ROOT%\config\hud.json"

echo.
echo Recorded sessions:
dir "%ROOT%\sessions\*.fzh"
pause