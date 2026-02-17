@echo off
chcp 65001 >nul
echo.
echo Stopping all SFDRN nodes...
taskkill /F /IM dotnet.exe >nul 2>&1
echo Done!
echo.
pause