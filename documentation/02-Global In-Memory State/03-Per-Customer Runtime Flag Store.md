# Functional Specification: Per-Customer Runtime Flag Store

**Parent Module**: Global In-Memory State | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides a process-scoped, in-memory key-value store that records runtime flags per customer. Its primary role is to propagate the `AUTO_BLOCKED` status set by the nightly batch process to the credit decision engine without a database round-trip.
- **Key Functionality**:
  - Stores arbitrary string flags keyed by customer ID in a `Hashtable`
  - Exposes thread-safe `SetFlag` and `GetFlag` accessors protected by `LegacyState.SyncRoot`
  - Enables the credit decision engine to veto order approvals for automatically blocked customers
  - State is entirely volatile: lost on application restart, never written to SQLite
  - Single flag currently in use: `"AUTO_BLOCKED"`
- **Users**: No direct user interaction. Consumed internally by `NightlyBatch` (writer) and `CreditDecisionService` (reader). Decision outcomes surface to operations staff via `OrderEntry.aspx`.

---

## 2. Business Logic

### Key Processes

**Automatic Customer Block Propagation**: When the nightly batch detects a customer with `OverdueDays >= 90`, it sets the `Blocked` flag in the database **and** writes `"AUTO_BLOCKED"` into this store. The credit decision engine reads this flag independently, creating a redundant but intentionally distinct veto path.

1. `NightlyBatch.RunNow()` iterates all customers loaded into a `DataSet`.
2. For each customer where `OverdueDays >= 90` and `Blocked == false`, sets `Blocked = true` in the `DataSet` row.
3. Calls `LegacyState.SetFlag(customerId, "AUTO_BLOCKED")` to record the flag in memory.
4. Persists the `DataSet` to SQLite via `LegacyRepository.Save()`.
5. On the next order submission, `CreditDecisionService.Evaluate()` calls `LegacyState.GetFlag(customerId)` — if the result is `"AUTO_BLOCKED"`, the block veto is available to the decision rules.

**Credit Decision Veto (flag read path)**: `CreditDecisionService` reads the flag but applies the block through the customer's `Blocked` database field (rule `B01`). The `AUTO_BLOCKED` flag in this store is not explicitly checked in the current `CreditDecisionService` code; the batch writes both the database field and the in-memory flag, and the credit engine reads the database field. The in-memory flag therefore acts as an audit/signalling channel rather than a direct veto.

1. `OrderEntry.aspx.cs` calls `CreditDecisionService.Evaluate(ds, customerRow, amount, paymentMethod, priority)`.
2. The `customer["Blocked"]` field (from the database-loaded `DataSet`) triggers rule `B01` if `true` and `manualOverride` is `false`.
3. `LegacyState.GetFlag(customerId)` returns `"AUTO_BLOCKED"` if the batch previously set it; this value is available to callers but not directly branched on within `CreditDecisionService` in the current codebase.

### Critical Validations

| Field | Validation | Error |
|-------|------------|-------|
| `customerId` (SetFlag) | Must be non-null key; `Hashtable` accepts any object as key | Silent overwrite if key exists |
| `customerId` (GetFlag) | Returns `""` (empty string) if key absent — never `null` | No exception; caller must check for `"AUTO_BLOCKED"` |
| Flag value | Any string accepted; only `"AUTO_BLOCKED"` is currently written | No validation — open to future misuse |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `LegacyState.CustomerFlags` (`LegacyState.cs:7`) | In-memory flag store | Holds `Hashtable<customerId, flagValue>`; mutable, process-scoped |
| `LegacyState.SetFlag` (`LegacyState.cs:12-18`) | Thread-safe flag writer | Acquires `SyncRoot` lock; sets `CustomerFlags[customerId] = value` |
| `LegacyState.GetFlag` (`LegacyState.cs:20-27`) | Thread-safe flag reader | Acquires `SyncRoot` lock; returns `""` if key absent, otherwise `value.ToString()` |
| `NightlyBatch.RunNow` (`NightlyBatch.cs:26-79`) | Flag writer caller | Calls `SetFlag` when auto-block criteria met (`OverdueDays >= 90`) |
| `CreditDecisionService.Evaluate` (`CreditDecisionService.cs:17`) | Flag reader caller | Available to call `GetFlag`; customer block status cross-checked via `DataSet` field |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `OrderEntry.aspx` | Order submission UI | Invokes `CreditDecisionService`; displays decision outcome including `BLOCKED` status to the operator |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `LegacyState.CustomerFlags` (in-memory) | Runtime flag registry | Key: `customerId` (string); Value: flag string (e.g. `"AUTO_BLOCKED"`) |
| `Customers` table (SQLite) | Persistent customer state | `Id`, `Blocked` (boolean), `OverdueDays` |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| POST | `/OrderEntry.aspx` | Submit order; triggers flag read indirectly via `CreditDecisionService` | Session (Web Forms) |
| POST | `/Batch.aspx` | Manual batch trigger; causes flag writes for newly blocked customers | Session (Web Forms) |

**Internal API (no HTTP — direct static method calls)**:

```csharp
// Writer — called by NightlyBatch
LegacyState.SetFlag(string customerId, string value);
// Example: LegacyState.SetFlag("42", "AUTO_BLOCKED");

// Reader — available to CreditDecisionService
string flag = LegacyState.GetFlag(string customerId);
// Returns "AUTO_BLOCKED" or "" (never null)
```

---

## 5. Data Flows

### Main Flow: Automatic Block Flag Write

```
NightlyBatch timer tick
  → Load all Customers from SQLite (LegacyRepository.Load)
  → For each customer: evaluate OverdueDays >= 90
  → Set DataSet row Blocked = true
  → LegacyState.SetFlag(customerId, "AUTO_BLOCKED")   ← in-memory flag written
  → LegacyRepository.Save(ds)                          ← Blocked=true persisted to SQLite
  → Update LastBatchRun / LastBatchMessage / BatchExecutions
```

**Steps**:
1. Timer fires `NightlyBatch.Tick` → calls `RunNow()`.
2. `RunNow` uses `Interlocked.Exchange` to prevent concurrent runs.
3. Each qualifying customer gets `Blocked = true` in the `DataSet` and `"AUTO_BLOCKED"` in `CustomerFlags`.
4. `LegacyRepository.Save` flushes the `DataSet` to SQLite.
5. Flag persists in memory until process restart; database flag persists indefinitely.

### Secondary Flow: Flag Read at Order Evaluation

```
Operator submits order (OrderEntry.aspx)
  → CreditDecisionService.Evaluate(ds, customerRow, amount, ...)
  → Reads customer["Blocked"] from DataSet (loaded from SQLite)
  → If Blocked=true and ManualOverride=false → BLOCKED (B01)
  → LegacyState.GetFlag(customerId) available for additional checks
  → Decision returned to OrderEntry.aspx for display
```

### Error Handling

| Error | Cause | Action |
|-------|-------|--------|
| Concurrent `SetFlag` calls | Multiple threads writing flags simultaneously | `lock(SyncRoot)` serialises all writes; no data loss |
| `GetFlag` on unknown customer | Customer never flagged or process restarted | Returns `""` — caller must treat `""` as no flag |
| `RunNow` re-entry | Timer fires while previous run still active | `Interlocked.Exchange` returns early with "Batch already running" message |
| Exception during batch | Any error in `RunNow` | Caught; `LastBatchMessage` set to `"ERROR: <message>"`; `_running` reset in `finally` |

---

## 6. Integrations

### Internal

- **Shared Lock and Synchronisation Root** (`LegacyState.SyncRoot`): Both `SetFlag` and `GetFlag` acquire this lock before accessing `CustomerFlags` — mandatory for thread safety across concurrent web requests and the background batch timer.
- **Nightly Batch Processing** (`NightlyBatch.cs`): Sole writer of `"AUTO_BLOCKED"` flags. Writes occur inside `RunNow()` after the overdue threshold is confirmed.
- **Credit Decision Engine** (`CreditDecisionService.cs`): Consumer of `GetFlag`. The `AUTO_BLOCKED` flag is available as a supplementary signal; the primary block veto uses the `Blocked` column from the persisted `DataSet`.
- **Data Access and Persistence Layer** (`LegacyRepository.cs`): Provides the `DataSet` that `NightlyBatch` uses to determine which customers to flag. The flag store itself does not interact with the repository.

### External

- None. This submodule has no external system dependencies.

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `HardBlockOverdueDays` (`web.config`) | Threshold (days overdue) above which `CreditDecisionService` issues a hard `B90` block independently of the flag store | `90` |
| `BatchIntervalSeconds` (`web.config`) | Controls how frequently `NightlyBatch` fires, which is the only mechanism that writes `AUTO_BLOCKED` flags | `45` |

> **Note**: There is no configuration for the flag store itself. The `Hashtable` capacity, flag key names, and retention policy are all hardcoded.

---

## 8. Summary

- **Components**: 3 backend (field + 2 methods in `LegacyState.cs`), 1 frontend (`OrderEntry.aspx`), 1 data entity (in-memory `CustomerFlags`)
- **Endpoints**: 2 Web Forms pages (indirect); 0 REST/HTTP endpoints
- **Business rules**: 2 (auto-block write on `OverdueDays >= 90`; block veto via `Blocked` database field + `AUTO_BLOCKED` in-memory signal)
- **Integrations**: 4 internal, 0 external

---

**Note**: Document < 1500 lines. Prioritises essential functional information.
