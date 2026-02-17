@echo off
chcp 65001 >nul
echo.
echo ==========================================
echo   SFDRN Mesh Network - Test Suite
echo ==========================================
echo.
echo [TEST 1] Health Check - All Nodes
echo ----------------------------------
echo.
echo Node 1:
curl -s http://localhost:5000/health
echo.
echo Node 2:
curl -s http://localhost:5001/health
echo.
echo Node 3:
curl -s http://localhost:5002/health
echo.
echo [WAIT] Waiting 80 seconds for network convergence and cleanup...
timeout /t 80 >nul
echo.
echo [TEST 2] Network Snapshot (from Node 1)
echo ----------------------------------------
curl -s http://localhost:5000/network/snapshot
echo.
echo.
echo [TEST 3] Sending Test Packet
echo -----------------------------
echo Sending packet from local-01 to local-03...
echo.
curl -s -X POST http://localhost:5000/routing/forward ^
  -H "Content-Type: application/json" ^
  -d "{\"packetId\":\"test-packet-1\",\"sourceNode\":\"local-01\",\"destinationNode\":\"local-03\",\"ttl\":10,\"encryptedPayload\":\"SGVsbG8gV29ybGQh\"}"
echo.
echo.
echo [TEST 4] Check Node 3 Received Packet
echo --------------------------------------
curl -s http://localhost:5002/network/snapshot
echo.
timeout /t 5 >nul
echo.
echo [TEST 5] Final Network State (After Cleanup)
echo --------------------------------------------
curl -s http://localhost:5000/network/snapshot
echo.
echo.
echo ==========================================
echo   Test completed!
echo ==========================================
echo.
pause