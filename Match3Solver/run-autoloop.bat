@echo off
title Match-3 Auto Solver (Looping)
cd /d "%~dp0"
echo Starting Match-3 Solver with AUTO-LOOP...
echo Games will play back-to-back automatically.
echo Press Ctrl+C to stop.
echo.
dotnet run --project . -c Release -- --autoloop
pause
