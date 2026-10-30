#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
if ! command -v xsp4 >/dev/null 2>&1; then
  echo "ERROR: xsp4 no está instalado. Usa 'docker compose up --build' o instala mono-xsp4." >&2
  exit 1
fi
exec xsp4 --nonstop --port 8080 --address 0.0.0.0 --root "$(pwd)"
