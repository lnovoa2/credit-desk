# Functional Specification: Direct SQL Query Aggregates - Backend Component

**Parent Module**: Data Access and Persistence Layer | **Date**: 2026-08-10

## 1. Executive Summary
- **Purpose**: Provides real-time calculated aggregates by querying the SQLite database directly, deliberately bypassing the in-memory DataSet already loaded in memory. Supplies the Credit Decision Engine with the two critical financial values it needs to evaluate an order.
- **Key Functionality**:
  - Calculates total open order exposure per customer (non-blocked orders sum)
  - Counts orders placed within a configurable rolling time window per customer
  - Provides an in-memory customer lookup helper against the DataSet
  - Provides a generic scalar integer query helper used internally
- **Users**: No direct user interaction. Invoked internally by `CreditDecisionService` during every credit evaluation triggered from `OrderEntry.aspx`.

---

## 2. Business Logic

### Key Processes

**Open Order Exposure Calculation**: Determines how much credit a customer has already committed in active (non-blocked) orders before adding a new order amount.
1. Receive customer ID from `CreditDecisionService.Evaluate()`
2. Open a fresh database connection (bypasses in-memory DataSet)
3. Execute `SELECT COALESCE(SUM(Amount),0) FROM Orders WHERE CustomerId=@id AND Status<>'BLOCKED'`
4. Return the decimal sum (zero if no open orders exist)
5. Caller adds this value to `OutstandingDebt + newAmount` to compute total exposure

**Recent Order Frequency Count**: Detects high-frequency ordering behavior within a rolling window, which contributes to risk score adjustment.
1. Receive customer ID and lookback days (always 14 days in current codebase)
2. Compute the cutoff timestamp: `DateTime.Now.AddDays(-days)`
3. Open a fresh database connection
4. Execute `SELECT COUNT(*) FROM Orders WHERE CustomerId=@id AND CreatedAt>=@edge`
5. Return the integer count; if count >= 3, caller adds +8 to the effective risk score

**In-Memory Customer Lookup**: Allows callers to retrieve a single customer `DataRow` from the already-loaded `DataSet` without a new database query.
1. Receive the DataSet and the customer ID string
2. Call `DataTable.Select()` with a SQL-escaped predicate: `Id='<escapedId>'`
3. Return the first matching `DataRow`, or `null` if not found

### Critical Validations
| Field | Validation | Effect |
|-------|------------|--------|
| `Status` (Orders) | Must not equal `'BLOCKED'` | Excluded from open exposure sum |
| `CreatedAt` (Orders) | Must be >= rolling window cutoff | Included in recent order count |
| Customer ID | SQL-escaped via `Escape()` helper (`'` → `''`) | Prevents SQL injection in `FindCustomer` |
| `COALESCE` on SUM | Applied to handle NULL when no rows match | Returns 0 instead of NULL for a customer with no open orders |

---

## 3. Components

### Backend
| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `SumOpenOrders(DataSet, string)` | Aggregate open financial exposure per customer | Direct DB query bypassing DataSet; returns `decimal`; used to compute total exposure in credit evaluation |
| `CountRecentOrders(DataSet, string, int)` | Count orders in rolling time window | Direct DB query; computes cutoff via `DateTime.Now.AddDays(-days)`; contributes to risk score adjustment |
| `FindCustomer(DataSet, string)` | In-memory customer row lookup | Queries `DataSet` (not DB) using `DataTable.Select()`; SQL-escapes ID to prevent injection |
| `ScalarInt(string)` | Generic scalar integer query | Internal helper; opens connection, executes raw SQL, returns `int`; used by `EnsureDatabase` to count rows |

### Frontend
*None — this submodule has no UI components.*

### Data
| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `Orders` table | Source for both aggregate queries | `Id`, `CustomerId`, `Amount`, `Status`, `CreatedAt` |
| `Customers` DataTable | Source for in-memory lookup via `FindCustomer` | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride` |

---

## 4. APIs and Endpoints

### Main Endpoints
*This submodule exposes no HTTP endpoints.* All methods are internal C# static methods on `LegacyRepository`.

### Internal Method Signatures
| Method | Signature | Returns |
|--------|-----------|---------|
| `SumOpenOrders` | `(DataSet ds, string customerId)` | `decimal` |
| `CountRecentOrders` | `(DataSet ds, string customerId, int days)` | `int` |
| `FindCustomer` | `(DataSet ds, string id)` | `DataRow` or `null` |
| `ScalarInt` | `(string sql)` — private | `int` |

**SQL Queries Executed**:
```sql
-- SumOpenOrders
SELECT COALESCE(SUM(Amount),0) FROM Orders
WHERE CustomerId=@id AND Status<>'BLOCKED'

-- CountRecentOrders
SELECT COUNT(*) FROM Orders
WHERE CustomerId=@id AND CreatedAt>=@edge

-- ScalarInt (example usage in EnsureDatabase)
SELECT COUNT(*) FROM Customers
```

---

## 5. Data Flows

### Main Flow: Credit Evaluation Order Submission

```
OrderEntry.aspx → Load() → FindCustomer(ds, id) → CreditDecisionService.Evaluate()
                                                         │
                                  ┌──────────────────────┤
                                  │                      │
                           SumOpenOrders()        CountRecentOrders()
                           (direct DB query)      (direct DB query)
                                  │                      │
                                  └──────────────────────┘
                                           │
                                    exposure + risk computed
                                           │
                                    credit decision returned
                                           │
                                    Save(ds) → SQLite
```

**Steps**:
1. `OrderEntry.aspx.cs` calls `LegacyRepository.Load()` to obtain the full in-memory `DataSet`
2. `FindCustomer(ds, customerId)` retrieves the customer `DataRow` from the DataSet
3. `CreditDecisionService.Evaluate()` calls `SumOpenOrders()` — opens a new DB connection and queries live Orders table
4. `CreditDecisionService.Evaluate()` calls `CountRecentOrders(ds, customerId, 14)` — opens a second DB connection and queries Orders within the last 14 days
5. Results feed into exposure calculation (`debt + openOrders + newAmount`) and risk score adjustment (`recentOrders >= 3` → `risk += 8`)

### Error Handling
| Error | Cause | Action |
|-------|-------|--------|
| `SqliteException` | Connection failure or DB locked | Exception propagates up; unhandled at this layer |
| Customer not found | `FindCustomer` returns `null` | Caller (`OrderEntry.aspx.cs`) must handle null before calling `Evaluate()` |
| `COALESCE` guard | No open orders for customer | Returns `0m` safely; no error raised |

---

## 6. Integrations

### Internal
- **Credit Decision Engine (`CreditDecisionService`)**: Primary consumer — calls `SumOpenOrders` and `CountRecentOrders` on every credit evaluation; results feed directly into exposure and risk score calculations
- **Database Initialization and Schema Management**: `EnsureDatabase()` called defensively at the start of both `SumOpenOrders` and `CountRecentOrders` before opening connections
- **Full Dataset Load - Read Operations**: `FindCustomer` operates against the `DataSet` produced by `Load()`; Web UI code-behinds call `FindCustomer` after calling `Load()`
- **Global In-Memory State (`LegacyState.SyncRoot`)**: Acquired by `EnsureDatabase()` (called internally); aggregate query methods themselves do **not** acquire the lock independently

### External
*None — no external system integrations.*

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `HardBlockOverdueDays` | Used by caller (`CreditDecisionService`), not by this submodule directly | `90` |
| Rolling window `days` parameter | Passed by `CreditDecisionService` to `CountRecentOrders` — currently hardcoded to `14` at call site | `14` days |
| `busy_timeout` PRAGMA | Set on every `OpenConnection()` call; prevents immediate failure under DB contention | `5000` ms |

---

## 8. Summary
- **Components**: 4 backend, 0 frontend, 2 data entities
- **Endpoints**: 0 HTTP endpoints (internal methods only)
- **Business Rules**: 2 — open exposure aggregation (excludes BLOCKED status), recent order frequency window (14-day rolling)
- **Integrations**: 4 internal, 0 external

### Migration Notes
This submodule represents intentional **legacy data access debt**: `SumOpenOrders` and `CountRecentOrders` open fresh database connections even though a fully populated `DataSet` is already resident in memory. This mixed-access pattern creates a consistency window risk (DB may differ from the in-memory copy if a concurrent write has occurred) and is explicitly noted as a primary migration challenge in the source code comment at `LegacyRepository.cs:84`. A migration should replace these direct SQL queries with equivalent LINQ or ORM queries against the already-loaded in-memory data, eliminating the extra DB round-trips and the consistency window.

---
**Note**: Document kept under 1500 lines. Essential functional information prioritised.
