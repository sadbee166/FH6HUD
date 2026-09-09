@echo off
set "ROOT=F:\workspace_git\FH6HUD"

cd /d "%ROOT%" || exit /b 1

dotnet build "%ROOT%\src\ForzaHud\ForzaHud.csproj" --nologo
if errorlevel 1 exit /b 1

"%ROOT%\src\ForzaHud\bin\Debug\net10.0-windows\win-x64\ForzaHud.exe" --config "%ROOT%\config\hud.json"
