# Functional Specification: Container Build and Deployment

**Parent Module**: Infrastructure, Configuration and Scripts | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Defines the complete containerized runtime environment for the LegacyCreditDesk ASP.NET Web Forms application, using Docker and Docker Compose to package and run the application on Mono/XSP4.
- **Key Functionality**:
  - Builds a reproducible Docker image based on Mono 6.12 with all required dependencies
  - Manages a workaround for end-of-life Debian Buster package repositories (archive.debian.org)
  - Persists the SQLite database file across container restarts via a named Docker volume
  - Exposes the application on port 8080 with an automatic restart policy
  - Copies and provisions the `Mono.Data.Sqlite.dll` driver into the application's `bin/` directory
- **Users**: Developers, DevOps engineers, and migration tool operators who deploy and run the application

---

## 2. Business Logic

### Key Processes

**Image Build**: Constructs the runtime environment from source
1. Starts from `mono:6.12` base image (Debian Buster)
2. Redirects apt package sources to `archive.debian.org` (EOL workaround)
3. Installs `mono-xsp4` (ASP.NET host) and `sqlite3` (CLI tool)
4. Copies the entire project into `/app`
5. Creates `App_Data/` and `bin/` directories, copies `Mono.Data.Sqlite.dll` from the Mono global assembly cache into `/app/bin/`, sets `App_Data` permissions to `777`, and marks all shell scripts executable
6. Starts XSP4 on port 8080 bound to all interfaces

**Container Startup**: Launches the application service
1. Docker Compose builds the image from the local context (`.`)
2. Maps host port 8080 to container port 8080
3. Mounts the named volume `legacy-credit-data` at `/app/App_Data` (SQLite persistence)
4. XSP4 starts as the container process; `LegacyRepository.EnsureDatabase()` creates the schema and seed data on first access if the database file does not exist

**Data Persistence**: Ensures the SQLite database survives container lifecycle
1. Named volume `legacy-credit-data` is managed by Docker
2. Volume is attached at `/app/App_Data` where `legacy-credit-desk.db` is stored
3. On `docker compose down` (without `-v`) the database is preserved
4. On `docker compose down -v` (reset script) the database is destroyed and recreated on next boot

### Critical Validations

| Item | Rule | Effect |
|------|------|--------|
| apt sources | Redirected to `archive.debian.org` | Required because Debian Buster has reached EOL; without this, `apt-get update` fails |
| `App_Data` permissions | Set to `777` | XSP4 runs as a non-root user inside Mono; write access is needed for SQLite to create/modify the `.db` file |
| `Mono.Data.Sqlite.dll` | Manually copied to `/app/bin/` | ASP.NET on Mono requires the SQLite driver in the application `bin/` directory for `DbProviderFactory` resolution |
| Port exposure | Only port 8080 is exposed | No other service port is published; the application has no external database server |

---

## 3. Components

### Infrastructure Components

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `Dockerfile` | Image definition | Multi-step build: base image selection, apt mirror redirect, package install, project copy, directory/permission setup, XSP4 launch |
| `docker-compose.yml` | Service orchestration | Port mapping, named volume mount, restart policy, build context reference |
| `legacy-credit-data` volume | SQLite persistence | Stores `legacy-credit-desk.db` across container restarts; destroyed only by `down -v` |

### Runtime Dependencies

| Dependency | Version | Role |
|------------|---------|------|
| `mono:6.12` | Base image | Provides the Mono CLR for .NET 4.x execution |
| `mono-xsp4` | Debian package | ASP.NET Web Forms HTTP host; replaces IIS in Linux container |
| `sqlite3` | Debian package | CLI client used by `verify-docker.sh` for database assertions |
| `Mono.Data.Sqlite.dll` | Part of Mono 4.5 GAC | ADO.NET provider for SQLite consumed by `LegacyRepository.cs` |

---

## 4. APIs and Endpoints

### Exposed Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| GET | `http://localhost:8080/Default.aspx` | Operations dashboard | No |
| GET | `http://localhost:8080/Customers.aspx` | Customer credit file | No |
| GET/POST | `http://localhost:8080/OrderEntry.aspx` | Order entry and credit decision | No |
| GET/POST | `http://localhost:8080/Batch.aspx` | Batch status and manual trigger | No |

> No authentication or TLS is configured. The application is intended for local development and testing environments only.

**Container Port Mapping**:
```
Host: 0.0.0.0:8080  →  Container: 0.0.0.0:8080
```

---

## 5. Data Flows

### Main Flow: Container Build and First Start

```
Developer → docker compose up --build
    → Dockerfile executes (layers: base → apt → copy → setup → CMD)
    → Image tagged and stored locally
    → Container started; volume legacy-credit-data mounted at /app/App_Data
    → xsp4 process starts on port 8080
    → First HTTP request → LegacyRepository.EnsureDatabase() → legacy-credit-desk.db created
    → Application ready
```

**Steps**:
1. `docker compose up --build` triggers image build from `Dockerfile`
2. apt sources are patched and packages installed; project files are copied
3. Directory structure and permissions are prepared; XSP4 CMD is set
4. Compose starts the container, maps port 8080, and attaches the named volume
5. On first HTTP request, `LegacyRepository.EnsureDatabase()` creates the SQLite schema and seed data

### Data Persistence Flow

```
Container restart (without -v) → Volume reattached → existing .db file used
Container reset (reset-data.sh) → docker compose down -v → volume destroyed
    → docker compose up --build → new container → EnsureDatabase() → fresh seed data
```

### Error Handling

| Error | Cause | Response |
|-------|-------|----------|
| `apt-get update` fails | Debian Buster EOL; original mirrors unreachable | Build fails; fix is the `archive.debian.org` sed redirect already in the Dockerfile |
| `App_Data` write error | Incorrect directory permissions | XSP4 cannot create SQLite file; `chmod 777` in RUN step prevents this at build time |
| Port 8080 conflict | Host port already in use | `docker compose up` fails with "address already in use"; operator must free the port |
| Missing `Mono.Data.Sqlite.dll` | DLL not in `/app/bin/` | Runtime `InvalidOperationException` from `DbProviderFactory`; RUN step copies DLL during build |
| Volume not mounted | Compose file missing volume definition | SQLite file lost on container restart; named volume definition in `docker-compose.yml` prevents this |

---

## 6. Integrations

### Internal
- **Application Bootstrap and Global Lifecycle**: XSP4 (started by CMD) hosts `Global.asax`; `Application_Start` fires on first request and calls `LegacyRepository.EnsureDatabase()`
- **Data Access and Persistence Layer**: `LegacyRepository.cs` uses `Mono.Data.Sqlite.dll` (provisioned by Dockerfile) and writes to `/app/App_Data/legacy-credit-desk.db` (persisted by named volume)
- **ASP.NET Runtime Configuration**: `web.config` is copied into the image as part of `COPY . /app`; XSP4 reads it at startup
- **Local Development Startup Script** (`run-local.sh`): Made executable by `chmod +x /app/scripts/*.sh` in the RUN step
- **Smoke Test Script** (`verify-docker.sh`): Depends on the image built by this submodule; calls `docker compose build --no-cache` and `docker compose up -d`
- **Data Reset Script** (`reset-data.sh`): Calls `docker compose down -v` and `docker compose up --build`; both commands reference the Compose configuration defined here

### External
- **Docker Engine**: Image build, container runtime, and volume management — no authentication beyond Docker socket access
- **Debian archive.debian.org**: Package mirror used during `docker build`; requires internet access at build time

---

## 7. Configuration

| Property | Purpose | Value |
|----------|---------|-------|
| Base image | Mono CLR version for .NET 4.x compatibility | `mono:6.12` |
| apt mirror | Override for EOL Debian Buster sources | `archive.debian.org` |
| `WORKDIR` | Container working directory | `/app` |
| `App_Data` permissions | Allow XSP4 process to write SQLite file | `777` |
| Exposed port | HTTP port XSP4 listens on | `8080` |
| Host port mapping | Port published to the host | `8080:8080` |
| Named volume | Docker volume for SQLite persistence | `legacy-credit-data` → `/app/App_Data` |
| Restart policy | Container auto-restart behavior | `unless-stopped` |
| `Mono.Data.Sqlite.dll` source | Path inside the `mono:6.12` image | `/usr/lib/mono/4.5/Mono.Data.Sqlite.dll` |

---

## 8. Summary

- **Components**: 2 configuration files (`Dockerfile`, `docker-compose.yml`), 1 Docker named volume (`legacy-credit-data`)
- **Endpoints**: 4 HTTP endpoints on port 8080 (all ASP.NET Web Forms pages)
- **Business Rules**: 4 (apt mirror redirect, App_Data permissions, DLL provisioning, port 8080 only)
- **Integrations**: 7 internal (Bootstrap, Data Access, Runtime Config, run-local.sh, verify-docker.sh, reset-data.sh, web.config), 2 external (Docker Engine, archive.debian.org)

---
**Note**: Document maintained under 1500 lines. Prioritizes essential functional information.
