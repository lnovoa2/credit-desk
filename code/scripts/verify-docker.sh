#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
cleanup() { docker compose down >/dev/null 2>&1 || true; }
trap cleanup EXIT INT TERM

docker compose build --no-cache
docker compose up -d

wait_page() {
  url="$1"
  expected="$2"
  i=0
  while [ "$i" -lt 45 ]; do
    BODY=$(curl -fsS "$url" 2>/dev/null || true)
    if echo "$BODY" | grep -q "$expected"; then return 0; fi
    i=$((i + 1)); sleep 1
  done
  echo "ERROR: $url did not render expected content: $expected" >&2
  docker compose logs >&2 || true
  exit 1
}

wait_page http://localhost:8080/Default.aspx "Operations Dashboard"
wait_page http://localhost:8080/Customers.aspx "Customer Credit File"
wait_page http://localhost:8080/OrderEntry.aspx "Order Credit Authorization"
wait_page http://localhost:8080/Batch.aspx "Night Credit Batch"

docker compose exec -T legacy-credit-desk test -s /app/App_Data/legacy-credit-desk.db
CUSTOMERS=$(docker compose exec -T legacy-credit-desk sqlite3 /app/App_Data/legacy-credit-desk.db "select count(*) from Customers;")
ORDERS=$(docker compose exec -T legacy-credit-desk sqlite3 /app/App_Data/legacy-credit-desk.db "select count(*) from Orders;")
VIEWS=$(docker compose exec -T legacy-credit-desk sqlite3 /app/App_Data/legacy-credit-desk.db "select count(*) from sqlite_master where type='view' and name in ('vw_CustomerExposure','vw_ManualReviewQueue');")
TRIGGERS=$(docker compose exec -T legacy-credit-desk sqlite3 /app/App_Data/legacy-credit-desk.db "select count(*) from sqlite_master where type='trigger' and name='trg_OrderBlockedAudit';")
[ "$CUSTOMERS" -ge 5 ]
[ "$ORDERS" -ge 2 ]
[ "$VIEWS" -eq 2 ]
[ "$TRIGGERS" -eq 1 ]

echo "OK: 4 WebForms pages render; SQLite exists; seed data, views and trigger are present"
