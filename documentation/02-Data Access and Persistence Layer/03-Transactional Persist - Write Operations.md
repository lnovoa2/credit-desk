# Functional Specification: Transactional Persist - Write Operations

**Parent Module**: Data Access and Persistence Layer | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Flushes all pending in-memory `DataSet` changes back to the SQLite database inside a single atomic transaction, keeping the persistent store consistent with the application's working copy after every mutating operation.
- **Key Functionality**:
  1. Wraps all write operations (Customers, Orders, Audit) in a single SQLite transaction — all succeed or all roll back.
  2. Detects changed rows via `DataRow.RowState` and routes each row to the correct SQL verb (INSERT / UPDATE / DELETE).
  3. Enforces append-only semantics on the Audit table (`INSERT OR IGNORE`; deletions are silently ignored).
  4. Provides `AddAudit()` helper so callers can append domain-event records to the in-memory `DataSet` before calling `Save()`.
  5. Calls `ds.AcceptChanges()` on successful commit, resetting all row states to `Unchanged`.
- **Users**: Internal — called programmatically by `NightlyBatch`, `Customers.aspx.cs`, and `OrderEntry.aspx.cs`. No direct user interaction.

---

## 2. Business Logic

### Key Processes

**Process 1 — Order Persistence (OrderEntry)**:
1. UI loads the current `DataSet` via `Load()`.
2. Amount and payment-method validations are performed (amount > 0; if INVOICE then amount >= 25 EUR).
3. `CreditDecisionService.Evaluate()` is called; the result populates a new `Orders` row.
4. `AddAudit()` appends an ORDER event row to the in-memory `DataSet`.
5. `Save(ds)` flushes the new order and audit row to SQLite in one transaction.

**Process 2 — Manual Override Toggle (Customers)**:
1. UI loads `DataSet`; `FindCustomer()` locates the target row.
2. `ManualOverride` boolean field is toggled on the in-memory row (`DataRowState` becomes `Modified`).
3. `AddAudit()` appends a USER event row.
4. `Save(ds)` flushes the modified customer and the new audit row.

**Process 3 — Nightly Batch Risk Recalculation**:
1. `NightlyBatch.RunNow()` loads the full `DataSet`.
2. Risk scores and `Blocked` flags are recalculated for every customer; affected rows become `Modified`.
3. `AddAudit()` appends a BATCH event row.
4. `Save(ds)` flushes all modified customer rows and the batch audit row.

### Critical Validations

| Field / Condition | Rule | Behaviour on Violation |
|---|---|---|
| `RowState == Unchanged` | Rows with no changes are skipped entirely | No SQL issued; silent skip |
| Audit `RowState == Deleted` | Deleted audit rows are not persisted | Silent ignore; no DELETE issued |
| Duplicate Audit `Id` | `INSERT OR IGNORE` tolerates collisions with DB-trigger-generated rows | Duplicate silently discarded |
| Transaction scope | All three table saves share one `SqliteTransaction` | Partial failure rolls back the entire flush |
| Concurrency | Entire `Save()` body is wrapped in `LegacyState.SyncRoot` lock | Serialises all writes application-wide |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|---|---|---|
| `LegacyRepository.Save(DataSet)` | Public entry point for all write flushes | Acquires lock, opens connection, starts transaction, delegates to Save* helpers, commits, calls `AcceptChanges()` |
| `SaveCustomers(cn, tx, DataTable)` | Persists Customers table changes | Iterates rows by `RowState`; issues DELETE / INSERT / UPDATE via `AddCustomerParameters()` |
| `SaveOrders(cn, tx, DataTable)` | Persists Orders table changes | Iterates rows by `RowState`; issues DELETE / INSERT / UPDATE via `AddOrderParameters()` |
| `SaveAudit(cn, tx, DataTable)` | Persists Audit table additions | Append-only; only `DataRowState.Added` rows are written; uses `INSERT OR IGNORE` |
| `AddCustomerParameters(cmd, row)` | Parameter binding helper for Customers | Maps all 10 customer fields to parameterised SQL; converts `bool` to 0/1, `DateTime` to ISO-8601 |
| `AddOrderParameters(cmd, row)` | Parameter binding helper for Orders | Maps all 9 order fields to parameterised SQL; converts `DateTime` to ISO-8601 |
| `AddAudit(DataSet, type, subject, detail)` | In-memory audit row factory | Creates a new `Audit` `DataRow` with a new GUID, current timestamp, and supplied event data; appends to `DataSet` |

### Frontend

No UI components belong to this submodule. The three web pages that trigger writes are documented under the **Web User Interface** module.

### Data

| Entity | Purpose | Key Fields |
|---|---|---|
| `Customers` | Stores customer credit profiles | `Id` (PK), `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride` |
| `Orders` | Stores credit order records and decisions | `Id` (PK), `CustomerId` (FK), `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Immutable domain-event log | `Id` (PK, GUID), `At` (ISO-8601 timestamp), `Type`, `Subject`, `Detail` |

---

## 4. APIs and Endpoints

This submodule is a pure backend persistence component. It exposes no HTTP endpoints. It is consumed by internal callers only.

### Internal Method Signatures

| Method | Caller | Purpose |
|---|---|---|
| `LegacyRepository.Save(DataSet ds)` | `OrderEntry.aspx.cs`, `Customers.aspx.cs`, `NightlyBatch` | Flush all pending changes to SQLite |
| `LegacyRepository.AddAudit(DataSet ds, string type, string subject, string detail)` | Same callers, before `Save()` | Append a domain-event row to the in-memory Audit table |

**Typical call sequence**:
```
// 1. Mutate DataSet rows
customer["ManualOverride"] = true;

// 2. Append audit record
LegacyRepository.AddAudit(ds, "USER", customerId, "ManualOverride=True");

// 3. Flush to database
LegacyRepository.Save(ds);
// → ds.AcceptChanges() called on success
```

---

## 5. Data Flows

### Primary Flow: Write Flush

```
Caller mutates DataSet rows
        │
        ▼
LegacyRepository.Save(ds)
        │  acquires LegacyState.SyncRoot lock
        │  calls EnsureDatabase()
        │  opens SqliteConnection
        │  begins SqliteTransaction
        ├─► SaveCustomers()  → DELETE / INSERT / UPDATE per RowState
        ├─► SaveOrders()     → DELETE / INSERT / UPDATE per RowState
        └─► SaveAudit()      → INSERT OR IGNORE for Added rows only
                │
                ▼
        tx.Commit()
        ds.AcceptChanges()   ← all RowStates reset to Unchanged
        lock released
```

**Steps summary**:
1. Caller loads `DataSet`, mutates rows, optionally calls `AddAudit()`.
2. `Save()` acquires the global lock and opens a connection + transaction.
3. Each `SaveXxx()` helper iterates its `DataTable`, skipping `Unchanged` rows.
4. For `Deleted` rows: issues `DELETE WHERE Id=@Id` (using `Original` version).
5. For `Added` rows: issues `INSERT` with all fields bound via parameter helpers.
6. For `Modified` rows: issues `UPDATE … WHERE Id=@Id`.
7. Audit only processes `Added` rows; uses `INSERT OR IGNORE` to tolerate DB-trigger duplicates.
8. Transaction commits; `AcceptChanges()` resets the DataSet state.

### Error Handling

| Error | Cause | Action |
|---|---|---|
| `SqliteException` during Save | Constraint violation, locked DB, disk error | Transaction rolls back automatically (connection disposed without commit); exception propagates to caller |
| Batch already running | `Interlocked.Exchange` guard in `NightlyBatch` | Returns `"Batch already running"` string; no `Save()` is attempted |
| Concurrent `Save()` calls | Multiple threads attempting simultaneous writes | Serialised by `LegacyState.SyncRoot` lock; second caller blocks until first completes |
| Duplicate Audit GUID | DB trigger `trg_OrderBlockedAudit` inserts audit row for the same event | `INSERT OR IGNORE` in `SaveAudit()` silently discards the duplicate |

---

## 6. Integrations

### Internal

- **Full Dataset Load - Read Operations** (`LegacyRepository.Load()`): Provides the `DataSet` instance whose in-memory mutations `Save()` later flushes; `RowState` tracking starts after `Load()` calls `AcceptChanges()`.
- **Database Initialization and Schema Management** (`EnsureDatabase()`): Called defensively at the start of every `Save()` invocation to guarantee the schema exists before any write.
- **Global In-Memory State** (`LegacyState.SyncRoot`): The single application-wide lock that serialises all `Save()` calls.
- **Credit Decision Engine** (`CreditDecisionService`): Populates the `Orders` DataRow fields that `SaveOrders()` later persists; does not call `Save()` directly.
- **Nightly Batch Processing** (`NightlyBatch.RunNow()`): Primary batch write caller; mutates Customer risk scores and blocked flags then calls `Save()`.
- **Web UI — OrderEntry** (`OrderEntry.aspx.cs`): Creates new Order rows and calls `Save()` on each order submission.
- **Web UI — Customers** (`Customers.aspx.cs`): Toggles `ManualOverride` on Customer rows and calls `Save()`.

### External

None. The only external I/O is to the embedded SQLite file (`App_Data/legacy-credit-desk.db`) via `Mono.Data.Sqlite`.

---

## 7. Configuration

| Property | Purpose | Default / Source |
|---|---|---|
| `App_Data/legacy-credit-desk.db` | Physical SQLite file path | Resolved relative to `HttpRuntime.AppDomainAppPath` by `LegacyRepository.DbPath` |
| `PRAGMA busy_timeout=5000` | Milliseconds SQLite waits on a locked file before returning an error | 5000 ms — set on every `OpenConnection()` call |
| `PRAGMA foreign_keys=ON` | Enforces `Orders.CustomerId → Customers.Id` foreign key | Enabled on every connection |
| `LegacyState.SyncRoot` | Shared lock object for application-wide serialisation | Static field on `LegacyState`; no external configuration |

---

## 8. Summary

- **Components**: 7 backend methods (Save, SaveCustomers, SaveOrders, SaveAudit, AddCustomerParameters, AddOrderParameters, AddAudit), 0 frontend, 3 data entities (Customers, Orders, Audit)
- **Endpoints**: 0 HTTP endpoints (internal method API only)
- **Business rules**: 5 (skip unchanged rows, append-only audit, INSERT OR IGNORE for audit duplicates, global lock serialisation, AcceptChanges on commit)
- **Integrations**: 6 internal (Load, EnsureDatabase, LegacyState, CreditDecisionService, NightlyBatch, Web UI pages), 0 external

---
**Note**: Document kept under 1500 lines. All critical write-path logic is documented. The append-only audit constraint and the interaction with the DB-level `trg_OrderBlockedAudit` trigger are the primary migration challenges for this submodule.
