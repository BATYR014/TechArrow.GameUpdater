@echo off
cd /d "%~dp0"
dotnet run --project src\TechArrow.GameUpdater\TechArrow.GameUpdater.csproj
if errorlevel 1 pause
