# Functional Specification: Customer Credit File Page

**Parent Module**: Web User Interface (ASP.NET Web Forms) | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides a read/write view of the full customer credit portfolio, allowing credit operators to inspect all risk attributes per customer and toggle the commercial manual override flag on demand.
- **Key Functionality**:
  1. Displays every customer record with its complete credit profile (limit, debt, overdue days, risk score, blocked state, override state).
  2. Offers a per-row action button that flips `ManualOverride` between `true` and `false`.
  3. Writes a signed audit record for every toggle operation.
  4. Persists changes atomically to the SQLite database.
  5. Refreshes the grid after each action and shows a confirmation message.
- **Users**: Credit operators / back-office staff with access to the Legacy Credit Desk console.

---

## 2. Business Logic

### Core Processes

**Toggle Manual Override**: Flips the commercial exception flag for a specific customer.
1. User clicks "Toggle manual override" in the row of the target customer.
2. Code-behind reads the customer ID from column 0 of the clicked grid row.
3. The current `ManualOverride` boolean is read from the in-memory `DataSet` and inverted.
4. An audit record is appended with actor `"USER"`, subject = customer ID, and detail `"ManualOverride=True"` or `"ManualOverride=False"`.
5. The modified `DataSet` is persisted via `LegacyRepository.Save()` inside a SQLite transaction.
6. The grid is rebound from a fresh `LegacyRepository.Load()` call and a feedback message is shown.

**Grid Load (Read-Only)**: On first page load (non-postback), the full `Customers` table is loaded from SQLite and bound to the grid. No filtering or sorting is applied.

### Critical Validations

| Field | Validation | Behaviour |
|-------|------------|-----------|
| Customer ID (grid column 0) | Must match an existing customer in the `DataSet` (`FindCustomer` returns non-null) | If `null`, the toggle is silently skipped; no error shown |
| `CommandName` | Must equal `"ToggleOverride"` | Other commands are ignored via early return |
| `ManualOverride` | No domain constraint — any boolean value is accepted | Blindly inverted on each click |

### Important Business Rule

- **Manual override does NOT unblock customers.** The `Blocked` field is never touched by this page. The on-screen info box (`div.box`) explicitly states this constraint.
- **Override does NOT bypass hard stops.** The `CreditDecisionService` may still block orders for hard-stop reasons even when `ManualOverride=true`. The override only affects the risk-scoring adjustments applied within that service.

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `Customers.aspx.cs` (`CustomersPage`) | Code-behind controller | `Page_Load` → bind grid on first load; `CustomerGrid_RowCommand` → handle toggle; `BindGrid()` → reload and bind data |
| `LegacyRepository` | Data access layer | `Load()` — full dataset from SQLite; `FindCustomer()` — row lookup by ID; `AddAudit()` — append audit row; `Save()` — transactional persist |

### Frontend

| Component | Purpose | Key Functionality |
|-----------|---------|-------------------|
| `Customers.aspx` | Page markup | Declares `CustomerGrid` (GridView), info box, and `Message` label |
| `Site.master` | Shared layout | Navigation bar, page chrome, CSS injection |
| `Content/site.css` | Stylesheet | `.grid` table styles, `.box` info panel, `.btn` button styles |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `Customers` (SQLite table) | Credit profile per customer | `Id` (PK), `Name`, `Country`, `Since`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked` (bool), `ManualOverride` (bool) |
| `Audit` (SQLite table) | Immutable action log | `Id` (GUID), `At` (timestamp), `Type` (`"USER"`), `Subject` (customer ID), `Detail` (action string) |

---

## 4. APIs and Endpoints

This submodule uses the ASP.NET Web Forms postback model — there are no REST or HTTP API endpoints. All interactions are full-page round-trips via `form1` POST.

### Page Interactions

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| GET | `/Customers.aspx` | Initial page load, grid render | No (application-level only) |
| POST | `/Customers.aspx` | Toggle action postback (`__EVENTARGUMENT` = row index) | No (application-level only) |

**Postback Flow**:
```
// Request (HTTP POST form fields — ASP.NET ViewState driven)
__EVENTTARGET:    CustomerGrid
__EVENTARGUMENT:  <row-index>
CommandName:      ToggleOverride

// Response
Full page HTML with updated CustomerGrid + Message label
```

---

## 5. Data Flows

### Main Flow: Toggle Manual Override

```
User → Clicks "Toggle manual override" (row N)
     → HTTP POST to Customers.aspx
     → CustomerGrid_RowCommand fires (CommandName = "ToggleOverride")
     → Read customer ID from Rows[N].Cells[0].Text
     → LegacyRepository.Load() → DataSet
     → LegacyRepository.FindCustomer(ds, id) → DataRow
     → Invert customer["ManualOverride"]
     → LegacyRepository.AddAudit(ds, "USER", id, "ManualOverride=<value>")
     → LegacyRepository.Save(ds) → SQLite transaction COMMIT
     → BindGrid() → LegacyRepository.Load() → CustomerGrid.DataBind()
     → Message.Text = "Override changed for <id>"
     → HTML response rendered to browser
```

**Steps Summary**:
1. Postback triggers row command with row index as argument.
2. Customer ID extracted from grid cell (positional — column 0).
3. `ManualOverride` flipped in-memory and audit appended.
4. Full `DataSet` saved transactionally; grid reloaded from DB.

### Error Handling

| Error | Cause | Behaviour |
|-------|-------|-----------|
| Customer not found | ID in cell does not match any `Customers` row | Toggle silently skipped; no exception; grid rebound unchanged |
| SQLite write failure | DB locked, constraint violation, disk issue | Unhandled exception — ASP.NET default error page shown |
| Grid column reorder | `Cells[0]` no longer maps to customer ID | Wrong ID extracted; silently operates on wrong customer or fails |

---

## 6. Integrations

### Internal
- **Data Access and Persistence Layer** (`LegacyRepository`): All read and write operations to the `Customers` and `Audit` tables. Called on every page load and every toggle action.
- **Master Page Layout and Navigation** (`Site.master`): Provides the outer HTML shell, navigation bar, and stylesheet link shared by all pages.
- **Application Stylesheet** (`Content/site.css`): Supplies `.grid`, `.box`, and `.btn` CSS classes used by the grid and info panel.

### External
- None. This page does not call `CreditDecisionService`, `NightlyBatch`, or any external system.

---

## 7. Configuration

| Property | Purpose | Default / Source |
|----------|---------|-----------------|
| `App_Data/legacy-credit-desk.db` | SQLite database path (derived from `HttpRuntime.AppDomainAppPath`) | Runtime-resolved; no config key |
| `PRAGMA busy_timeout=5000` | SQLite lock-wait timeout per connection | Hard-coded in `LegacyRepository.OpenConnection()` |
| Grid column order | Determines which `Cells[N]` index is used to extract the customer ID | Hard-coded: column 0 = `Id` in `Customers.aspx` markup |

---

## 8. Summary

- **Components**: 2 backend (CustomersPage, LegacyRepository), 3 frontend (Customers.aspx, Site.master, site.css), 2 data entities (Customers, Audit)
- **Endpoints**: 2 (GET initial load, POST toggle action — Web Forms postback model)
- **Business rules**: 3 (toggle inverts ManualOverride only; does not unblock; does not bypass hard stops)
- **Integrations**: 3 internal (LegacyRepository, Site.master, site.css), 0 external

### Migration Notes
- The customer ID lookup depends on `Cells[0].Text` matching the `Id` column at grid column index 0. Any reordering of `<asp:BoundField>` elements in the markup without a matching code-behind update will silently break the toggle, directing it at a wrong customer or causing a silent no-op.
- Audit records are append-only (`INSERT OR IGNORE`); there is no delete or update path for the `Audit` table.
- `LegacyRepository.Save()` is called with the full `DataSet`, which persists all three tables (`Customers`, `Orders`, `Audit`) in one transaction. On this page only `Customers` and `Audit` rows are ever modified.

---
*Note: Document maintained under 1500 lines. Focused on essential functional information.*

---

## 9. Behavioral Contract

### 9.1 Events and Invoked Logic

| Trigger | Invoked Logic | Effect | Source |
|---------|---------------|--------|--------|
| Click "Toggle manual override" on a customer row (`CommandName=ToggleOverride`) | Read customer ID from grid column 0, invert `ManualOverride`, append audit record, `LegacyRepository.Save()`, rebind grid | Customer's `ManualOverride` flips, audit entry (actor `USER`, `ManualOverride=True/False`) is stored, grid and feedback message refresh | Customers.aspx.cs |
| First load (GET, non-postback) | `LegacyRepository.Load()` and bind `Customers` table | Full customer list shown, unfiltered and unsorted | Customers.aspx.cs |
| Postback with unknown customer ID or other `CommandName` | Early return | No change, no error shown | Customers.aspx.cs |

### 9.2 Interfaces and Entry Points

| Type | Identifier | Purpose | Source |
|------|------------|---------|--------|
| Page (GET) | `/Customers.aspx` | Render customer grid | Customers.aspx |
| Page (POST) | `/Customers.aspx` | Toggle override postback (`__EVENTARGUMENT` = row index) | Customers.aspx.cs |

### 9.3 Invariants and Oracles

| # | Invariant | Rule | Observable | Oracle (input → expected output) | Source |
|---|-----------|------|------------|----------------------------------|--------|
| 1 | Toggle is an involution | Two consecutive toggles restore the original value | `ManualOverride` | ManualOverride=false → toggle → true → toggle → false | Customers.aspx.cs |
| 2 | Override never changes `Blocked` | `Blocked` is not modified by the page | `Blocked` | Blocked=true, toggle → Blocked=true | Customers.aspx.cs |
| 3 | Every toggle is audited | One audit record per effective toggle | Audit log | Toggle customer 5 to true → audit(actor=USER, subject=5, detail="ManualOverride=True") | Customers.aspx.cs |
| 4 | Unknown customer is ignored silently | `FindCustomer == null` → skip | DataSet, message | Toggle id=999999 → no data change, no error | Customers.aspx.cs |
