#!/usr/bin/env bash
set -euo pipefail
# Removes only the embedded DB volume; the next application boot recreates schema + seed data.
docker compose down -v
docker compose up --build
