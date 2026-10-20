#!/usr/bin/env bash
# Runs everything for development: the local database, the API and the web dev server.
# Open http://localhost:5173. Ctrl+C stops the API and the dev server; the database keeps running.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

"$root/scripts/local-db.sh"

# Stop the background API whenever this script ends.
trap 'kill 0' EXIT

echo "Starting the API on http://localhost:5258..."
dotnet run --project "$root/src/Spindle.Api" --launch-profile http &

cd "$root/src/web"
if [[ ! -d node_modules ]]; then
  npm ci
fi
npm run dev
