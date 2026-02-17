@echo off
chcp 65001 >nul
echo.
echo ==========================================
echo   SFDRN Mesh Network - Starting 3 Nodes
echo ==========================================
echo.

REM Переходим в папку сервера
cd /d "%~dp0"

REM Убиваем старые процессы dotnet (если есть)
echo [1/4] Stopping old SFDRN processes...
taskkill /F /IM dotnet.exe >nul 2>&1
timeout /t 2 >nul

REM Node 1
echo [2/4] Starting Node 1 (local-01) on port 5000...
REM Добавлено --no-launch-profile, чтобы игнорировать порты из launchSettings.json и взять наши переменные
start "SFDRN-Node1" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Node1.json && set ASPNETCORE_URLS=http://localhost:5000 && dotnet run --no-launch-profile > node1.log 2>&1"
timeout /t 3 >nul

REM Node 2
echo [3/4] Starting Node 2 (local-02) on port 5001...
start "SFDRN-Node2" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Node2.json && set ASPNETCORE_URLS=http://localhost:5001 && dotnet run --no-launch-profile > node2.log 2>&1"
timeout /t 3 >nul

REM Node 3
echo [4/4] Starting Node 3 (local-03) on port 5002...
start "SFDRN-Node3" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Node3.json && set ASPNETCORE_URLS=http://localhost:5002 && dotnet run --no-launch-profile > node3.log 2>&1"
timeout /t 2 >nul

echo.
echo ==========================================
echo   All nodes started successfully!
echo ==========================================
echo.
echo Node 1 (local-01): http://localhost:5000
echo Node 2 (local-02): http://localhost:5001
echo Node 3 (local-03): http://localhost:5002
echo.
pause