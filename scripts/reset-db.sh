#!/bin/bash
# Reset PostgreSQL database in Docker
# Usage: chmod +x scripts/reset-db.sh && ./scripts/reset-db.sh

set -e

CONTAINER_NAME="postgres-prod"
DB_NAME="appdb"
DB_USER="appuser"
DB_PASSWORD="123456"

echo "=== Resetting database in Docker container '$CONTAINER_NAME' ==="

# Check if container is running
if ! docker ps --filter "name=$CONTAINER_NAME" --format "{{.Names}}" | grep -q "^${CONTAINER_NAME}$"; then
    echo "Error: Container '$CONTAINER_NAME' is not running."
    exit 1
fi

# Drop and recreate database
echo "Dropping database '$DB_NAME'..."
docker exec -it $CONTAINER_NAME psql -U $DB_USER -d postgres -c "DROP DATABASE IF EXISTS $DB_NAME;"

echo "Creating database '$DB_NAME'..."
docker exec -it $CONTAINER_NAME psql -U $DB_USER -d postgres -c "CREATE DATABASE $DB_NAME;"

echo "=== Database '$DB_NAME' has been reset successfully ==="
