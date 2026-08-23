@echo off
title FakeWheel VM Deploy
echo.
echo === FakeWheel VM Deploy ===
echo.
echo This window will show every step. Do not close it until it says DONE.
echo.
pause
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy-visible.ps1"
echo.
echo === Script finished. Press any key to close. ===
pause
