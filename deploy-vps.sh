#!/bin/bash
# deploy-vps.sh - Deploy SFDRN node to VPS

set -e

VPS_IP=$1
BOOTSTRAP_NODES=$2
SSH_KEY=${SSH_KEY:-~/.ssh/id_rsa}
SSH_USER=${SSH_USER:-root}

if [ -z "$VPS_IP" ]; then
    echo "Usage: ./deploy-vps.sh <VPS_IP> [BOOTSTRAP_NODES]"
    echo ""
    echo "Examples:"
    echo "  # First node (pioneer):"
    echo "  ./deploy-vps.sh 1.2.3.4"
    echo ""
    echo "  # Additional nodes:"
    echo "  ./deploy-vps.sh 5.6.7.8 http://1.2.3.4:5000"
    echo "  ./deploy-vps.sh 9.10.11.12 http://1.2.3.4:5000,http://5.6.7.8:5000"
    exit 1
fi

echo "=========================================="
echo "SFDRN Node Deployment"
echo "=========================================="
echo "Target VPS: $VPS_IP"
echo "Bootstrap:  ${BOOTSTRAP_NODES:-NONE (first node)}"
echo "=========================================="
echo ""

# 1. Create deployment directory on VPS
echo "[1/5] Creating deployment directory..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" "mkdir -p /opt/sfdrn"

# 2. Copy project files
echo "[2/5] Copying project files..."
scp -i "$SSH_KEY" -r \
    Dockerfile \
    docker-entrypoint.sh \
    docker-compose.yml \
    *.cs \
    *.csproj \
    "$SSH_USER@$VPS_IP:/opt/sfdrn/"

# 3. Create .env file with bootstrap configuration
echo "[3/5] Creating environment configuration..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" bash <<EOF
cd /opt/sfdrn
cat > .env <<ENVFILE
BOOTSTRAP_NODES=${BOOTSTRAP_NODES}
PORT=5000
ENVFILE
EOF

# 4. Build and start the container
echo "[4/5] Building and starting SFDRN node..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" bash <<'EOF'
cd /opt/sfdrn

# Install Docker if not present
if ! command -v docker &> /dev/null; then
    echo "Installing Docker..."
    curl -fsSL https://get.docker.com -o get-docker.sh
    sh get-docker.sh
    rm get-docker.sh
fi

# Install Docker Compose if not present
if ! command -v docker-compose &> /dev/null; then
    echo "Installing Docker Compose..."
    curl -L "https://github.com/docker/compose/releases/latest/download/docker-compose-$(uname -s)-$(uname -m)" -o /usr/local/bin/docker-compose
    chmod +x /usr/local/bin/docker-compose
fi

# Build and start
docker-compose down 2>/dev/null || true
docker-compose build
docker-compose up -d
EOF

# 5. Show logs and status
echo "[5/5] Checking node status..."
sleep 5
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" "cd /opt/sfdrn && docker-compose logs --tail=50"

echo ""
echo "=========================================="
echo "Deployment completed!"
echo "=========================================="
echo "Node endpoint: http://$VPS_IP:5000"
echo "Health check:  curl http://$VPS_IP:5000/health"
echo "View logs:     ssh $SSH_USER@$VPS_IP 'cd /opt/sfdrn && docker-compose logs -f'"
echo "=========================================="