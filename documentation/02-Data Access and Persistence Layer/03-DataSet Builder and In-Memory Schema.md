# Functional Specification: DataSet Builder and In-Memory Schema

**Parent Module**: Data Access and Persistence Layer | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Constructs a typed, empty `DataSet` whose table and column definitions exactly mirror the SQLite database schema. The resulting `DataSet` serves as the in-memory unit-of-work object used by all application layers to read, mutate, and persist data.
- **Key Functionality**:
  - Creates a named `DataSet("LegacyCreditDesk")` with three typed `DataTable` objects
  - Defines strongly-typed columns matching the SQLite schema (types, nullability, primary keys)
  - Establishes the `Customers.Id` primary key constraint in-memory
  - Acts as the structural contract between the database layer and all consumers
  - Is invoked exclusively by `Load()` before populating tables from SQLite
- **Users**: Internal component — not directly exposed to end users. Consumed by the Credit Decision Engine, Nightly Batch, and all Web Forms code-behinds via the `Load()` call chain.

---

## 2. Business Logic

### Key Processes

**Schema Construction**: Creates the in-memory data structure that mirrors the live database
1. Instantiate `DataSet` named `"LegacyCreditDesk"`
2. Define `Customers` table: 10 typed columns, set `Id` as primary key
3. Define `Orders` table: 9 typed columns (no in-memory FK constraint)
4. Define `Audit` table: 5 typed columns
5. Add all three tables to the `DataSet` and return it

**Integration with Load Cycle**: `BuildSchema()` is always called first within `Load()`, never standalone
1. `Load()` acquires `LegacyState.SyncRoot` lock
2. `BuildSchema()` constructs the empty typed structure
3. `FillTable()` populates each table from SQLite
4. `ds.AcceptChanges()` marks all rows as `Unchanged`
5. Caller receives the fully populated `DataSet`

### Critical Validations

| Field | Validation | Effect |
|-------|------------|--------|
| `Customers.Id` | Primary key set on `DataTable` | Prevents duplicate customer rows in memory |
| Column type mapping | Each column typed to match SQLite equivalents | Type coercion in `FillTable()` must match exactly |
| `DataSet` name | Fixed to `"LegacyCreditDesk"` | Downstream code references tables by string name |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `LegacyRepository.BuildSchema()` (lines 333–348) | Build typed empty `DataSet` | Define Customers/Orders/Audit table schemas; set primary key; return `DataSet` |
| `LegacyRepository.Load()` (lines 44–59) | Consume `BuildSchema()` output | Call `BuildSchema()`, fill tables via `FillTable()`, call `AcceptChanges()` |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| *(none)* | — | This submodule has no frontend components |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `Customers` DataTable | In-memory customer credit profiles | `Id` (PK, string), `Name`, `Country`, `Since` (DateTime), `CreditLimit` (decimal), `OutstandingDebt` (decimal), `OverdueDays` (int), `RiskScore` (int), `Blocked` (bool), `ManualOverride` (bool) |
| `Orders` DataTable | In-memory order records | `Id` (string), `CustomerId` (string), `Amount` (decimal), `PaymentMethod` (string), `Priority` (string), `Status` (string), `DecisionCode` (string), `DecisionText` (string), `CreatedAt` (DateTime) |
| `Audit` DataTable | In-memory audit trail | `Id` (string), `At` (DateTime), `Type` (string), `Subject` (string), `Detail` (string) |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| *(none)* | — | This submodule exposes no HTTP endpoints | — |

`BuildSchema()` is a private static method. It is called internally within `LegacyRepository.Load()` and is not accessible from outside the class.

**Internal Method Signature**:
```csharp
// Internal call (private)
private static DataSet BuildSchema()

// Called by:
public static DataSet Load()
```

---

## 5. Data Flows

### Main Flow: Schema Construction → Data Load

```
Load() caller (Web UI / NightlyBatch)
    → LegacyRepository.Load()          [acquires SyncRoot lock]
        → BuildSchema()                [creates empty typed DataSet]
        → FillTable(Customers SQL)     [populates Customers rows]
        → FillTable(Orders SQL)        [populates Orders rows]
        → FillTable(Audit SQL)         [populates Audit rows]
        → ds.AcceptChanges()           [marks all rows Unchanged]
    → returns DataSet to caller
```

**Steps Summary**:
1. Caller requests full data load via `LegacyRepository.Load()`
2. `BuildSchema()` provides the typed container with column definitions and primary key
3. `FillTable()` maps SQLite values to .NET types (DateTime ISO-8601, bool from int, decimal invariant-culture)
4. `AcceptChanges()` resets `RowState` to `Unchanged` so change tracking starts clean
5. Caller mutates rows; `RowState` transitions to `Added`/`Modified`/`Deleted` for `Save()` to process

### Error Handling

| Error | Cause | Response |
|-------|-------|----------|
| Type mismatch at fill time | Column type in `BuildSchema()` does not match SQLite data | `InvalidCastException` or `FormatException` thrown from `FillTable()` |
| Duplicate primary key | Two rows with same `Customers.Id` loaded from DB | `ConstraintException` from `DataTable.Rows.Add()` |
| `NullReferenceException` on table access | Caller uses string `"Customers"` / `"Orders"` / `"Audit"` and typos | Runtime exception — no guard; relies on exact string matching |

---

## 6. Integrations

### Internal

- **Full Dataset Load - Read Operations (`Load()`)**: Consumes `BuildSchema()` output — provides the typed empty structure that `FillTable()` then populates
- **Transactional Persist - Write Operations (`Save()`)**: Uses the same `DataSet` reference; reads `RowState` on each `DataTable` to determine INSERT/UPDATE/DELETE commands
- **Credit Decision Engine (`CreditDecisionService`)**: Receives the populated `DataSet` from callers; calls `FindCustomer()` to locate a row within the `Customers` DataTable defined by `BuildSchema()`
- **Nightly Batch (`NightlyBatch`)**: Calls `Load()` at the start of each batch tick; iterates `Customers` DataTable and mutates rows before calling `Save()`
- **Web UI code-behinds** (`Default.aspx.cs`, `Customers.aspx.cs`, `OrderEntry.aspx.cs`, `Batch.aspx.cs`): All obtain the `DataSet` via `Load()` and access tables by string name (`"Customers"`, `"Orders"`, `"Audit"`)

### External

*(none — this submodule has no external system dependencies)*

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `DataSet` name `"LegacyCreditDesk"` | Identifier for the in-process working dataset | Fixed constant in `BuildSchema()` |
| `Customers.PrimaryKey` | In-memory uniqueness constraint on `Id` | Set to `customers.Columns["Id"]` |
| Column types | .NET type for each column, must match SQLite schema | Hard-coded in `BuildSchema()` — no external configuration |

---

## 8. Summary

- **Components**: 1 backend (`BuildSchema()`), 0 frontend, 3 data entities (Customers, Orders, Audit DataTables)
- **Endpoints**: 0
- **Business rules**: 2 (primary key constraint on Customers.Id; strict column-type mapping matching SQLite schema)
- **Integrations**: 5 internal (Load, Save, CreditDecisionService, NightlyBatch, Web UI code-behinds), 0 external

---
**Note**: Document kept under 1500 lines. Focuses on essential functional information. `BuildSchema()` is a low-complexity submodule; its criticality lies in being the structural contract that all other submodules depend on — any column type drift between `BuildSchema()` and `SchemaSql` will cause runtime failures in `FillTable()`.
