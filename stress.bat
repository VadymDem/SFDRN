@echo off
chcp 65001 >nul

echo.
echo ==========================================
echo   SFDRN Stress Test - 5 Nodes + Dijkstra
echo ==========================================
echo.
echo Topology:
echo   Denmark(5000) --- Germany(5001) --- Poland(5002)
echo                          ^|                  ^|
echo                      Austria(5003) --- Czech(5004)
echo.

cd /d "%~dp0"

echo [INIT] Waiting 80 seconds for full convergence...
timeout /t 80 >nul

echo.
echo ==========================================
echo   BASELINE
echo ==========================================
echo Denmark:  & curl -s http://localhost:5000/health
echo.
echo Germany:  & curl -s http://localhost:5001/health
echo.
echo Poland:   & curl -s http://localhost:5002/health
echo.
echo Austria:  & curl -s http://localhost:5003/health
echo.
echo Czech:    & curl -s http://localhost:5004/health
echo.
echo Snapshot Denmark:
curl -s http://localhost:5000/network/snapshot
echo.
echo.

echo ==========================================
echo   TEST 1 - Denmark to Czech (full path)
echo   Expected route: denmark-^>germany-^>austria-^>czech
echo   OR:             denmark-^>germany-^>poland-^>czech
echo ==========================================
echo.
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-1\",\"sourceNode\":\"denmark\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"RFRLQ1o=\"}"
echo.
echo Czech packetsStored:
curl -s http://localhost:5004/network/snapshot | findstr packetsStored
echo.
echo.

echo ==========================================
echo   TEST 2 - Kill Germany (Denmark isolated)
echo   Expected: Denmark-^>Czech fails
echo   Expected: Poland-^>Czech works via Austria
echo ==========================================
echo.
echo [ACTION] Killing Germany...
for /f "tokens=5" %%a in ('netstat -ano ^| findstr :5001 ^| findstr LISTENING') do taskkill /PID %%a /F >nul 2>&1
echo [ACTION] Waiting 30 seconds for dead detection...
timeout /t 30 >nul
echo.
echo Snapshot Denmark:
curl -s http://localhost:5000/network/snapshot
echo.
echo Denmark to Czech (should fail - isolated):
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-2a\",\"sourceNode\":\"denmark\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"RFRLQ1o=\"}"
echo.
echo Poland to Czech (should work via Austria):
curl -s -X POST http://localhost:5002/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-2b\",\"sourceNode\":\"poland\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"UFZMQ1o=\"}"
echo.
echo.

echo ==========================================
echo   TEST 3 - Revive Germany
echo ==========================================
echo.
echo [ACTION] Restarting Germany...
start "SFDRN-Germany" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Germany.json && set ASPNETCORE_URLS=http://localhost:5001 && dotnet run --no-launch-profile > germany.log 2>&1"
echo [ACTION] Waiting 120 seconds...
timeout /t 120 >nul
echo.
echo Snapshot Denmark (all 5 Alive):
curl -s http://localhost:5000/network/snapshot
echo.
echo Denmark to Czech (should work again):
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-3\",\"sourceNode\":\"denmark\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"RFRLQ1oy\"}"
echo.
echo.

echo ==========================================
echo   TEST 4 - Kill Poland, reroute via Austria
echo   Expected: denmark-^>germany-^>austria-^>czech
echo ==========================================
echo.
echo [ACTION] Killing Poland...
for /f "tokens=5" %%a in ('netstat -ano ^| findstr :5002 ^| findstr LISTENING') do taskkill /PID %%a /F >nul 2>&1
echo [ACTION] Waiting 30 seconds...
timeout /t 30 >nul
echo.
echo Denmark to Czech with Poland dead (reroute via Austria):
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-4\",\"sourceNode\":\"denmark\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"UmVyb3V0ZQ==\"}"
echo.
echo Snapshot Denmark:
curl -s http://localhost:5000/network/snapshot
echo.
echo.

echo ==========================================
echo   TEST 5 - Full recovery
echo ==========================================
echo.
echo [ACTION] Restarting Poland...
start "SFDRN-Poland" cmd /c "set SFDRN_NODE_CONFIG=appsettings.Poland.json && set ASPNETCORE_URLS=http://localhost:5002 && dotnet run --no-launch-profile > poland.log 2>&1"
echo [ACTION] Waiting 120 seconds...
timeout /t 120 >nul
echo.
echo Denmark snapshot (all 5 Alive):
curl -s http://localhost:5000/network/snapshot
echo.
echo Czech snapshot (all 5 Alive):
curl -s http://localhost:5004/network/snapshot
echo.
echo Final packet Denmark to Czech:
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-final\",\"sourceNode\":\"denmark\",\"destinationNode\":\"czech\",\"ttl\":10,\"encryptedPayload\":\"RmluYWw=\"}"
echo.
echo.

echo ==========================================
echo   Stress Test completed!
echo ==========================================
echo.
pause