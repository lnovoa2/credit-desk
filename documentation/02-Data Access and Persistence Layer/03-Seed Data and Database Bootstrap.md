# Functional Specification: Seed Data and Database Bootstrap

**Parent Module**: Data Access and Persistence Layer | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides the deterministic initial state of the SQLite database by inserting a fixed set of reference customers, seed orders, and a bootstrap audit record when the database is first created or found empty.
- **Key Functionality**:
  - Inserts five reference customers with varied risk profiles covering three countries (ES, PT, IT)
  - Inserts two seed orders with known decision outcomes (REVIEW, APPROVED)
  - Writes a `SYSTEM/BOOTSTRAP` audit record to mark the initialization event
  - Guarantees idempotency via `INSERT OR IGNORE` on all inserts
  - Executes entirely within a single SQLite transaction
- **Users**: No end users interact directly with this submodule. It is invoked automatically by the application bootstrap process (`Global.asax → Application_Start`). Operations teams may indirectly trigger a re-seed by running `scripts/reset-data.sh`. Migration and test engineers rely on the known-state fixture for regression testing.

---

## 2. Business Logic

### Key Processes

**Bootstrap Seed Process**: Populates an empty database with the reference dataset needed for the application to operate and for migration tests to execute against a known baseline.

1. `Global.asax.Application_Start` calls `LegacyRepository.EnsureDatabase()`
2. `EnsureDatabase` checks: `File.Exists(DbPath) == false` OR `COUNT(*) FROM Customers == 0`
3. If either condition is true, `SeedDatabase()` is called within the global `LegacyState.SyncRoot` lock
4. `SeedDatabase` opens a connection, begins a transaction, and executes all inserts via `InsertCustomer`, `InsertOrder`, and an inline audit `INSERT`
5. Transaction is committed; the database is now in a known, deterministic state

**Data Reset Process**: Allows restoring the initial state for testing or operational rollback.

1. Operator runs `scripts/reset-data.sh`
2. Script executes `docker compose down -v` (destroys the SQLite volume)
3. Script runs `docker compose up --build` — next application startup triggers `EnsureDatabase()` → `SeedDatabase()` automatically

### Critical Validations

| Field | Validation Rule | Behavior |
|-------|----------------|----------|
| All seed inserts | `INSERT OR IGNORE` | Silently skips if a record with the same primary key already exists |
| Seed trigger condition | `firstRun == true` OR `COUNT(*) FROM Customers == 0` | Seed runs if database file is new OR if table was emptied externally |
| Transaction atomicity | All inserts in a single `SqliteTransaction` | Partial seeds cannot occur; all-or-nothing commit |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `LegacyRepository.SeedDatabase()` (line 281) | Orchestrates the seed sequence | Opens connection + transaction; calls insert helpers; writes bootstrap audit row; commits |
| `LegacyRepository.InsertCustomer()` (line 305) | Inserts a single customer record | Parameterised `INSERT OR IGNORE INTO Customers`; maps all 10 fields including bool-to-int conversion |
| `LegacyRepository.InsertOrder()` (line 317) | Inserts a single order record | Parameterised `INSERT OR IGNORE INTO Orders`; maps all 9 fields including ISO-8601 date |
| `LegacyRepository.EnsureDatabase()` (line 24) | Guards and triggers seed | Checks existence/emptiness condition; calls `SeedDatabase()` if needed; protected by `LegacyState.SyncRoot` |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| *(none)* | This submodule has no frontend components | Seed is infrastructure-only; no UI exposes or controls it directly |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `Customers` | Stores customer credit profiles | `Id` (PK, TEXT), `Name`, `Country`, `Since`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked` (INTEGER 0/1), `ManualOverride` (INTEGER 0/1) |
| `Orders` | Stores credit orders with decisions | `Id` (PK, TEXT), `CustomerId` (FK), `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` (ISO-8601 TEXT) |
| `Audit` | Append-only event log | `Id` (PK, UUID), `At` (ISO-8601 TEXT), `Type` (`SYSTEM`), `Subject` (`BOOTSTRAP`), `Detail` (free text) |

---

## 4. APIs and Endpoints

### Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| *(none)* | — | Seed is triggered internally; no HTTP endpoint exposes it | — |

**Internal invocation**:
```
// Entry point (Global.asax)
LegacyRepository.EnsureDatabase();

// Condition that triggers SeedDatabase()
if (firstRun || ScalarInt("SELECT COUNT(*) FROM Customers") == 0)
    SeedDatabase();
```

**Seed transaction result** (rows written on first boot):

```
// Customers inserted (5 rows)
C001 - Ferretería Andévalo     | ES | CreditLimit 12000 | Debt 900   | Overdue 0   | Risk 18 | Blocked false
C002 - Comercial Atlántico     | PT | CreditLimit 9000  | Debt 6400  | Overdue 42  | Risk 61 | Blocked false
C003 - Suministros Mineros Sur | ES | CreditLimit 18000 | Debt 15100 | Overdue 118 | Risk 86 | Blocked true
C004 - Talleres Condado        | ES | CreditLimit 4500  | Debt 350   | Overdue 0   | Risk 39 | Blocked false
C005 - Distribuzioni Mare      | IT | CreditLimit 25000 | Debt 5900  | Overdue 12  | Risk 48 | Blocked false

// Orders inserted (2 rows)
ORD-SEED-01 | C002 | 1450 EUR | INVOICE   | NORMAL | REVIEW   | code R42 | CreatedAt T-8d
ORD-SEED-02 | C001 | 620 EUR  | TRANSFER  | NORMAL | APPROVED | code A00 | CreatedAt T-2d

// Audit record (1 row)
Type=SYSTEM | Subject=BOOTSTRAP | Detail='Embedded SQLite database created'
```

---

## 5. Data Flows

### Main Flow: First-Boot Database Initialization

```
Application Start → EnsureDatabase() → Schema DDL executed → COUNT(*) FROM Customers == 0
    → SeedDatabase() → BEGIN TRANSACTION
    → InsertCustomer x5 (INSERT OR IGNORE)
    → InsertOrder x2 (INSERT OR IGNORE)
    → INSERT Audit BOOTSTRAP row
    → COMMIT → Database in known state
```

**Steps**:
1. `Global.asax` fires `Application_Start` and calls `EnsureDatabase()` under `LegacyState.SyncRoot` lock
2. Schema DDL runs (`CREATE TABLE IF NOT EXISTS` — idempotent) and first-run / empty-table condition is evaluated
3. If seeding is needed, a transaction is opened; all customer and order rows are inserted with `INSERT OR IGNORE`
4. A `SYSTEM/BOOTSTRAP` audit entry is appended within the same transaction
5. Transaction commits; subsequent calls to `EnsureDatabase()` find `COUNT(*) > 0` and skip `SeedDatabase()`

### Data Reset Flow

```
Operator → reset-data.sh → docker compose down -v → volume destroyed
    → docker compose up --build → Application_Start → EnsureDatabase()
    → COUNT(*) == 0 → SeedDatabase() → known state restored
```

**Steps**:
1. `reset-data.sh` tears down the container and deletes the Docker volume holding `App_Data/`
2. Container rebuild brings up the application fresh
3. First HTTP request or application start triggers `EnsureDatabase()` → `SeedDatabase()` again

### Error Handling

| Error | Cause | Response |
|-------|-------|---------|
| Transaction failure during seed | Disk full, SQLite lock, or unexpected exception | Transaction rolls back automatically; database left empty; next request re-triggers `EnsureDatabase()` |
| Duplicate seed attempt (partial prior run) | `COUNT(*) == 0` check passed but some rows already exist | `INSERT OR IGNORE` silently skips existing rows; no error raised |
| `App_Data/` directory missing | First run on fresh container | `EnsureDatabase()` calls `Directory.CreateDirectory(dir)` before opening connection |
| Concurrent seed race | Two threads both see `COUNT(*) == 0` simultaneously | `LegacyState.SyncRoot` lock in `EnsureDatabase()` serialises access; only one thread executes the seed |

---

## 6. Integrations

### Internal

- **Database Initialization and Schema Management (`EnsureDatabase`)**: Prerequisite — DDL (`SchemaSql`) must have executed before `SeedDatabase()` is called; `SeedDatabase()` is invoked directly from `EnsureDatabase()` when the empty-database condition is detected
- **Global In-Memory State (`LegacyState.SyncRoot`)**: Concurrency guard — `EnsureDatabase()` acquires this application-wide lock before checking conditions and calling `SeedDatabase()`, preventing concurrent initialization races
- **Application Bootstrap and Global Lifecycle (`Global.asax`)**: Caller — `Application_Start` is the sole external trigger for `EnsureDatabase()` and therefore indirectly for `SeedDatabase()`
- **Nightly Batch Processing (`NightlyBatch`)**: Consumer — uses C002 and C003 seed customers as realistic targets for risk-score recalculation and auto-block logic (C003 already has 118 overdue days, above the 90-day threshold)
- **Credit Decision Engine (`CreditDecisionService`)**: Consumer — seed customers provide diverse attribute combinations (risk scores 18–86, debt ratios, country surcharge targets) that exercise all decision branches during smoke tests

### External

*(none — this submodule has no external system dependencies)*

---

## 7. Configuration

| Property | Purpose | Default / Value |
|----------|---------|-----------------|
| `DbPath` | Physical path of `legacy-credit-desk.db` | `{AppDomainAppPath}/App_Data/legacy-credit-desk.db` |
| `ConnectionString` | SQLite URI used by `OpenConnection()` | `URI=file:{DbPath};Version=3` |
| `PRAGMA busy_timeout` | Prevents immediate failure under concurrent access | `5000` ms (set in `OpenConnection()`) |
| `PRAGMA foreign_keys` | Enforces FK constraint on `Orders.CustomerId` | `ON` (set in `OpenConnection()`) |
| Seed condition | Logic that decides whether to call `SeedDatabase()` | `!File.Exists(DbPath) OR COUNT(*) FROM Customers == 0` |
| `scripts/reset-data.sh` | Operational script to wipe and re-seed | Executes `docker compose down -v && docker compose up --build` |

---

## 8. Summary

- **Components**: 4 backend (SeedDatabase, InsertCustomer, InsertOrder, EnsureDatabase), 0 frontend, 3 data entities (Customers, Orders, Audit)
- **Endpoints**: 0 (internal invocation only)
- **Business rules**: 4 (idempotency via INSERT OR IGNORE; atomic single-transaction seed; empty-table re-seed trigger; concurrency lock via SyncRoot)
- **Integrations**: 5 internal (Schema Management, Global State, Application Bootstrap, Nightly Batch, Credit Decision Engine), 0 external

---
*Note: Document kept under 1500 lines. Focused on essential functional information.*
