#!/bin/bash
# deploy-vps.sh - Deploy SFDRN node to VPS with SQLite persistence

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
echo "SFDRN Node Deployment (with SQLite)"
echo "=========================================="
echo "Target VPS: $VPS_IP"
echo "Bootstrap:  ${BOOTSTRAP_NODES:-NONE (first node)}"
echo "=========================================="
echo ""

# 1. Create deployment directories on VPS
echo "[1/6] Creating deployment directories..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" << 'EOF'
mkdir -p /opt/sfdrn
mkdir -p /opt/sfdrn/data
mkdir -p /opt/sfdrn/backups
EOF

# 2. Copy project files
echo "[2/6] Copying project files..."
scp -i "$SSH_KEY" -r \
    Dockerfile \
    docker-entrypoint.sh \
    docker-compose.yml \
    *.cs \
    *.csproj \
    "$SSH_USER@$VPS_IP:/opt/sfdrn/"

# 3. Create .env file with configuration
echo "[3/6] Creating environment configuration..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" << EOF
cd /opt/sfdrn
cat > .env << 'ENVFILE'
BOOTSTRAP_NODES=${BOOTSTRAP_NODES}
PORT=5000
BACKUP_DIR=/opt/sfdrn/backups
ENVFILE
EOF

# 4. Create backup script
echo "[4/6] Creating backup script..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" << 'EOF'
cat > /opt/sfdrn/backup.sh << 'BACKUP'
#!/bin/bash
BACKUP_DIR=/opt/sfdrn/backups
DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="$BACKUP_DIR/sfdrn_$DATE.db"

# Copy database from container
docker cp sfdrn-node:/app/data/sfdrn.db "$BACKUP_FILE"

# Compress
gzip "$BACKUP_FILE"

# Keep only last 7 days
find "$BACKUP_DIR" -name "*.gz" -mtime +7 -delete

echo "Backup created: ${BACKUP_FILE}.gz"
BACKUP

chmod +x /opt/sfdrn/backup.sh
EOF

# 5. Build and start the container
echo "[5/6] Building and starting SFDRN node..."
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" << 'EOF'
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

# 6. Show logs and status
echo "[6/6] Checking node status..."
sleep 5
ssh -i "$SSH_KEY" "$SSH_USER@$VPS_IP" "cd /opt/sfdrn && docker-compose logs --tail=50"

echo ""
echo "=========================================="
echo "Deployment completed!"
echo "=========================================="
echo "Node endpoint:   http://$VPS_IP:5000"
echo "Health check:    curl http://$VPS_IP:5000/mesh/health"
echo "Network status:  curl http://$VPS_IP:5000/mesh/network"
echo ""
echo "Data persistence:"
echo "  Database:      /opt/sfdrn/data (Docker volume)"
echo "  Backups:       /opt/sfdrn/backups"
echo ""
echo "Commands:"
echo "  View logs:     ssh $SSH_USER@$VPS_IP 'cd /opt/sfdrn && docker-compose logs -f'"
echo "  Backup DB:     ssh $SSH_USER@$VPS_IP '/opt/sfdrn/backup.sh'"
echo "  Restart:       ssh $SSH_USER@$VPS_IP 'cd /opt/sfdrn && docker-compose restart'"
echo "=========================================="