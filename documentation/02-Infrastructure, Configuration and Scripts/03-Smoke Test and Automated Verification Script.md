# Functional Specification: Smoke Test and Automated Verification Script

**Parent Module**: Infrastructure, Configuration and Scripts | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: End-to-end automated smoke test that validates the entire application stack — from Docker image build through HTTP page rendering to SQLite database state — in a single script execution.
- **Key Functionality**:
  - Builds the Docker image from scratch (no cache) and starts the container in detached mode
  - Polls all four WebForms pages via HTTP until expected content strings appear (or timeout after 45 seconds per page)
  - Verifies the SQLite database file exists, contains seed data, and has the required views and trigger
  - Reports a clear "OK" on full pass or exits non-zero with Docker logs on failure
  - Automatically tears down the container on exit via signal trap
- **Users**: Developers, CI/CD pipelines, QA engineers verifying a working deployment

---

## 2. Business Logic

### Core Processes

**Full-Stack Smoke Test**: Sequential verification of every layer of the application from container build to database state.
1. Build the Docker image with `--no-cache` to ensure a clean, reproducible build
2. Start the container in detached mode (`docker compose up -d`)
3. Poll each of the four WebForms pages via `curl` until expected content is found or 45-second timeout expires
4. If any page fails to render its expected content, dump Docker logs to stderr and exit with code 1
5. Run four `sqlite3` queries inside the running container to assert database correctness
6. Print "OK" message on full success; signal trap ensures `docker compose down` runs on any exit

**HTTP Page Polling (`wait_page`)**: Resilient polling loop that accounts for container startup latency.
1. Accepts a URL and an expected content string as arguments
2. Issues `curl -fsS` up to 45 times with 1-second intervals between attempts
3. Checks response body for the expected string via `grep -q`
4. Returns 0 on first successful match; exits 1 with diagnostic output after 45 failed attempts

### Critical Validations

| Check | Assertion | Failure Action |
|-------|-----------|----------------|
| `Default.aspx` renders | Body contains "Operations Dashboard" | Log + exit 1 |
| `Customers.aspx` renders | Body contains "Customer Credit File" | Log + exit 1 |
| `OrderEntry.aspx` renders | Body contains "Order Credit Authorization" | Log + exit 1 |
| `Batch.aspx` renders | Body contains "Night Credit Batch" | Log + exit 1 |
| SQLite file exists | `test -s /app/App_Data/legacy-credit-desk.db` | Immediate exit 1 |
| Seed customers loaded | `COUNT(*) FROM Customers >= 5` | Exit 1 (shell `[` assertion) |
| Seed orders loaded | `COUNT(*) FROM Orders >= 2` | Exit 1 (shell `[` assertion) |
| Required views exist | COUNT of `vw_CustomerExposure`, `vw_ManualReviewQueue` = 2 | Exit 1 |
| Audit trigger exists | COUNT of `trg_OrderBlockedAudit` = 1 | Exit 1 |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `scripts/verify-docker.sh` | Orchestrates the full smoke test | Build image, start container, poll pages, assert DB, teardown |
| `wait_page` shell function | Resilient HTTP content check | Retry loop with 45-second timeout per page |
| `cleanup` shell trap | Guaranteed container teardown | Runs `docker compose down` on EXIT, INT, TERM signals |

### Frontend

| Component | Purpose | Content String Verified |
|-----------|---------|------------------------|
| `Default.aspx` | Operations dashboard | "Operations Dashboard" |
| `Customers.aspx` | Customer credit file management | "Customer Credit File" |
| `OrderEntry.aspx` | Order entry and credit authorization | "Order Credit Authorization" |
| `Batch.aspx` | Nightly batch monitoring | "Night Credit Batch" |

### Data

| Entity | Purpose | Assertion |
|--------|---------|-----------|
| `Customers` table | Seed customer data | `COUNT(*) >= 5` |
| `Orders` table | Seed order data | `COUNT(*) >= 2` |
| `vw_CustomerExposure` | Customer exposure view | Must exist in `sqlite_master` |
| `vw_ManualReviewQueue` | Manual review queue view | Must exist in `sqlite_master` |
| `trg_OrderBlockedAudit` | Order blocked audit trigger | Must exist in `sqlite_master` |
| `legacy-credit-desk.db` | SQLite database file | Must exist and be non-empty (`test -s`) |

---

## 4. APIs and Endpoints

### Endpoints Verified

| Method | URL | Purpose | Expected Content |
|--------|-----|---------|-----------------|
| GET | `http://localhost:8080/Default.aspx` | Operations dashboard availability | "Operations Dashboard" |
| GET | `http://localhost:8080/Customers.aspx` | Customer credit file availability | "Customer Credit File" |
| GET | `http://localhost:8080/OrderEntry.aspx` | Order entry page availability | "Order Credit Authorization" |
| GET | `http://localhost:8080/Batch.aspx` | Batch monitoring page availability | "Night Credit Batch" |

**HTTP Verification Pattern**:
```sh
# Request (via curl inside wait_page)
curl -fsS http://localhost:8080/Default.aspx

# Success: body contains expected string → return 0
# Failure after 45 retries: print error, dump docker compose logs, exit 1
```

**Database Verification Pattern**:
```sh
# Run sqlite3 inside the running container
docker compose exec -T legacy-credit-desk sqlite3 /app/App_Data/legacy-credit-desk.db \
  "select count(*) from Customers;"
# Assert result >= 5 via POSIX [ assertion
```

---

## 5. Data Flows

### Main Flow: End-to-End Smoke Test

```
Developer/CI → verify-docker.sh → docker compose build --no-cache
                                          ↓
                                  docker compose up -d
                                          ↓
                              wait_page × 4 (curl polling)
                                          ↓
                              HTTP 200 + content match × 4
                                          ↓
                         sqlite3 assertions × 4 inside container
                                          ↓
                           "OK" message → exit 0 / exit 1 + logs
                                          ↓
                              cleanup trap → docker compose down
```

**Steps**:
1. Script changes to project root relative to its own location
2. Registers cleanup trap for guaranteed teardown on any exit signal
3. Builds image and starts container; all four WebForms pages are polled in sequence
4. Database assertions run only after all four pages pass (fail-fast ordering)
5. Success or failure reported; container always torn down regardless of outcome

### Error Handling

| Error | Cause | Action |
|-------|-------|--------|
| `wait_page` timeout | Container slow to start or page error | Dump `docker compose logs` to stderr, exit 1 |
| SQLite file missing | `EnsureDatabase()` not triggered yet | `test -s` fails, exit 1 |
| Seed count too low | Schema created but seed data missing | Shell `[` assertion fails, exit 1 |
| View/trigger absent | Schema migration incomplete | Shell `[` assertion fails, exit 1 |
| `docker compose build` fails | Dockerfile error, network issue | `set -eu` propagates non-zero exit, cleanup trap fires |
| `curl` unavailable | Missing on host | `curl` command not found, script aborts |

---

## 6. Integrations

### Internal

- **Container Build and Deployment** (`Dockerfile`, `docker-compose.yml`): `verify-docker.sh` drives `docker compose build` and `docker compose up -d`; the Compose service name `legacy-credit-desk` is referenced directly in `exec` commands
- **Web User Interface** (`Default.aspx`, `Customers.aspx`, `OrderEntry.aspx`, `Batch.aspx`): Pages are the HTTP targets; their rendered content strings are the pass/fail criteria
- **Data Access and Persistence Layer** (`LegacyRepository.EnsureDatabase()`): The DB schema, seed data, views, and trigger created by `EnsureDatabase()` are what the sqlite3 assertions validate
- **Application Bootstrap and Global Lifecycle** (`Global.asax`): `Application_Start` triggers `EnsureDatabase()` on the first HTTP request, which is why page polling precedes the DB assertions

### External

- **Docker / Docker Compose**: Container lifecycle management — build, start, exec, down
- **`curl`**: HTTP GET requests for page availability checks
- **`sqlite3` CLI**: SQL query execution inside the running container for database state assertions

---

## 7. Configuration

| Property | Purpose | Value |
|----------|---------|-------|
| Polling retries | Max attempts per page before failure | 45 (hardcoded) |
| Polling interval | Wait between retries | 1 second (hardcoded `sleep 1`) |
| Application port | HTTP port polled by curl | 8080 (hardcoded, matches Compose port mapping) |
| Container service name | Docker Compose service for `exec` calls | `legacy-credit-desk` (hardcoded, matches `docker-compose.yml`) |
| DB path inside container | SQLite file path for `test -s` and `sqlite3` | `/app/App_Data/legacy-credit-desk.db` (hardcoded) |
| Min customers threshold | Seed data assertion lower bound | 5 (`>= 5`) |
| Min orders threshold | Seed data assertion lower bound | 2 (`>= 2`) |
| Required views count | Number of views that must exist | 2 (`= 2`) |
| Required trigger count | Number of triggers that must exist | 1 (`= 1`) |

---

## 8. Summary

- **Components**: 0 backend application components, 0 frontend application components — 1 shell script (`verify-docker.sh`) with 2 internal shell constructs (`wait_page` function, `cleanup` trap)
- **Endpoints**: 4 HTTP GET endpoints verified (all WebForms pages on port 8080)
- **Business rules**: 9 assertions (4 HTTP content checks + 1 file existence check + 4 SQL count assertions)
- **Integrations**: 4 internal (Container Build, Web UI, Data Access Layer, Bootstrap), 3 external (Docker, curl, sqlite3 CLI)

---
**Note**: Document kept under 1500 lines. All essential functional information included. `verify-docker.sh` is the **only automated test harness** in the entire project — it provides integration-level coverage (network + database) but does not cover unit-level business logic (credit decision rules, batch scoring, etc.).
