# Functional Specification: Local Development Startup Script

**Parent Module**: Infrastructure, Configuration and Scripts | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides a Docker-free local startup path that launches the LegacyCreditDesk ASP.NET application directly on the developer's host machine using a pre-installed Mono/XSP4 runtime, bypassing the containerised deployment entirely.
- **Key Functionality**:
  - Verifies that the `xsp4` binary is available on the host PATH before attempting startup
  - Navigates to the project root regardless of the caller's working directory
  - Starts the XSP4 web server bound to all network interfaces on port 8080
  - Replaces itself with the server process (exec semantics — no wrapper process remains)
  - Provides an actionable error message when the prerequisite is missing
- **Users**: Developers who have Mono/XSP4 installed locally and prefer to avoid Docker overhead during development

---

## 2. Business Logic

### Key Processes

**Local Server Startup**: Launches XSP4 web server as a direct host process alternative to Docker.
1. Script resolves the project root relative to its own location (`$(dirname "$0")/..`)
2. Changes working directory to the project root so XSP4 can locate `web.config` and `.aspx` files
3. Checks whether `xsp4` is available using POSIX `command -v`
4. If `xsp4` is absent, prints an instructional error to stderr and exits with code 1
5. If `xsp4` is present, exec-replaces the script process with `xsp4 --nonstop --port 8080 --address 0.0.0.0 --root "$(pwd)"`

### Critical Validations

| Field | Validation | Error |
|-------|------------|-------|
| `xsp4` binary | Must be resolvable via `command -v` on PATH | `ERROR: xsp4 no está instalado. Usa 'docker compose up --build' o instala mono-xsp4.` (exits 1) |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `scripts/run-local.sh` | Host-native application launcher | Prerequisite check, directory resolution, XSP4 process handoff |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| N/A | — | Script has no UI component; it starts the server that serves existing `.aspx` pages |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| N/A | — | No data entities owned by this script; all data is managed by the application modules it starts |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| GET | `http://localhost:8080/` | Application entry point once server is running | No |
| GET | `http://localhost:8080/Default.aspx` | Operations dashboard | No |
| GET | `http://localhost:8080/Customers.aspx` | Customer credit file | No |
| GET | `http://localhost:8080/OrderEntry.aspx` | Order entry and credit decision | No |
| GET | `http://localhost:8080/Batch.aspx` | Batch status and manual trigger | No |

> Note: These endpoints are exposed by XSP4 after startup. The script itself does not define or handle HTTP traffic.

**Invocation**:
```sh
# From project root or any directory
./scripts/run-local.sh
```

**XSP4 Command Executed**:
```sh
exec xsp4 --nonstop --port 8080 --address 0.0.0.0 --root "$(pwd)"
```

---

## 5. Data Flows

### Main Flow: Local Startup Without Docker

```
Developer → ./scripts/run-local.sh → directory resolution → xsp4 check
         → [FAIL] xsp4 absent → error to stderr → exit 1
         → [OK] xsp4 present → exec xsp4 → server listening on :8080
                                               ↓
                                        reads web.config
                                               ↓
                                        serves .aspx pages
                                               ↓
                                        LegacyRepository creates App_Data/legacy-credit-desk.db (first request)
```

**Steps**:
1. Developer runs the script from any directory
2. Script resolves and changes to the project root
3. Script validates `xsp4` availability; aborts with error if missing
4. Script exec-replaces itself with XSP4 bound to `0.0.0.0:8080`
5. XSP4 reads `web.config`, serves pages, and triggers database creation on first HTTP request

### Error Handling

| Error | Cause | Action |
|-------|-------|--------|
| `xsp4` not found | `mono-xsp4` package not installed on host | Print error to stderr, exit 1 |
| Port 8080 already in use | Another process bound to port 8080 | XSP4 startup fails with OS-level error (not handled by script) |
| `web.config` missing | Script run from wrong directory or corrupt checkout | XSP4 fails to load application configuration |

---

## 6. Integrations

### Internal

- **Container Build and Deployment** (`Dockerfile` / `docker-compose.yml`): Functional equivalent — both paths start XSP4 on port 8080 and read `web.config`. The script is made executable inside the Dockerfile (`chmod +x scripts/`).
- **ASP.NET Runtime Configuration** (`web.config`): Read by XSP4 at startup; provides all application thresholds and assembly registrations.
- **Data Access and Persistence Layer** (`LegacyRepository.cs`): Invoked on first HTTP request to create the SQLite schema and seed data at `App_Data/legacy-credit-desk.db`.
- **Project Documentation and Business Knowledge** (`README.md`): Documents this script under the "Arranque sin Docker" section as the alternative startup path.

### External

- **Mono/XSP4 runtime**: Required host dependency (`mono-xsp4` package). Must be pre-installed by the developer. Not bundled or managed by this script.

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `--port 8080` | TCP port XSP4 listens on | `8080` (hardcoded in script) |
| `--address 0.0.0.0` | Network interface binding | All interfaces (hardcoded) |
| `--nonstop` | Prevents XSP4 from exiting after handling first request | Enabled (hardcoded) |
| `--root "$(pwd)"` | Web root directory passed to XSP4 | Project root resolved at runtime |

> There are no environment variables or external configuration files consumed by the script itself. All runtime application configuration is handled by `web.config` once XSP4 starts.

---

## 8. Summary

- **Components**: 0 backend services, 0 frontend views, 0 data entities — 1 utility script (`run-local.sh`)
- **Endpoints**: 0 defined by this script (5 HTTP endpoints exposed by XSP4 after startup)
- **Business Rules**: 1 (prerequisite check: `xsp4` must be on PATH)
- **Integrations**: 4 internal (Container Build and Deployment, ASP.NET Runtime Configuration, Data Access and Persistence Layer, Project Documentation), 1 external (Mono/XSP4 runtime)

---
*Note: Document kept under 1500 lines. Essential functional information prioritised.*
