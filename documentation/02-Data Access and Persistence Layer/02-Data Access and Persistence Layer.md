# Submodules of Module: Data Access and Persistence Layer

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 3
**Analysis Date**: 2026-08-10
**Parent Module**: Data Access and Persistence Layer

## Summary
Total submodules identified: **6**

### Distribution by Type
- Backend Components: 6
- Frontend Components: 0
- Data Layer: 6 (all submodules touch the database)
- API/Services: 0
- Configuration: 0
- Utilities: 1

## Submodule Index
1. Database Initialization and Schema Management - Backend Component / Data Layer
2. DataSet Builder and In-Memory Schema - Backend Component / Data Layer
3. Full Dataset Load - Read Operations - Backend Component / Data Layer
4. Transactional Persist - Write Operations - Backend Component / Data Layer
5. Direct SQL Query Aggregates - Backend Component / Data Layer
6. Seed Data and Database Bootstrap - Backend Component / Utility

---

## Submodule Detail

### Database Initialization and Schema Management

**Type**: Backend Component / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `EnsureDatabase()` (lines 24–42), `SchemaSql` constant (lines 352–379), `OpenConnection()` (lines 123–133)

**Purpose**: Creates the SQLite database file on first run, executes the full DDL (`CREATE TABLE IF NOT EXISTS`, indexes, views, trigger) via `SchemaSql`, and enables foreign-key enforcement and a 5-second busy timeout on every connection. Guards the entire operation under `LegacyState.SyncRoot` to prevent concurrent initialization races.

**Key Files**:
1. `LegacyRepository.cs` — `EnsureDatabase()` — entry point called by `Global.asax` on application start and defensively before every data operation
2. `LegacyRepository.cs` — `SchemaSql` — inline DDL string that defines tables `Customers`, `Orders`, `Audit`; indexes `IX_Orders_Customer_Status`, `IX_Orders_CreatedAt`; views `vw_CustomerExposure`, `vw_ManualReviewQueue`; trigger `trg_OrderBlockedAudit`
3. `LegacyRepository.cs` — `OpenConnection()` — opens a `SqliteConnection` and applies `PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;`
4. `LegacyRepository.cs` — `DbPath` / `ConnectionString` — resolve the physical database file path relative to `HttpRuntime.AppDomainAppPath`
5. `App_Data/` — runtime directory that holds `legacy-credit-desk.db` (empty at design time; created on first access)

**Dependencies**:
- **External Modules**: Global In-Memory State (`LegacyState.SyncRoot` for thread safety)
- **Internal Submodules**: Seed Data and Database Bootstrap (called from `EnsureDatabase` when the Customers table is empty)
- **Libraries**: `Mono.Data.Sqlite`, `System.IO`, `System.Web.HttpRuntime`

**Related Components**:
- **Entities**: `Customers`, `Orders`, `Audit` tables; `vw_CustomerExposure`, `vw_ManualReviewQueue` views; `trg_OrderBlockedAudit` trigger
- **Services**: Called by `Global.asax` (Application Bootstrap module) on `Application_Start`
- **APIs**: None
- **UI**: None

**Integration Points**:
- `Global.asax.Application_Start` → `LegacyRepository.EnsureDatabase()` — guarantees schema exists before any page is served
- Every public `LegacyRepository` method calls `EnsureDatabase()` defensively before opening connections

---

### DataSet Builder and In-Memory Schema

**Type**: Backend Component / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `BuildSchema()` (lines 333–348)

**Purpose**: Constructs a typed, empty `DataSet` whose table and column definitions exactly mirror the SQLite schema. The resulting `DataSet` is used as the in-memory working copy loaded by the read path and mutated by all upper layers before being flushed back to SQLite.

**Key Files**:
1. `LegacyRepository.cs` — `BuildSchema()` — creates `DataSet("LegacyCreditDesk")` with typed `DataTable` objects for `Customers` (10 columns, `Id` as primary key), `Orders` (9 columns), and `Audit` (5 columns)

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Full Dataset Load — Read Operations (consumes the returned `DataSet`)
- **Libraries**: `System.Data`

**Related Components**:
- **Entities**: Mirrors `Customers`, `Orders`, `Audit` table schemas
- **Services**: Returned `DataSet` is shared across Credit Decision Engine, Nightly Batch, and Web UI code-behinds
- **APIs**: None
- **UI**: None

**Integration Points**:
- `Load()` calls `BuildSchema()` and then fills its tables via `FillTable()`
- Callers receive a single `DataSet` reference that acts as the in-process unit-of-work object

---

### Full Dataset Load - Read Operations

**Type**: Backend Component / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `Load()` (lines 44–59), `FillTable()` (lines 135–161)

**Purpose**: Loads the entire database content into an in-memory `DataSet` in a single locked operation. Reads `Customers`, `Orders`, and `Audit` tables in full via explicit SELECT statements, performs type-aware value conversion (dates, booleans, decimals), calls `AcceptChanges()` so all rows start as `Unchanged`, and returns the fully populated `DataSet`.

**Key Files**:
1. `LegacyRepository.cs` — `Load()` — top-level entry point; acquires `LegacyState.SyncRoot` lock, calls `BuildSchema()`, opens one connection for all three `FillTable()` calls, then `AcceptChanges()`
2. `LegacyRepository.cs` — `FillTable()` — generic fill loop; executes a SQL string, iterates the reader, performs per-column type coercion (`DateTime` ISO round-trip parse, `bool` from integer, `decimal` invariant-culture), and adds rows to the target `DataTable`

**Dependencies**:
- **External Modules**: Global In-Memory State (`LegacyState.SyncRoot`)
- **Internal Submodules**: Database Initialization and Schema Management (`EnsureDatabase()`), DataSet Builder and In-Memory Schema (`BuildSchema()`)
- **Libraries**: `Mono.Data.Sqlite`, `System.Data`, `System.Globalization`

**Related Components**:
- **Entities**: `Customers`, `Orders`, `Audit` tables
- **Services**: Called by `NightlyBatch`, `CreditDecisionService` (indirectly via callers), and all Web Forms code-behinds that need the full state
- **APIs**: None
- **UI**: None

**Integration Points**:
- Web UI code-behinds (`Default.aspx.cs`, `Customers.aspx.cs`, `OrderEntry.aspx.cs`, `Batch.aspx.cs`) call `LegacyRepository.Load()` to obtain the working `DataSet`
- `NightlyBatch` calls `Load()` at the start of each batch tick

---

### Transactional Persist - Write Operations

**Type**: Backend Component / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `Save()` (lines 61–76), `SaveCustomers()` (lines 163–191), `SaveOrders()` (lines 207–235), `SaveAudit()` (lines 250–269), `AddCustomerParameters()` (lines 193–205), `AddOrderParameters()` (lines 237–248), `AddAudit()` (lines 112–121)

**Purpose**: Flushes all pending `DataSet` changes back to SQLite inside a single transaction. Iterates each `DataTable` row by `RowState`, issuing `INSERT`, `UPDATE`, or `DELETE` commands as appropriate. Audit rows are append-only (`INSERT OR IGNORE`; `Deleted` state is ignored). Calls `AcceptChanges()` on commit. Also provides `AddAudit()` for appending new audit rows to the in-memory `DataSet` before saving.

**Key Files**:
1. `LegacyRepository.cs` — `Save(DataSet)` — public entry point; opens connection, begins transaction, delegates to the three private Save* helpers, commits, calls `ds.AcceptChanges()`
2. `LegacyRepository.cs` — `SaveCustomers()` — handles `Deleted`, `Added`, and `Modified` rows for the `Customers` table; reuses `AddCustomerParameters()` for parameter binding
3. `LegacyRepository.cs` — `SaveOrders()` — handles `Deleted`, `Added`, and `Modified` rows for the `Orders` table; reuses `AddOrderParameters()`
4. `LegacyRepository.cs` — `SaveAudit()` — append-only INSERT for `Added` rows in the `Audit` table; uses `INSERT OR IGNORE` to tolerate duplicate IDs
5. `LegacyRepository.cs` — `AddAudit()` — helper that creates a new `Audit` `DataRow` with a new GUID, current timestamp, and the supplied type/subject/detail, and appends it to `ds.Tables["Audit"]`

**Dependencies**:
- **External Modules**: Global In-Memory State (`LegacyState.SyncRoot`)
- **Internal Submodules**: Database Initialization and Schema Management (`EnsureDatabase()`)
- **Libraries**: `Mono.Data.Sqlite`, `System.Data`, `System.Globalization`

**Related Components**:
- **Entities**: `Customers`, `Orders`, `Audit` tables
- **Services**: Called by `NightlyBatch.RunBatch()`, `Customers.aspx.cs` (manual override toggle), `OrderEntry.aspx.cs` (new order + decision persistence)
- **APIs**: None
- **UI**: None

**Integration Points**:
- All write paths in the application converge on `LegacyRepository.Save(ds)` after mutating the in-memory `DataSet`
- `AddAudit()` is used by every caller that needs to record a domain event before the `Save()` call

---

### Direct SQL Query Aggregates

**Type**: Backend Component / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `SumOpenOrders()` (lines 86–96), `CountRecentOrders()` (lines 98–110), `ScalarInt()` (lines 271–279), `FindCustomer()` (lines 78–82)

**Purpose**: Provides calculated aggregates by querying the SQLite database directly, intentionally bypassing the loaded `DataSet`. `SumOpenOrders` returns the total non-blocked order amount for a customer; `CountRecentOrders` returns the number of orders within a rolling date window. This mixed-access pattern (DataSet + direct SQL) deliberately simulates realistic legacy data access debt and is a documented migration challenge. `FindCustomer` and `ScalarInt` are supporting helpers for in-memory lookups and scalar queries.

**Key Files**:
1. `LegacyRepository.cs` — `SumOpenOrders(DataSet, string)` — executes `SELECT COALESCE(SUM(Amount),0) FROM Orders WHERE CustomerId=@id AND Status<>'BLOCKED'`; uses parameterised query; ignores the passed `DataSet`
2. `LegacyRepository.cs` — `CountRecentOrders(DataSet, string, int)` — executes `SELECT COUNT(*) FROM Orders WHERE CustomerId=@id AND CreatedAt>=@edge`; computes rolling window from `DateTime.Now.AddDays(-days)`
3. `LegacyRepository.cs` — `ScalarInt(string)` — generic helper that opens a connection and returns `int` from `ExecuteScalar`; used internally by `EnsureDatabase`
4. `LegacyRepository.cs` — `FindCustomer(DataSet, string)` — queries the in-memory `DataSet` (not the DB) using `DataTable.Select()` with SQL-escaped predicate; returns the matching `DataRow` or null

**Dependencies**:
- **External Modules**: Credit Decision Engine (`CreditDecisionService` calls `SumOpenOrders` and `CountRecentOrders`)
- **Internal Submodules**: Database Initialization and Schema Management (`EnsureDatabase()`)
- **Libraries**: `Mono.Data.Sqlite`, `System.Data`, `System.Globalization`

**Related Components**:
- **Entities**: `Orders` table (aggregates), `Customers` DataTable (in-memory lookup)
- **Services**: `CreditDecisionService.Evaluate()` uses both aggregate methods to assess open exposure and order frequency
- **APIs**: None
- **UI**: None

**Integration Points**:
- `CreditDecisionService` calls `SumOpenOrders` and `CountRecentOrders` during credit evaluation — these are the only aggregate queries in the system
- `FindCustomer` is used by Web UI code-behinds to locate a specific customer row in the loaded `DataSet`

---

### Seed Data and Database Bootstrap

**Type**: Utility / Data Layer

**Location**:
- **Backend**: `App_Code/LegacyRepository.cs` — `SeedDatabase()` (lines 281–303), `InsertCustomer()` (lines 305–315), `InsertOrder()` (lines 317–326)

**Purpose**: Populates the database with a fixed set of five reference customers and two seed orders when the database is first created or found empty. All inserts use `INSERT OR IGNORE` to guarantee idempotency. Also writes a `SYSTEM/BOOTSTRAP` audit record. Provides the deterministic initial state required by migration tests and smoke tests.

**Key Files**:
1. `LegacyRepository.cs` — `SeedDatabase()` — orchestrates seed within a single transaction; inserts C001–C005 (covering ES/PT/IT countries, varied risk/debt profiles including one hard-blocked customer C003) and two seed orders
2. `LegacyRepository.cs` — `InsertCustomer()` — parameterised `INSERT OR IGNORE INTO Customers` helper used exclusively by `SeedDatabase`
3. `LegacyRepository.cs` — `InsertOrder()` — parameterised `INSERT OR IGNORE INTO Orders` helper used exclusively by `SeedDatabase`

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Database Initialization and Schema Management (must have run `SchemaSql` before seed inserts)
- **Libraries**: `Mono.Data.Sqlite`, `System`

**Related Components**:
- **Entities**: `Customers`, `Orders`, `Audit` tables
- **Services**: Invoked only from `EnsureDatabase()` when `COUNT(*) FROM Customers == 0`
- **APIs**: None
- **UI**: None

**Integration Points**:
- `scripts/reset-data.sh` deletes the database file; the next request triggers `EnsureDatabase()` → `SeedDatabase()` to restore the known-good initial state
- Migration test objectives (`docs/migration-test-objectives.md`) rely on the five seed customers having specific attribute values (e.g. C003 blocked with 118 overdue days, C002 with 42 overdue days at risk 61)

---

## Relationship Map

### Data Flow

```text
[Web UI / NightlyBatch]
        │
        ▼
LegacyRepository.Load()          ← Full read into DataSet (FillTable)
        │
        ▼
[In-Memory DataSet]              ← Shared working copy
        │
        ├── mutated by callers (new Orders, updated Customers, new Audit rows)
        │
        ▼
LegacyRepository.Save(DataSet)   ← Transactional flush (SaveCustomers/Orders/Audit)
        │
        ▼
[SQLite: legacy-credit-desk.db]

[CreditDecisionService]
        │
        ├── SumOpenOrders()      ← Direct SQL bypass (Orders table)
        └── CountRecentOrders()  ← Direct SQL bypass (Orders table)
```

### Call Hierarchy
- `Global.asax.Application_Start` → `EnsureDatabase()` → `SeedDatabase()` (first run only)
- `NightlyBatch.RunBatch()` → `Load()` → `Save()` → `AddAudit()`
- `OrderEntry.aspx.cs` → `Load()` → `FindCustomer()` → `CreditDecisionService.Evaluate()` [which calls `SumOpenOrders()`, `CountRecentOrders()`] → `Save()`
- `Customers.aspx.cs` → `Load()` → `FindCustomer()` → mutate row → `AddAudit()` → `Save()`
- `Default.aspx.cs` / `Batch.aspx.cs` → `Load()` (read-only display)

### UI-Backend Mapping
| UI Component | Backend Method | DB Table / View |
|---|---|---|
| `Default.aspx` — dashboard KPIs and recent orders | `Load()` | `Customers`, `Orders`, `Audit` |
| `Customers.aspx` — credit profile list and override toggle | `Load()`, `FindCustomer()`, `Save()`, `AddAudit()` | `Customers`, `Audit` |
| `OrderEntry.aspx` — order creation + credit decision | `Load()`, `FindCustomer()`, `SumOpenOrders()`, `CountRecentOrders()`, `Save()`, `AddAudit()` | `Orders`, `Customers`, `Audit` |
| `Batch.aspx` — batch status and manual trigger | `Load()` (read), `Save()` (via NightlyBatch) | `Customers`, `Audit` |
| `NightlyBatch` (timer) | `Load()`, `Save()`, `AddAudit()` | `Customers`, `Audit` |

---

## Completeness Verification

### File Statistics
- Total backend files in `App_Code/`: 4 — Documented from this module: 1 (`LegacyRepository.cs`)
- Total frontend files: 0
- Total JavaScript files: 0
- Total CSS files: 0
- Total SQL scripts: 0 (DDL is embedded inline in `LegacyRepository.cs` as `SchemaSql`)
- Total runtime data files: 1 (`App_Data/legacy-credit-desk.db` — created at runtime, not a design-time artifact)
- **Coverage**: 100% of files belonging to this module

### Directories Analyzed
- [x] `App_Code/LegacyRepository.cs` — 1 file, all 380 lines fully mapped across 6 submodules
- [x] `App_Data/` — 1 `.gitkeep` placeholder; runtime database file created by `EnsureDatabase()`

---

## Technical Notes
- **Mixed access pattern**: `SumOpenOrders` and `CountRecentOrders` open fresh database connections even though a fully loaded `DataSet` is already in memory. This is intentional legacy debt explicitly noted in the source comment at line 84 and is a primary migration challenge.
- **Single synchronisation lock**: All public methods (including direct SQL queries) acquire `LegacyState.SyncRoot`. This guarantees consistency but serialises all database I/O application-wide.
- **No ORM**: All SQL is hand-written. The `DataSet` acts as a manual unit-of-work; `RowState` change tracking drives the save logic.
- **Audit trigger**: `trg_OrderBlockedAudit` fires at the database level for every blocked order insert, creating an audit row independently of application-layer `AddAudit()` calls. Migration tooling must account for both audit sources.
- **ISO-8601 date storage**: All `DateTime` values are stored as ISO-8601 round-trip strings (`"o"` format) and parsed back with `DateTimeStyles.RoundtripKind` to avoid timezone ambiguity.

---

## Complexity and Prioritisation

### Complexity by Submodule
- **High**: Transactional Persist - Write Operations — three-table transactional flush with RowState branching; most error-prone path during migration
- **High**: Full Dataset Load - Read Operations — type coercion across all three tables; datetime and bool mapping must be replicated exactly in a migration
- **Medium**: Database Initialization and Schema Management — inline DDL with views and trigger; schema must be reproduced faithfully in target database
- **Medium**: Direct SQL Query Aggregates — intentional mixed-access debt; must be rationalised during migration (replace with ORM queries or repository methods using the already-loaded DataSet)
- **Low**: DataSet Builder and In-Memory Schema — pure in-memory structure; straightforward to replace with typed entity classes
- **Low**: Seed Data and Database Bootstrap — fixed data set; trivially replaced with a migration script or fixture file

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analysed in full detail to produce the complete functional specification.
