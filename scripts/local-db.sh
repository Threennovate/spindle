#!/usr/bin/env bash
# Starts local PostgreSQL in Docker, points the API and the integration tests at it,
# and applies migrations. Safe to run repeatedly: the password and the data volume are reused.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
env_file="$root/infra/.env"
api_project="$root/src/Spindle.Api"

if ! docker info >/dev/null 2>&1; then
  if command -v colima >/dev/null 2>&1; then
    echo "Starting Colima..."
    colima start --vm-type vz --vz-rosetta --cpu 4 --memory 4
  else
    echo "Docker is not running. Start it, or install Colima: brew install colima docker docker-compose" >&2
    exit 1
  fi
fi

if [[ ! -f "$env_file" ]] || ! grep -q '^POSTGRES_PASSWORD=' "$env_file"; then
  # Letters and digits only, so it needs no escaping in connection strings or shells.
  random="$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9')"
  printf '\nPOSTGRES_PASSWORD=%s\n' "${random:0:40}" >>"$env_file"
  chmod 600 "$env_file"
  echo "Added a PostgreSQL password to infra/.env."
fi
# shellcheck source=/dev/null
source "$env_file"

echo "Starting PostgreSQL and Mailpit..."
docker compose -f "$root/infra/docker-compose.yml" up --detach --wait postgres mailpit

server="Host=localhost;Port=15432;Username=spindle;Password=$POSTGRES_PASSWORD"
dotnet user-secrets set "ConnectionStrings:Default" "$server;Database=Spindle" --project "$api_project" >/dev/null
dotnet user-secrets set "ConnectionStrings:PostgresTests" "$server;Database=postgres" --id spindle-tests >/dev/null

if ! dotnet user-secrets list --project "$api_project" | grep -q '^Auth:SigningKey '; then
  dotnet user-secrets set "Auth:SigningKey" "$(openssl rand -base64 48)" --project "$api_project" >/dev/null
fi

echo "Applying migrations..."
dotnet tool restore >/dev/null
dotnet ef database update --project "$root/src/Spindle.Infrastructure" --startup-project "$api_project"

echo "Ready: PostgreSQL on localhost:15432 (database Spindle); emails at http://localhost:8025."
