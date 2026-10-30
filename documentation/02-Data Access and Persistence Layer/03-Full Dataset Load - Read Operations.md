# Functional Specification: Full Dataset Load - Read Operations

**Parent Module**: Data Access and Persistence Layer | **Date**: 2026-08-10

## 1. Executive Summary
- **Purpose**: Loads the complete database content into an in-memory `DataSet` in a single, thread-safe operation, providing the shared working copy used by all application layers for read and write operations.
- **Key Functionality**:
  - Acquires a global process lock before any I/O to prevent concurrent reads
  - Delegates schema construction to `BuildSchema()` to obtain a typed, empty `DataSet`
  - Reads all rows from `Customers`, `Orders`, and `Audit` tables via explicit SELECT queries
  - Performs type-aware value coercion (ISO-8601 dates, integer-backed booleans, invariant-culture decimals)
  - Calls `AcceptChanges()` so all loaded rows start as `Unchanged`, enabling `RowState`-based change tracking
- **Users**: Internal application components — Web UI code-behinds (`Default.aspx.cs`, `Customers.aspx.cs`, `OrderEntry.aspx.cs`, `Batch.aspx.cs`) and the `NightlyBatch` background processor.

---

## 2. Business Logic

### Key Processes

**Full Dataset Load (`Load()`)**: Provides every application consumer with a unified, consistent snapshot of the entire database.
1. Acquire `LegacyState.SyncRoot` lock (exclusive, process-scoped)
2. Call `EnsureDatabase()` — guarantees schema and seed data exist before reading
3. Call `BuildSchema()` — constructs a typed `DataSet` with `Customers`, `Orders`, and `Audit` `DataTable` definitions
4. Open a single `SqliteConnection` and call `FillTable()` three times (one per table)
5. Call `ds.AcceptChanges()` — marks all loaded rows as `Unchanged`; return the `DataSet` to the caller

**Row Fill and Type Coercion (`FillTable()`)**: Reads raw SQLite values and maps them to the typed .NET columns of each `DataTable`.
1. Execute the SQL SELECT statement against the open connection
2. Iterate the `SqliteDataReader` row by row
3. For each column, detect the target .NET type and apply the appropriate conversion:
   - `DateTime` → `DateTime.Parse` with `DateTimeStyles.RoundtripKind` (ISO-8601 `"o"` format)
   - `bool` → `Convert.ToInt32(v) != 0` (SQLite stores booleans as integers 0/1)
   - `decimal` → `Convert.ToDecimal` with `CultureInfo.InvariantCulture`
   - Other types → `Convert.ChangeType` with `InvariantCulture`
   - `DBNull` → preserved as `DBNull.Value`
4. Add the converted `DataRow` to the target `DataTable`

### Critical Validations

| Field | Validation Rule | Behaviour on Failure |
|-------|-----------------|----------------------|
| `DateTime` columns | Parsed using ISO-8601 round-trip format with `RoundtripKind` flag | `FormatException` propagates; load aborts |
| `bool` columns (`Blocked`, `ManualOverride`) | Must be stored as integer 0 or 1 in SQLite | Incorrect integer coerced; non-integer raises `InvalidCastException` |
| `decimal` columns (`CreditLimit`, `OutstandingDebt`, `Amount`) | Parsed with `InvariantCulture` to avoid locale-dependent decimal separators | Locale mismatch raises `FormatException` |
| `DBNull` values | Explicitly preserved as `DBNull.Value`; no silent substitution | Caller must handle nulls in downstream logic |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `LegacyRepository.Load()` (line 44) | Top-level entry point for the full read operation | Acquires lock, calls `EnsureDatabase`, orchestrates `BuildSchema` and `FillTable`, calls `AcceptChanges`, returns `DataSet` |
| `LegacyRepository.FillTable()` (line 135) | Generic table reader with type coercion | Executes SELECT, iterates reader, converts each column value to the typed .NET equivalent, adds rows to `DataTable` |
| `LegacyRepository.BuildSchema()` (line 333) | Typed `DataSet` constructor | Creates `DataSet("LegacyCreditDesk")` with `Customers` (10 columns, `Id` PK), `Orders` (9 columns), `Audit` (5 columns) |
| `LegacyRepository.OpenConnection()` (line 123) | Managed connection factory | Opens `SqliteConnection`, applies `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;` |
| `LegacyRepository.EnsureDatabase()` (line 24) | Pre-condition guard | Called defensively before every data operation to guarantee schema and seed data exist |

### Frontend

| Component | Purpose | Primary Functionality |
|-----------|---------|----------------------|
| `Default.aspx.cs` | Operations dashboard | Calls `Load()` to display KPI counts and recent orders/audit entries (read-only) |
| `Customers.aspx.cs` | Customer credit file | Calls `Load()` to list all customer credit profiles; uses the returned `DataSet` as the basis for the manual override toggle write path |
| `OrderEntry.aspx.cs` | Order creation | Calls `Load()` to retrieve customer data before invoking `CreditDecisionService.Evaluate()` and persisting new orders |
| `Batch.aspx.cs` | Batch monitor | Calls `Load()` to display batch execution history (read-only) |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `Customers` | Credit profiles for all customers | `Id` (PK, TEXT), `Name`, `Country`, `Since` (DateTime), `CreditLimit` (decimal), `OutstandingDebt` (decimal), `OverdueDays` (int), `RiskScore` (int), `Blocked` (bool), `ManualOverride` (bool) |
| `Orders` | Credit order records | `Id` (PK, TEXT), `CustomerId` (FK), `Amount` (decimal), `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` (DateTime) |
| `Audit` | Immutable audit trail | `Id` (PK, TEXT), `At` (DateTime), `Type`, `Subject`, `Detail` |

---

## 4. APIs and Endpoints

This submodule has no HTTP endpoints. It is an internal data-access layer invoked programmatically.

### Internal Method Signatures

| Method | Signature | Purpose |
|--------|-----------|---------|
| `Load()` | `public static DataSet Load()` | Returns fully populated, change-tracked `DataSet` |
| `FillTable()` | `private static void FillTable(SqliteConnection cn, DataTable table, string sql)` | Fills a single `DataTable` from a SQL SELECT |

**SQL Queries Executed by `Load()`**:

```sql
-- Customers (ordered by Id)
SELECT Id, Name, Country, Since, CreditLimit, OutstandingDebt, OverdueDays, RiskScore, Blocked, ManualOverride
FROM Customers ORDER BY Id

-- Orders (ordered by creation time)
SELECT Id, CustomerId, Amount, PaymentMethod, Priority, Status, DecisionCode, DecisionText, CreatedAt
FROM Orders ORDER BY CreatedAt

-- Audit (ordered by timestamp)
SELECT Id, At, Type, Subject, Detail
FROM Audit ORDER BY At
```

**Return Value Example**:
```
DataSet "LegacyCreditDesk"
  ├── DataTable "Customers"  — N rows, RowState = Unchanged
  ├── DataTable "Orders"     — M rows, RowState = Unchanged
  └── DataTable "Audit"      — P rows, RowState = Unchanged
```

---

## 5. Data Flows

### Main Flow: Full Dataset Load

```
Caller (Web UI / NightlyBatch)
    │
    ▼ LegacyRepository.Load()
    │
    ├─► Acquire LegacyState.SyncRoot lock
    ├─► EnsureDatabase() — schema + seed guard
    ├─► BuildSchema()    — typed empty DataSet
    ├─► OpenConnection() — single SQLite connection
    │       ├─► FillTable(Customers)
    │       ├─► FillTable(Orders)
    │       └─► FillTable(Audit)
    ├─► ds.AcceptChanges() — all rows → Unchanged
    └─► return DataSet
    │
    ▼ Caller receives DataSet
      (mutates rows → calls Save(ds) to persist changes)
```

**Steps Summary**:
1. Caller invokes `Load()`; global lock acquired — no concurrent database access possible
2. `EnsureDatabase()` verifies schema DDL has run and seed data exists
3. `BuildSchema()` produces a typed in-memory skeleton with correct .NET column types
4. A single connection services all three `FillTable()` calls sequentially
5. `FillTable()` coerces each SQLite value to its typed .NET equivalent before adding the row
6. `AcceptChanges()` resets all `RowState` flags to `Unchanged`; the caller's mutations are now trackable

### Error Handling

| Error | Cause | Behaviour |
|-------|-------|-----------|
| `FormatException` on DateTime | Stored value is not valid ISO-8601 `"o"` format | Exception propagates; `Load()` aborts; lock released via `lock` block |
| `InvalidCastException` | Boolean column contains non-integer value in SQLite | Exception propagates; load aborts |
| `SqliteException` (busy) | Another process holds the database file lock beyond 5 seconds | `PRAGMA busy_timeout=5000` causes SQLite to wait up to 5 s before raising; exception propagates on timeout |
| `IOException` | `App_Data/` directory not accessible | Raised inside `EnsureDatabase()` before any reads; propagates to caller |
| Thread contention | Multiple threads call `Load()` simultaneously | Second caller blocks on `LegacyState.SyncRoot`; proceeds sequentially after first completes |

---

## 6. Integrations

### Internal
- **Global In-Memory State (`LegacyState`)**: Provides `SyncRoot` — the single process-wide lock object used to serialize all `Load()` calls and prevent concurrent read/write races.
- **Database Initialization and Schema Management (`EnsureDatabase`)**: Called defensively at the start of every `Load()` invocation to guarantee the SQLite file, schema DDL, and seed data are present before any SELECT is issued.
- **DataSet Builder and In-Memory Schema (`BuildSchema`)**: Constructs the typed `DataSet` skeleton consumed by `FillTable()`. Column type definitions in `BuildSchema` must exactly match the SQLite schema to ensure correct coercion in `FillTable`.
- **Transactional Persist - Write Operations (`Save`)**: Receives the same `DataSet` returned by `Load()` after the caller has mutated rows. `AcceptChanges()` in `Load()` is a prerequisite for `RowState`-based change detection in `Save()`.
- **Credit Decision Engine (`CreditDecisionService`)**: Indirectly consumes the `DataSet` through `OrderEntry.aspx.cs`; also triggers two direct SQL aggregate calls (`SumOpenOrders`, `CountRecentOrders`) that bypass the loaded `DataSet`.
- **Nightly Batch (`NightlyBatch`)**: Calls `Load()` at the start of every batch tick to obtain a fresh snapshot of all customers before recalculating risk scores.

### External
- None. All data access is against the embedded SQLite file (`App_Data/legacy-credit-desk.db`) via `Mono.Data.Sqlite`.

---

## 7. Configuration

| Property | Purpose | Default / Source |
|----------|---------|-----------------|
| `DbPath` | Absolute filesystem path to the SQLite database file | `{HttpRuntime.AppDomainAppPath}/App_Data/legacy-credit-desk.db` |
| `ConnectionString` | SQLite URI connection string | `URI=file:{DbPath};Version=3` |
| `PRAGMA foreign_keys=ON` | Enforces referential integrity on every connection | Applied in `OpenConnection()` — always ON, not configurable |
| `PRAGMA busy_timeout=5000` | Maximum milliseconds to wait when the database file is locked | 5000 ms, hardcoded in `OpenConnection()` |
| `DateTimeStyles.RoundtripKind` | Timezone-safe ISO-8601 parsing mode | Hardcoded in `FillTable()` |
| `CultureInfo.InvariantCulture` | Locale-independent decimal and date parsing | Hardcoded in `FillTable()` |

---

## 8. Summary
- **Components**: 5 backend, 4 frontend (consumers), 3 data entities
- **Endpoints**: 0 HTTP — internal method `LegacyRepository.Load()` only
- **Business Rules**: 4 type-coercion rules (DateTime ISO-8601, bool from int, decimal invariant-culture, DBNull preservation); 1 locking rule (global `SyncRoot` serialization)
- **Integrations**: 5 internal (LegacyState, EnsureDatabase, BuildSchema, Save, NightlyBatch/CreditDecisionService callers), 0 external

---
**Note**: Document kept under 1500 lines. Priority given to essential functional information.
