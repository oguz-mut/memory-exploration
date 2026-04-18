@echo off
title Match-3 Auto Solver
cd /d "%~dp0"
echo Starting Match-3 Solver...
echo Open a Match-3 game in Project Gorgon and it will auto-play.
echo Press Ctrl+C to stop.
echo.
echo Usage: run-solver.bat [--autoloop] [--maxgames=N] [--strategy=Auto^|MCTS^|Iterative^|Beam^|Eval]
echo.
dotnet run --project . -c Release -- %*
pause
