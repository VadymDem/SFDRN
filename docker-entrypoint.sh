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
    # Пробуем получить IP адреса из домена (если он уже активен)
    DNS_IPS=$(dig +short sfdrn.qzz.io | grep -E '^[0-9.]+$' || true)
    
    if [ -n "$DNS_IPS" ]; then
        echo "Found nodes via DNS: $DNS_IPS"
        # Формируем список URL из полученных IP
        NEIGHBORS_JSON=$(echo "$DNS_IPS" | jq -R -s -c 'split("\n") | map(select(length > 0) | "http://\(.):5000")')
    else
        echo "No nodes found via DNS. Starting as a potential pioneer."
        NEIGHBORS_JSON="[]"
    fi
else
    echo "Using provided bootstrap list: $BOOTSTRAP_NODES"
    NEIGHBORS_JSON=$(echo "$BOOTSTRAP_NODES" | jq -R 'split(",") | map(select(length > 0))')
fi

# 5. Generate appsettings.json
cat > /app/appsettings.json <<EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
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
echo "  Bootstrap: ${BOOTSTRAP_NODES:-NONE (pioneer node)}"
echo "=========================================="
echo "Starting SFDRN node..."
echo ""

# Start the application
# Force bind to all interfaces inside the container
export ASPNETCORE_URLS="http://0.0.0.0:5000"

# Start the application
exec dotnet SFDRN.dll