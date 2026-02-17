@echo off
chcp 65001 >nul
echo.
echo ==========================================
echo   SFDRN Mesh Network - Starting 5 Nodes
echo ==========================================
echo.
echo Topology:
echo   Denmark(5000) --- Germany(5001) --- Poland(5002)
echo                          ^|                  ^|
echo                      Austria(5003) --- Czech(5004)
echo.

cd /d "%~dp0"

echo [1/6] Stopping old SFDRN processes...
taskkill /F /IM dotnet.exe >nul 2>&1
timeout /t 2 >nul

echo [2/6] Starting Denmark on port 5000...
start "SFDRN-Denmark" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Denmark.json && set ASPNETCORE_URLS=http://localhost:5000 && dotnet run --no-launch-profile > denmark.log 2>&1"
timeout /t 2 >nul

echo [3/6] Starting Germany on port 5001...
start "SFDRN-Germany" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Germany.json && set ASPNETCORE_URLS=http://localhost:5001 && dotnet run --no-launch-profile > germany.log 2>&1"
timeout /t 2 >nul

echo [4/6] Starting Poland on port 5002...
start "SFDRN-Poland" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Poland.json && set ASPNETCORE_URLS=http://localhost:5002 && dotnet run --no-launch-profile > poland.log 2>&1"
timeout /t 2 >nul

echo [5/6] Starting Austria on port 5003...
start "SFDRN-Austria" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Austria.json && set ASPNETCORE_URLS=http://localhost:5003 && dotnet run --no-launch-profile > austria.log 2>&1"
timeout /t 2 >nul

echo [6/6] Starting Czech Republic on port 5004...
start "SFDRN-Czech" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Czech.json && set ASPNETCORE_URLS=http://localhost:5004 && dotnet run --no-launch-profile > czech.log 2>&1"
timeout /t 2 >nul

echo.
echo ==========================================
echo   All nodes started!
echo ==========================================
echo.
echo   Denmark:  http://localhost:5000
echo   Germany:  http://localhost:5001
echo   Poland:   http://localhost:5002
echo   Austria:  http://localhost:5003
echo   Czech:    http://localhost:5004
echo.
pause