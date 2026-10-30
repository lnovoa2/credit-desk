# Functional Specification: Risk Score Recalculation and Auto-Block Logic

**Parent Module**: Nightly Batch Processing | **Date**: 2026-08-10

---

## 1. Executive Summary

- **Purpose**: Background component executed on each batch tick that recalculates the risk score of every customer in the system and permanently blocks those whose overdue days reach or exceed the configured threshold (90 days by default).
- **Key Functionality**:
  1. Loads the full customer dataset from SQLite on every execution.
  2. Applies a multi-condition additive/subtractive risk score adjustment per customer based on `OverdueDays` and the `OutstandingDebt / CreditLimit` ratio.
  3. Clamps the resulting score to the range [0, 100].
  4. Permanently blocks any customer (sets `Blocked = true`) whose `OverdueDays >= 90` and who is not already blocked.
  5. Records a runtime `AUTO_BLOCKED` flag in the in-memory state for each newly blocked customer.
  6. Persists all mutations (score changes, block flags) and writes one audit record per execution to the database.
- **Users**: No direct user interaction. Triggered automatically by the batch timer or manually by operators via `Batch.aspx`.

---

## 2. Business Logic

### Key Processes

**Risk Score Recalculation**: For each customer, the system computes a new risk score by applying additive and subtractive adjustments to the current `RiskScore` value stored in the database.

1. Read `OverdueDays`, `OutstandingDebt`, `CreditLimit`, `RiskScore`, and `Blocked` for each customer.
2. Apply overdue-based adjustment (mutually exclusive, evaluated top-down):
   - If `OverdueDays > 60` → add 9 points.
   - Else if `OverdueDays > 30` → add 4 points.
   - Else if `OverdueDays == 0` AND `OutstandingDebt < CreditLimit × 0.25` → subtract 2 points.
3. Apply debt-ratio adjustment (independent, evaluated regardless of overdue branch): if `OutstandingDebt > CreditLimit` → add 7 points.
4. Clamp the new score: if `> 100` set to `100`; if `< 0` set to `0`.
5. If the new score differs from the stored score, update the `RiskScore` column in the `DataSet` and increment the change counter.

**Auto-Block Enforcement**: After the score adjustment, evaluate the auto-block condition for each customer.

1. Check: `NOT Blocked AND OverdueDays >= 90`.
2. If true, set `Blocked = true` in the `DataSet`.
3. Record the `AUTO_BLOCKED` flag in `LegacyState.CustomerFlags` (volatile, not persisted to the database).
4. Increment the change counter.
5. **The batch never unblocks a customer.** No code path in this module reverses a block.

**Persistence**: After iterating all customers, persist and audit.

1. Append one audit row to the `Audit` table (`Type = "BATCH"`, `Subject = "CREDIT_RECALC"`, `Detail = "Processed=N; Changed=M"`).
2. Call `LegacyRepository.Save(ds)` inside a SQLite transaction — commits changes to `Customers` and the new `Audit` row atomically.
3. Update `LegacyState` counters: `LastBatchRun`, `BatchExecutions` (success path only), `LastBatchMessage`.

### Critical Validations

| Field | Rule | Effect |
|-------|------|--------|
| `OverdueDays > 60` | Add `+9` to risk score | Mutually exclusive with the `> 30` and `== 0` branches |
| `OverdueDays > 30` | Add `+4` to risk score | Only fires when `OverdueDays` is in (30, 60] |
| `OverdueDays == 0` AND `debt < limit × 0.25` | Subtract `−2` from risk score | Only beneficial adjustment; requires zero overdue AND low utilization |
| `OutstandingDebt > CreditLimit` | Add `+7` to risk score | Independent of overdue branch; can stack with any overdue adjustment |
| `RiskScore` after adjustment | Clamp to [0, 100] | Prevents under/overflow |
| `OverdueDays >= 90` AND `NOT Blocked` | Set `Blocked = true` | Permanent; no unblock path in this module |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `NightlyBatch.RunNow()` (`NightlyBatch.cs:26`) | Orchestrates the full batch execution | Loads `DataSet`, iterates all customers, applies score and block logic, triggers save and audit |
| `LegacyRepository.Load()` (`LegacyRepository.cs:44`) | Loads all tables from SQLite into a `DataSet` | Fills `Customers`, `Orders`, and `Audit` tables; holds `SyncRoot` lock during read |
| `LegacyRepository.Save(ds)` (`LegacyRepository.cs:61`) | Persists all dirty rows in the `DataSet` to SQLite | Executes `UPDATE Customers` for changed rows; inserts new `Audit` row; wrapped in a transaction |
| `LegacyRepository.AddAudit()` (`LegacyRepository.cs:112`) | Appends a new row to the in-memory `Audit` table | Creates row with `BATCH` / `CREDIT_RECALC` type and detail string |
| `LegacyState.SetFlag()` (`LegacyState.cs:12`) | Records a volatile per-customer flag | Writes `AUTO_BLOCKED` to `LegacyState.CustomerFlags` under `SyncRoot` lock |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `Batch.aspx` / `Batch.aspx.cs` | Batch monitoring and manual trigger | Displays `LastBatchRun`, `BatchExecutions`, `LastBatchMessage`, and the `CustomerFlags` table; "Run batch now" button invokes `RunNow()` synchronously |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `Customers` (SQLite table) | Stores customer credit profile | `Id`, `OverdueDays`, `OutstandingDebt`, `CreditLimit`, `RiskScore`, `Blocked`, `ManualOverride` |
| `Audit` (SQLite table) | Immutable log of batch executions | `Id` (GUID), `At` (timestamp), `Type` (`BATCH`), `Subject` (`CREDIT_RECALC`), `Detail` (`Processed=N; Changed=M`) |
| `LegacyState.CustomerFlags` (in-memory `Hashtable`) | Volatile per-customer runtime flags | Key: `CustomerId` (string); Value: `"AUTO_BLOCKED"` |
| `LegacyState` (static fields) | Volatile batch execution counters | `LastBatchRun` (DateTime), `BatchExecutions` (int), `LastBatchMessage` (string) |

---

## 4. APIs and Endpoints

This submodule has no HTTP endpoints. It exposes a single internal static method consumed by two callers:

| Caller | Method | Trigger |
|--------|--------|---------|
| `NightlyBatch.Tick` (timer callback) | `NightlyBatch.RunNow()` | Automatic, every `BatchIntervalSeconds` seconds |
| `BatchPage.Run_Click` (`Batch.aspx.cs`) | `NightlyBatch.RunNow()` | Manual button click by operator |

**Return value of `RunNow()`**:
```
// Success
"Processed N customers; changed M"

// Concurrent execution guard
"Batch already running"

// Exception
"ERROR: <exception message>"
```

---

## 5. Data Flows

### Main Flow: Periodic Batch Execution

```
Timer Tick / Button Click
    → Interlocked.Exchange(_running, 1)  [concurrency guard]
    → LegacyRepository.Load()            [full DataSet from SQLite]
    → foreach Customer:
          apply risk score adjustments
          clamp [0, 100]
          if OverdueDays >= 90 AND NOT Blocked → set Blocked=true, LegacyState.SetFlag("AUTO_BLOCKED")
    → LegacyRepository.AddAudit(ds, "BATCH", "CREDIT_RECALC", detail)
    → LegacyRepository.Save(ds)          [SQLite transaction: UPDATE Customers + INSERT Audit]
    → LegacyState counters updated
    → Interlocked.Exchange(_running, 0)  [finally block — always releases lock]
```

**Steps summary**:
1. Acquire concurrency lock (returns immediately if already running).
2. Load all customers fresh from SQLite on every tick (no caching).
3. Iterate and mutate scores and block flags in the in-memory `DataSet`.
4. Commit changes to SQLite atomically; write audit record.
5. Update volatile state and release concurrency lock.

### Error Handling

| Error | Cause | Response |
|-------|-------|---------|
| `"Batch already running"` | `_running == 1` when `RunNow` is entered (overlapping ticks or concurrent manual trigger) | Returns immediately without executing; `_running` stays at `1` until the active execution finishes |
| `Exception` during processing | SQLite error, type conversion failure, etc. | Caught; stored in `LegacyState.LastBatchMessage` as `"ERROR: <message>"`; **not rethrown** — timer continues running |
| `_running` reset failure | Not possible — `finally` block guarantees reset unconditionally | `Interlocked.Exchange(_running, 0)` always executes even if an exception is thrown |

---

## 6. Integrations

### Internal

- **Data Access and Persistence Layer** (`LegacyRepository`): Provides `Load()` and `Save(ds)` — sole persistence interface. Also provides `AddAudit()` for constructing audit rows.
- **Global In-Memory State** (`LegacyState`): Receives `AUTO_BLOCKED` flags via `SetFlag()`; provides `SyncRoot` lock used by repository operations; exposes counters read by `Batch.aspx`.
- **Batch Engine - Timer and Concurrency Control**: `RunNow()` is the entry point called by `Tick()` on each timer interval; the `_running` flag is declared in this submodule and shared with the timer lifecycle.
- **Batch Monitoring and Manual Trigger UI** (`Batch.aspx`): Reads `LegacyState` to display results; can invoke `RunNow()` synchronously on the HTTP thread.
- **Web User Interface - Customers page** (`Customers.aspx`): Displays the `Blocked` flag updated by this process; provides the only path to manually reverse a block via `ManualOverride`.

### External

None. The submodule has no external system dependencies.

---

## 7. Configuration

| Property | Effect | Value in `web.config` |
|----------|--------|----------------------|
| `BatchIntervalSeconds` | Interval between automatic executions; minimum enforced at 10 s | `15` |
| `HardBlockOverdueDays` | **Not read by this submodule directly** — threshold is hardcoded as `>= 90` in `NightlyBatch.cs:55` | `90` (documented intent; not wired to code) |

> **Note**: `HardBlockOverdueDays` is present in `web.config` but `NightlyBatch.cs` uses the literal `90` rather than reading from configuration. A future migration should replace the literal with `ConfigurationManager.AppSettings["HardBlockOverdueDays"]`.

---

## 8. Summary

- **Components**: 5 backend, 1 frontend, 4 data entities
- **Endpoints**: 0 HTTP endpoints (internal method `RunNow()` only)
- **Business rules**: 6 (3 overdue-based score adjustments, 1 debt-ratio adjustment, 1 score clamp, 1 auto-block rule)
- **Integrations**: 5 internal, 0 external

---

**Note**: Document kept under 1500 lines. Prioritizes essential functional information.
