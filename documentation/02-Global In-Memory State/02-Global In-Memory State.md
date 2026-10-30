# Submodules of Module: Global In-Memory State

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 4
**Analysis Date**: 2026-08-10
**Parent Module**: Global In-Memory State

## Summary
Total submodules identified: **3**

### Distribution by Type
- Backend Components: 3
- Frontend Components: 0
- Data Layer: 0
- API/Services: 0
- Configuration: 0
- Utilities: 0

## Submodule Index
1. [Shared Lock and Synchronisation Root](#shared-lock-and-synchronisation-root) - Backend Component
2. [Per-Customer Runtime Flag Store](#per-customer-runtime-flag-store) - Backend Component
3. [Batch Execution State Counters](#batch-execution-state-counters) - Backend Component

---

## Submodule Detail

### Shared Lock and Synchronisation Root

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/LegacyState.cs` — line 6 (`SyncRoot` field declaration)

**Purpose**: Exposes the single process-wide `object SyncRoot` that all callers must `lock` before reading or writing any mutable field in `LegacyState`. Ensures thread safety across the shared static class without requiring callers to maintain their own lock objects.

**Key Files**:
1. `LegacyState.cs` — declares `public static readonly object SyncRoot = new object();`

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Used by Per-Customer Runtime Flag Store and Batch Execution State Counters
- **Libraries**: `System` (BCL)

**Related Components**:
- **Entities**: None (no database persistence)
- **Services**: `NightlyBatch.cs` — acquires `SyncRoot` lock when updating batch state
- **APIs**: None
- **UI**: None

**Integration Points**:
- Any code that mutates or reads `LegacyState` fields must `lock(LegacyState.SyncRoot)` — the lock object is the single coordination point for the entire shared-state class.

---

### Per-Customer Runtime Flag Store

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/LegacyState.cs` — lines 7, 13–27 (`CustomerFlags` field + `SetFlag`/`GetFlag` methods)

**Purpose**: Maintains a `Hashtable` keyed by customer ID that stores per-customer runtime flags (e.g. `"AUTO_BLOCKED"`). Provides thread-safe `SetFlag` and `GetFlag` accessors. This state is process-scoped, not persisted, and is lost on application restart.

**Key Files**:
1. `LegacyState.cs` — `CustomerFlags` field (line 7), `SetFlag(string, string)` (lines 13–18), `GetFlag(string)` (lines 20–27)

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository`) — callers may cross-reference persisted customer records
- **Internal Submodules**: Shared Lock and Synchronisation Root (both accessors lock on `SyncRoot`)
- **Libraries**: `System.Collections.Hashtable`

**Related Components**:
- **Entities**: Customer records (customer ID is the key)
- **Services**: `NightlyBatch.cs` — writes `"AUTO_BLOCKED"` flag via `SetFlag`; `CreditDecisionService.cs` — reads flag via `GetFlag` to apply auto-block veto
- **APIs**: None
- **UI**: None directly (decision outcome displayed via `OrderEntry.aspx`)

**Integration Points**:
- `NightlyBatch` calls `LegacyState.SetFlag(customerId, "AUTO_BLOCKED")` after automatic block criteria are met.
- `CreditDecisionService` calls `LegacyState.GetFlag(customerId)` to check for `"AUTO_BLOCKED"` before issuing an approval.

---

### Batch Execution State Counters

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/LegacyState.cs` — lines 8–10 (`LastBatchRun`, `LastBatchMessage`, `BatchExecutions` field declarations)

**Purpose**: Holds three primitive fields that record the last batch run timestamp (`DateTime LastBatchRun`), the human-readable result message of the last run (`string LastBatchMessage`), and the cumulative count of batch executions since process start (`int BatchExecutions`). These values are read by the Batch monitoring UI and updated by `NightlyBatch` after each run.

**Key Files**:
1. `LegacyState.cs` — `LastBatchRun` (line 8), `LastBatchMessage` (line 9), `BatchExecutions` (line 10)

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Shared Lock and Synchronisation Root (callers are expected to lock before mutating these fields)
- **Libraries**: `System.DateTime`

**Related Components**:
- **Entities**: Batch audit records in SQLite (written independently by `LegacyRepository`; these fields are the in-memory mirror)
- **Services**: `NightlyBatch.cs` — writes all three fields after each batch run
- **APIs**: None
- **UI**: `Batch.aspx` / `Batch.aspx.cs` — reads these fields to display last-run timestamp, message, and execution count

**Integration Points**:
- `NightlyBatch` updates `LegacyState.BatchExecutions++`, `LegacyState.LastBatchRun = DateTime.UtcNow`, and `LegacyState.LastBatchMessage` after completing each batch cycle.
- `Batch.aspx.cs` reads these three fields on page load to render the batch status panel.

---

## Relationship Map

### Data Flow

```text
[NightlyBatch.cs]
    │
    ├─ lock(SyncRoot) → SetFlag(customerId, "AUTO_BLOCKED") → CustomerFlags[customerId]
    │
    └─ (no lock used in existing code, but SyncRoot available) → LastBatchRun / LastBatchMessage / BatchExecutions

[CreditDecisionService.cs]
    └─ lock(SyncRoot) → GetFlag(customerId) → CustomerFlags[customerId]

[Batch.aspx.cs]
    └─ reads LastBatchRun / LastBatchMessage / BatchExecutions (display only)
```

### Call Hierarchy
- `NightlyBatch` calls `LegacyState.SetFlag` (writes AUTO_BLOCKED flags)
- `NightlyBatch` writes `LegacyState.LastBatchRun`, `LastBatchMessage`, `BatchExecutions`
- `CreditDecisionService` calls `LegacyState.GetFlag` (reads AUTO_BLOCKED flags)
- `Batch.aspx.cs` reads batch counter fields

### UI-Backend Mapping
| UI Component | Backend Service | Database Table |
|---|---|---|
| `Batch.aspx` | `LegacyState.BatchExecutions` / `LastBatchRun` / `LastBatchMessage` | None (in-memory only) |
| `OrderEntry.aspx` | `CreditDecisionService` → `LegacyState.GetFlag` | `Orders` (result persisted) |

---

## Completeness Verification

### File Statistics
- Total backend files in module directory (`/App_Code/LegacyState.cs`): 1 — Documented: 1
- Total frontend files: 0 — Documented: 0
- Total JavaScript files: 0 — Documented: 0
- Total CSS files: 0 — Documented: 0
- Total SQL scripts: 0 — Documented: 0
- **Coverage**: 100%

### Directories Analyzed
- [x] `/App_Code/` — 1 file (`LegacyState.cs`)

---

## Technical Notes
- `LegacyState` is a `static` class; there is no instantiation — all fields and methods are process-global singletons.
- `CustomerFlags` uses `Hashtable` (non-generic, .NET 1.x style) instead of `ConcurrentDictionary<string,string>`, which is a migration smell.
- The three batch-counter fields (`LastBatchRun`, `LastBatchMessage`, `BatchExecutions`) are mutated by `NightlyBatch` without an explicit `lock(SyncRoot)` in the existing code — a potential data race if batch executions overlap (unlikely given a single `System.Threading.Timer`, but worth noting for migration).
- None of this state is persisted to SQLite; a process restart resets all flags and counters.

---

## Complexity and Prioritisation

### Submodule Complexity
- **High**: Per-Customer Runtime Flag Store — thread-safety contract, cross-cutting impact on credit decisions
- **Medium**: Batch Execution State Counters — simple fields but subtle missing-lock risk
- **Low**: Shared Lock and Synchronisation Root — single field, no logic

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analysed in full detail to produce the complete functional specification.
