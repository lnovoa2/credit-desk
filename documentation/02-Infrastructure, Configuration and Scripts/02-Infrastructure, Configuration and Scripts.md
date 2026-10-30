# Submodules of Module: Infrastructure, Configuration and Scripts

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 7
**Analysis Date**: 2026-08-10 09:04:23
**Parent Module**: Infrastructure, Configuration and Scripts

## Summary
Total submodules identified: **6**

### Distribution by Type
- Backend Components: 0
- Frontend Components: 0
- Data Layer: 0
- API/Services: 0
- Configuration: 2
- Utilities: 4

## Submodule Index
1. [ASP.NET Runtime Configuration](#aspnet-runtime-configuration) - Configuration
2. [Container Build and Deployment](#container-build-and-deployment) - Configuration
3. [Local Development Startup Script](#local-development-startup-script) - Utility
4. [Data Reset and Environment Rebuild Script](#data-reset-and-environment-rebuild-script) - Utility
5. [Smoke Test and Automated Verification Script](#smoke-test-and-automated-verification-script) - Utility
6. [Project Documentation and Business Knowledge](#project-documentation-and-business-knowledge) - Utility

---

## Submodule Detail

### ASP.NET Runtime Configuration

**Type**: Configuration

**Location**:
- **Configuration**: `web.config`

**Purpose**: Provides all runtime-tunable parameters consumed by the application code at startup, plus ASP.NET framework directives. Five `appSettings` keys drive the credit decision thresholds and batch cadence; the `system.web` block registers the Mono.Data.Sqlite assembly, disables custom errors, and targets .NET 4.0.

**Key Files**:
1. `web.config` - XML configuration file read by ASP.NET/Mono at startup. Defines `BatchIntervalSeconds` (15), `HighRiskScore` (72), `ManualReviewAmount` (7500), `HardBlockOverdueDays` (90), and `LegacyCountrySurcharge` ("PT,IT,GR"). Also registers `Mono.Data.Sqlite` assembly and sets `debug="true"`, `validateRequest="false"`, and `customErrors mode="Off"`.

**Dependencies**:
- **External Modules**: Credit Decision Engine (reads `HighRiskScore`, `ManualReviewAmount`, `LegacyCountrySurcharge`, `HardBlockOverdueDays`), Nightly Batch Processing (reads `BatchIntervalSeconds`, `HardBlockOverdueDays`), Data Access and Persistence Layer (Mono.Data.Sqlite assembly registration)
- **Internal Submodules**: None
- **Libraries**: ASP.NET / Mono runtime (consumer of the file)

**Related Components**:
- **Entities**: None
- **Services**: `CreditDecisionService.cs` — reads threshold keys via `ConfigurationManager.AppSettings`; `NightlyBatch.cs` — reads `BatchIntervalSeconds`
- **APIs**: None
- **UI**: None

**Integration Points**:
- Read at runtime by `System.Web.Configuration.WebConfigurationManager` / `System.Configuration.ConfigurationManager` throughout `App_Code/` classes

---

### Container Build and Deployment

**Type**: Configuration

**Location**:
- **Configuration**: `Dockerfile`, `docker-compose.yml`

**Purpose**: Defines the complete containerised runtime environment and the Compose-based single-service deployment. The Dockerfile layers Mono 6.12, installs mono-xsp4 and sqlite3 from archived Debian repos, copies the application, prepares `App_Data` with write permissions, and launches XSP4 on port 8080. The Compose file maps port 8080, mounts a named volume to persist the SQLite file across container restarts, and sets the restart policy.

**Key Files**:
1. `Dockerfile` - Multi-step image build: base `mono:6.12`, apt mirror redirect to `archive.debian.org`, installs `mono-xsp4` and `sqlite3`, sets `WORKDIR /app`, copies project, creates `App_Data` and `bin`, copies `Mono.Data.Sqlite.dll`, makes scripts executable, exposes port 8080, starts `xsp4`.
2. `docker-compose.yml` - Single-service Compose definition: builds from local context, maps host port 8080 → container 8080, mounts named volume `legacy-credit-data` at `/app/App_Data`, restart policy `unless-stopped`.

**Dependencies**:
- **External Modules**: All application modules (entire project is copied into the image)
- **Internal Submodules**: Local Development Startup Script (scripts made executable in Dockerfile), Smoke Test and Automated Verification Script (relies on image built here)
- **Libraries**: `mono:6.12` base image, `mono-xsp4`, `sqlite3`, `Mono.Data.Sqlite.dll`

**Related Components**:
- **Entities**: `App_Data/legacy-credit-desk.db` — persisted via `legacy-credit-data` Docker named volume
- **Services**: XSP4 — the ASP.NET host process started by CMD
- **APIs**: Port 8080 — the only network exposure
- **UI**: All `.aspx` pages served by XSP4

**Integration Points**:
- `docker compose up --build` is the primary recommended entry point (documented in README)
- `reset-data.sh` uses `docker compose down -v` and `docker compose up --build`
- `verify-docker.sh` uses `docker compose build --no-cache` and `docker compose up -d`

---

### Local Development Startup Script

**Type**: Utility

**Location**:
- **Scripts**: `scripts/run-local.sh`

**Purpose**: Provides a Docker-free local startup path for developers who already have Mono/XSP4 installed on their machine. Verifies that `xsp4` is on the PATH and, if found, executes it with the project root as the web root on port 8080. Exits with an informative error if the prerequisite is missing.

**Key Files**:
1. `scripts/run-local.sh` - POSIX `sh` script. Changes directory to the project root relative to its own location (`$(dirname "$0")/..`). Checks for `xsp4` via `command -v`; aborts with an instructional error if absent. Exec-replaces itself with `xsp4 --nonstop --port 8080 --address 0.0.0.0 --root "$(pwd)"`.

**Dependencies**:
- **External Modules**: None (no dependency on other application modules at the script level)
- **Internal Submodules**: Container Build and Deployment (alternative path; README directs users to Docker first)
- **Libraries**: POSIX `sh`, `xsp4` (mono-xsp4 package, must be pre-installed)

**Related Components**:
- **Entities**: None
- **Services**: XSP4 web server process
- **APIs**: Port 8080
- **UI**: All `.aspx` pages once the server is running

**Integration Points**:
- Documented in `README.md` under "Arranque sin Docker"
- Functionally equivalent to the Dockerfile CMD; difference is the absence of containerisation

---

### Data Reset and Environment Rebuild Script

**Type**: Utility

**Location**:
- **Scripts**: `scripts/reset-data.sh`

**Purpose**: Performs a full teardown of the Docker environment (including the persistent data volume) and rebuilds from scratch. On the next application boot, `LegacyRepository.EnsureDatabase()` recreates the SQLite schema and re-inserts seed data. Used to return to a known-clean state during testing or after data corruption.

**Key Files**:
1. `scripts/reset-data.sh` - `bash` script with `set -euo pipefail`. Runs `docker compose down -v` to destroy containers and named volumes, then `docker compose up --build` to rebuild the image and start the service.

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository.EnsureDatabase()` recreates data on next boot)
- **Internal Submodules**: Container Build and Deployment (`docker-compose.yml` drives the down/up cycle)
- **Libraries**: `bash`, Docker CLI, Docker Compose

**Related Components**:
- **Entities**: `App_Data/legacy-credit-desk.db` — destroyed by `-v` flag, recreated on first HTTP request
- **Services**: Docker named volume `legacy-credit-data`
- **APIs**: None
- **UI**: None

**Integration Points**:
- Documented in `README.md` under "Reset completo de la BBDD"
- Counterpart to the manual equivalent: `docker compose down -v && docker compose up --build`

---

### Smoke Test and Automated Verification Script

**Type**: Utility

**Location**:
- **Scripts**: `scripts/verify-docker.sh`

**Purpose**: End-to-end smoke test that builds the Docker image, starts the container, polls all four WebForms pages until they return expected content strings (45-second timeout per page), then uses `sqlite3` inside the container to assert that the database exists, contains seed data (≥5 customers, ≥2 orders), exposes both required views, and has the audit trigger. Reports "OK" on full pass or exits non-zero with Docker logs on failure.

**Key Files**:
1. `scripts/verify-docker.sh` - POSIX `sh` script with `set -eu`. Defines a `wait_page url expected` helper that polls with `curl` up to 45 times (1-second interval). Registers a `cleanup` trap on EXIT/INT/TERM to run `docker compose down`. Builds with `--no-cache`, starts detached. Calls `wait_page` for `Default.aspx` ("Operations Dashboard"), `Customers.aspx` ("Customer Credit File"), `OrderEntry.aspx` ("Order Credit Authorization"), `Batch.aspx` ("Night Credit Batch"). Then verifies db file exists and runs four `sqlite3` count queries inside the container.

**Dependencies**:
- **External Modules**: Web User Interface (page existence and content strings), Data Access and Persistence Layer (SQLite schema: tables `Customers`, `Orders`, views `vw_CustomerExposure`, `vw_ManualReviewQueue`, trigger `trg_OrderBlockedAudit`)
- **Internal Submodules**: Container Build and Deployment (image and compose file it drives)
- **Libraries**: POSIX `sh`, `curl`, `docker`, Docker Compose, `sqlite3` CLI

**Related Components**:
- **Entities**: `Customers`, `Orders`, `vw_CustomerExposure`, `vw_ManualReviewQueue`, `trg_OrderBlockedAudit`
- **Services**: XSP4 (HTTP responses tested via curl)
- **APIs**: `http://localhost:8080/{page}.aspx`
- **UI**: `Default.aspx`, `Customers.aspx`, `OrderEntry.aspx`, `Batch.aspx`

**Integration Points**:
- Documented in `README.md` under "Verificación automática"
- Only automated test harness in the project; covers full stack from image build to database state

---

### Project Documentation and Business Knowledge

**Type**: Utility

**Location**:
- **Documentation**: `docs/business-rules.md`, `docs/migration-test-objectives.md`, `README.md`

**Purpose**: Human-readable reference materials that describe the project's purpose, the legacy business rules (intentionally incomplete), the migration challenge objectives, and operational instructions. These files are not consumed by the application at runtime; they exist to guide migration tool developers and testers.

**Key Files**:
1. `README.md` - Main project overview in Spanish. Covers objective, legacy stack, embedded database structure, local startup with and without Docker, verification and reset procedures, domain complexity (credit decision factors), test scenarios, relevant file structure, rationale for SQLite, and compatibility notes.
2. `docs/business-rules.md` - Intentionally incomplete business-rule summary (six high-level rules). Explicitly states that thresholds, rule ordering, country penalties, urgency behavior, card behavior, and exact override semantics are not documented and must be recovered from source code.
3. `docs/migration-test-objectives.md` - Ten numbered objectives that a migration tool must achieve: discover Web Forms patterns, recover data model from `DataSet` schema, separate layers, detect split business logic, preserve rule ordering and short-circuit behavior, preserve override vs. blocking semantics, identify non-persisted state, identify concurrency protection, reproduce the batch, and generate regression tests.

**Dependencies**:
- **External Modules**: All modules (documentation references the full codebase)
- **Internal Submodules**: All other submodules in this module (README documents scripts and config)
- **Libraries**: None (static Markdown)

**Related Components**:
- **Entities**: All database entities referenced by name
- **Services**: Referenced conceptually: `CreditDecisionService`, `NightlyBatch`, `LegacyRepository`, `LegacyState`
- **APIs**: None
- **UI**: All four `.aspx` pages referenced in test scenarios

**Integration Points**:
- `README.md` is the entry point for any developer or tool approaching this project
- `docs/business-rules.md` is the documented source for domain knowledge (intentional gap with code)
- `docs/migration-test-objectives.md` drives the design of the entire fixture

---

## Relationship Map

### Data Flow

```text
[Developer / CI] → [verify-docker.sh] → [docker compose build] → [Dockerfile] → [Container]
                                                                        ↓
                                                              [web.config injected]
                                                                        ↓
                                                              [xsp4 serves .aspx pages]
                                                                        ↓
                                                       [curl polls pages] + [sqlite3 asserts DB]

[Developer] → [reset-data.sh] → [docker compose down -v] → [volume destroyed]
                                       ↓
                              [docker compose up --build] → [new container]
                                                                  ↓
                                                    [LegacyRepository.EnsureDatabase()]
                                                                  ↓
                                                       [schema + seed recreated]

[Developer (no Docker)] → [run-local.sh] → [xsp4 on host] → [reads web.config]
```

### Call Hierarchy
- `verify-docker.sh` calls `docker compose build`, `docker compose up -d`, `docker compose exec sqlite3`
- `reset-data.sh` calls `docker compose down -v`, `docker compose up --build`
- `run-local.sh` calls `xsp4`
- `docker-compose.yml` references `Dockerfile` for image build
- `Dockerfile` copies all project files including `web.config` and `scripts/`
- Application code reads `web.config` via `ConfigurationManager.AppSettings`

### UI–Backend Mapping
| UI / Script Component | Backend Service | DB Table / Object |
|---|---|---|
| `verify-docker.sh` | XSP4 (HTTP) | `Customers`, `Orders`, `vw_CustomerExposure`, `vw_ManualReviewQueue`, `trg_OrderBlockedAudit` |
| `reset-data.sh` | Docker Compose + `LegacyRepository.EnsureDatabase()` | All tables (recreated) |
| `run-local.sh` | XSP4 (host) | All (via full application) |
| `web.config` | `CreditDecisionService`, `NightlyBatch`, `LegacyRepository` | None (config only) |

---

## Completeness Verification

### File Statistics
- Total configuration files: 2 — Documented: 2 (`web.config`, `docker-compose.yml`)
- Total container files: 1 — Documented: 1 (`Dockerfile`)
- Total shell scripts: 3 — Documented: 3 (`run-local.sh`, `reset-data.sh`, `verify-docker.sh`)
- Total documentation files: 3 — Documented: 3 (`README.md`, `docs/business-rules.md`, `docs/migration-test-objectives.md`)
- Total JavaScript files: 0 — N/A
- Total CSS files: 0 — N/A
- Total SQL scripts: 0 — N/A
- **Coverage**: 100%

### Directories Analyzed
- [x] `/` (root, module-scope files) — 3 files (`web.config`, `Dockerfile`, `docker-compose.yml`)
- [x] `/scripts/` — 3 files (`run-local.sh`, `reset-data.sh`, `verify-docker.sh`)
- [x] `/docs/` — 2 files (`business-rules.md`, `migration-test-objectives.md`)
- [x] `/` (root, `README.md`) — 1 file

---

## Technical Notes

- `web.config` is the single source of runtime-tunable thresholds. Changing any of the five `appSettings` keys alters credit decision behavior, batch cadence, and hard-block logic without recompilation.
- The Dockerfile redirects apt sources to `archive.debian.org` because the base image (Debian Buster / Mono 6.12) has reached end-of-life; this is a maintenance liability.
- `verify-docker.sh` is the only automated test harness for the entire project. It provides integration-level coverage (network + database) but no unit or business-logic coverage.
- `docs/business-rules.md` is deliberately incomplete by design — it is part of the migration challenge, not an oversight.
- The `documentation/` directory tree (pipeline output, cost records) is also part of this module per the Phase 1 assignment, but its contents are generated artifacts from the CodeToDocAnalyzer tool rather than hand-authored operational files.

---

## Complexity and Prioritisation

### Complexity by Submodule
- **High**: Smoke Test and Automated Verification Script (`verify-docker.sh`) — orchestrates full stack; contains HTTP polling loop, multi-assertion SQL checks, signal traps
- **Medium**: Container Build and Deployment (`Dockerfile` + `docker-compose.yml`) — non-trivial due to archived Debian mirror workaround, manual DLL copy, volume and permission setup
- **Medium**: ASP.NET Runtime Configuration (`web.config`) — simple file but high impact; all five keys directly gate production behavior
- **Low**: Data Reset and Environment Rebuild Script (`reset-data.sh`) — two-command script, trivial logic
- **Low**: Local Development Startup Script (`run-local.sh`) — minimal guard + exec, straightforward
- **Low**: Project Documentation and Business Knowledge (`README.md`, `docs/`) — static Markdown, no runtime impact

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analysed in full detail to produce the complete functional specification.
