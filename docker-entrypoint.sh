#!/bin/bash
set -e

echo "=========================================="
echo "SFDRN Node - Auto Configuration"
echo "=========================================="

# 1. Auto-generate NODE_ID if not provided
if [ -z "$NODE_ID" ]; then
    NODE_ID="node-$(head /dev/urandom | tr -dc a-z0-9 | head -c 8)"
    echo "Generated NODE_ID: $NODE_ID"
else
    echo "Using provided NODE_ID: $NODE_ID"
fi

# 2. Auto-detect external IP if PUBLIC_ENDPOINT not provided
if [ -z "$PUBLIC_ENDPOINT" ]; then
    echo "Detecting external IP..."
    EXTERNAL_IP=$(curl -s --max-time 5 https://api.ipify.org || echo "127.0.0.1")
    PUBLIC_ENDPOINT="http://${EXTERNAL_IP}:5000"
    echo "Detected PUBLIC_ENDPOINT: $PUBLIC_ENDPOINT"
else
    echo "Using provided PUBLIC_ENDPOINT: $PUBLIC_ENDPOINT"
fi

# 3. Auto-detect region by IP geolocation
if [ -z "$REGION" ]; then
    echo "Detecting region..."
    EXTERNAL_IP_FOR_GEO=$(echo $PUBLIC_ENDPOINT | sed -n 's/.*\/\/\([^:]*\).*/\1/p')
    REGION=$(curl -s --max-time 5 "https://ipapi.co/${EXTERNAL_IP_FOR_GEO}/country_name/" || echo "Unknown")
    echo "Detected REGION: $REGION"
else
    echo "Using provided REGION: $REGION"
fi

# 4. Parse BOOTSTRAP_NODES (comma-separated, can be empty)
if [ -z "$BOOTSTRAP_NODES" ]; then
    echo "No bootstrap nodes provided. Checking DNS for sfdrn.qzz.io..."
    DNS_IPS=$(dig +short sfdrn.qzz.io | grep -E '^[0-9.]+$' || true)
    
    if [ -n "$DNS_IPS" ]; then
        echo "Found nodes via DNS: $DNS_IPS"
        NEIGHBORS_JSON=$(echo "$DNS_IPS" | jq -R -s -c 'split("\n") | map(select(length > 0) | "http://\(.):5000")')
    else
        echo "No nodes found via DNS. Starting as a potential pioneer."
        NEIGHBORS_JSON="[]"
    fi
else
    echo "Using provided bootstrap list: $BOOTSTRAP_NODES"
    NEIGHBORS_JSON=$(echo "$BOOTSTRAP_NODES" | jq -R 'split(",") | map(select(length > 0))')
fi

# 5. Database path configuration
DB_PATH=${DB_PATH:-/app/data}
mkdir -p "$DB_PATH"
echo "Database path: $DB_PATH"

# 6. Check existing database
DB_FILE="$DB_PATH/sfdrn.db"
if [ -f "$DB_FILE" ]; then
    echo "Found existing database: $DB_FILE"
    DB_SIZE=$(du -h "$DB_FILE" | cut -f1)
    echo "Database size: $DB_SIZE"
    
    # Показаем статистику
    PROFILE_COUNT=$(sqlite3 "$DB_FILE" "SELECT COUNT(*) FROM Profiles;" 2>/dev/null || echo "0")
    MESSAGE_COUNT=$(sqlite3 "$DB_FILE" "SELECT COUNT(*) FROM Messages;" 2>/dev/null || echo "0")
    echo "Profiles in DB: $PROFILE_COUNT"
    echo "Messages in DB: $MESSAGE_COUNT"
else
    echo "No existing database found. Will be created on first run."
fi

# 7. Generate appsettings.json
cat > /app/appsettings.json <<EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Database": {
    "Path": "$DB_PATH"
  },
  "Node": {
    "NodeId": "$NODE_ID",
    "Region": "$REGION",
    "PublicEndpoint": "$PUBLIC_ENDPOINT",
    "Neighbors": $NEIGHBORS_JSON
  }
}
EOF

echo "=========================================="
echo "Configuration generated:"
echo "  Node ID:   $NODE_ID"
echo "  Region:    $REGION"
echo "  Endpoint:  $PUBLIC_ENDPOINT"
echo "  Database:  $DB_FILE"
echo "  Bootstrap: ${BOOTSTRAP_NODES:-NONE (pioneer node)}"
echo "=========================================="
echo "Starting SFDRN node..."
echo ""

# Force bind to all interfaces inside the container
export ASPNETCORE_URLS="http://0.0.0.0:5000"

# Start the application
exec dotnet SFDRN.dll