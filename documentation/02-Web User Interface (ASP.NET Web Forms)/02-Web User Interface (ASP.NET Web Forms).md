# Submodules of Module: Web User Interface (ASP.NET Web Forms)

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 6
**Analysis Date**: 2026-08-10
**Parent Module**: Web User Interface (ASP.NET Web Forms)

## Summary
Total submodules identified: **6**

### Distribution by Type
- Backend Components: 4
- Frontend Components: 5
- Data Layer: 0
- API/Services: 0
- Configuration: 0
- Utilities: 1

## Submodule Index
1. [Master Page Layout and Navigation](#master-page-layout-and-navigation) - Frontend Component
2. [Operations Dashboard Page](#operations-dashboard-page) - Frontend Component + Backend Component
3. [Customer Credit File Page](#customer-credit-file-page) - Frontend Component + Backend Component
4. [Order Entry and Credit Authorization Page](#order-entry-and-credit-authorization-page) - Frontend Component + Backend Component
5. [Batch Monitor and Manual Trigger Page](#batch-monitor-and-manual-trigger-page) - Frontend Component + Backend Component
6. [Application Stylesheet](#application-stylesheet) - Utility

---

## Submodule Detail

### Master Page Layout and Navigation

**Type**: Frontend Component

**Location**:
- **Frontend**: `/Site.master`

**Purpose**: Defines the shared HTML shell (doctype, `<head>`, top banner, navigation bar, and content placeholder) that all four application pages inherit. Provides consistent chrome — application title, navigation links to Dashboard, Customers, New Order, and Night Batch — and injects the single global stylesheet. All pages embed their content in the `MainContent` `ContentPlaceHolder` declared here.

**Key Files**:
1. `Site.master` — ASP.NET master page; supplies the outer HTML document structure, top banner (`div.top`), navigation bar (`div.nav`), and `<asp:ContentPlaceHolder ID="MainContent">` for page-specific content

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Application Stylesheet (`Content/site.css` — linked via `<link>` tag)
- **Libraries**: `System.Web` (ASP.NET Web Forms master-page runtime)

**Related Components**:
- **Entities**: None
- **Services**: None
- **APIs**: None
- **UI**: All four application pages (`Default.aspx`, `Customers.aspx`, `OrderEntry.aspx`, `Batch.aspx`) inherit from this master page via `MasterPageFile="~/Site.master"`

**Integration Points**:
- Linked by every `.aspx` page through the `MasterPageFile` directive; changing navigation or layout here affects the entire application in one edit
- The `<form id="form1" runat="server">` wrapping the `ContentPlaceHolder` means every page's server controls participate in a single top-level form post

---

### Operations Dashboard Page

**Type**: Frontend Component + Backend Component

**Location**:
- **Frontend**: `/Default.aspx`
- **Backend**: `/Default.aspx.cs`

**Purpose**: Renders a read-only operations overview on application load. Computes four KPI metrics (total customers, blocked count, orders in REVIEW status, total outstanding debt) from the in-memory `DataSet`, then displays the 8 most recent orders and audit entries as auto-generated grid tables. Performs no write operations.

**Key Files**:
1. `Default.aspx` — Page markup; declares `<asp:Literal ID="Kpis">` for the KPI tile row and two `<asp:GridView>` controls (`OrdersGrid`, `AuditGrid`) for recent data tables
2. `Default.aspx.cs` — Code-behind class `DashboardPage`; `Page_Load` loads the `DataSet`, aggregates KPIs via LINQ-style `DataTable.Select`, and binds the grids using the private `Tail()` helper to take the last N rows

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository.Load()`)
- **Internal Submodules**: Master Page Layout and Navigation (inherits `Site.master`)
- **Libraries**: `System.Data`, `System.Web.UI`

**Related Components**:
- **Entities**: `Customers` table (for KPI aggregation), `Orders` table (grid), `Audit` table (grid)
- **Services**: `LegacyRepository.Load()` — sole data source
- **APIs**: None
- **UI**: `Kpis` Literal (inline HTML KPI tiles), `OrdersGrid` GridView, `AuditGrid` GridView

**Integration Points**:
- Non-destructive read path; calls only `LegacyRepository.Load()`, never `Save()`
- KPI for "manual reviews" counts Orders with `Status='REVIEW'`; KPI for total debt iterates all customer rows and sums `OutstandingDebt` via a `for` loop (not a SQL aggregate)
- The private `Tail(DataTable, int)` helper is duplicated verbatim in `OrderEntry.aspx.cs` — a migration consolidation candidate

---

### Customer Credit File Page

**Type**: Frontend Component + Backend Component

**Location**:
- **Frontend**: `/Customers.aspx`
- **Backend**: `/Customers.aspx.cs`

**Purpose**: Lists all customer credit profiles in a grid and provides a single per-row action — "Toggle manual override" — which flips the `ManualOverride` boolean for the selected customer, writes an audit record, and persists the change. Does not unblock blocked customers or alter any other field.

**Key Files**:
1. `Customers.aspx` — Page markup; defines `<asp:GridView ID="CustomerGrid">` with explicit bound columns (`Id`, `Name`, `Country`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Blocked`, `ManualOverride`) and a `ButtonField` with `CommandName="ToggleOverride"`; includes an `<asp:Label ID="Message">` for feedback text
2. `Customers.aspx.cs` — Code-behind class `CustomersPage`; `Page_Load` binds the grid on first load via `BindGrid()`; `CustomerGrid_RowCommand` handles the toggle: reads the customer ID from the clicked row's first cell, loads the `DataSet`, flips `ManualOverride`, writes an audit entry, saves, and refreshes the grid

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository.Load()`, `LegacyRepository.FindCustomer()`, `LegacyRepository.AddAudit()`, `LegacyRepository.Save()`)
- **Internal Submodules**: Master Page Layout and Navigation (inherits `Site.master`)
- **Libraries**: `System.Data`, `System.Web.UI.WebControls`

**Related Components**:
- **Entities**: `Customers` table (all columns displayed), `Audit` table (row written on each toggle)
- **Services**: `LegacyRepository` — all persistence calls
- **APIs**: None
- **UI**: `CustomerGrid` GridView with embedded `ButtonField`, `Message` Label for operation feedback

**Integration Points**:
- The customer ID for the toggle is extracted from `CustomerGrid.Rows[rowIndex].Cells[0].Text` — relies on the grid column order matching the `DataField` mapping; changing column order in markup without updating the code-behind would silently break the lookup
- Every toggle produces one audit record with the action string `"ManualOverride=True"` or `"ManualOverride=False"` and actor `"USER"`
- Does NOT interact with `CreditDecisionService` or `NightlyBatch`; purely a data-edit screen

---

### Order Entry and Credit Authorization Page

**Type**: Frontend Component + Backend Component

**Location**:
- **Frontend**: `/OrderEntry.aspx`
- **Backend**: `/OrderEntry.aspx.cs`

**Purpose**: Accepts credit order parameters (customer, amount, payment method, priority), validates them, invokes the Credit Decision Engine, persists the resulting order, and displays the decision outcome. Contains a commercially significant validation rule (invoice minimum of 25 EUR) that resides in the code-behind rather than in `CreditDecisionService`.

**Key Files**:
1. `OrderEntry.aspx` — Page markup; `<asp:DropDownList ID="CustomerId">` populated server-side, `<asp:TextBox ID="Amount">` (default `250`), `<asp:DropDownList ID="Payment">` (INVOICE/TRANSFER/CARD), `<asp:DropDownList ID="Priority">` (NORMAL/URGENT), a submit button, an `<asp:Literal ID="Result">` for the styled decision panel, and `<asp:GridView ID="RecentOrdersGrid">` for the last 10 orders
2. `OrderEntry.aspx.cs` — Code-behind class `OrderEntryPage`; `Page_Load` populates the customer dropdown on first load; `Submit_Click` validates amount (parse + positive check), applies the 25 EUR/INVOICE minimum guard, calls `CreditDecisionService.Evaluate()`, builds a new `Orders` row, writes an audit record, persists via `LegacyRepository.Save()`, and renders the color-coded decision result

**Dependencies**:
- **External Modules**: Credit Decision Engine (`CreditDecisionService.Evaluate()`)
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository.Load()`, `LegacyRepository.FindCustomer()`, `LegacyRepository.AddAudit()`, `LegacyRepository.Save()`)
- **Internal Submodules**: Master Page Layout and Navigation (inherits `Site.master`)
- **Libraries**: `System.Data`, `System.Globalization`, `System.Web.UI.WebControls`

**Related Components**:
- **Entities**: `Customers` table (dropdown source, customer lookup), `Orders` table (new row inserted), `Audit` table (decision record written)
- **Services**: `CreditDecisionService.Evaluate()` — core decision; `LegacyRepository` — persistence
- **APIs**: None
- **UI**: `CustomerId` DropDownList, `Amount` TextBox, `Payment` DropDownList, `Priority` DropDownList, `Result` Literal (decision panel with CSS class `ok`/`warn`/`bad`), `RecentOrdersGrid` GridView

**Integration Points**:
- **Migration risk**: The `amount < 25m && Payment == "INVOICE"` guard is in the code-behind, not in `CreditDecisionService`. Any migration that ports only the service class will silently drop this business rule.
- Order IDs are generated as `"ORD-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")` — no sequence/UUID; collisions theoretically possible under concurrent submissions in the same millisecond
- Amount parsing uses `CultureInfo.InvariantCulture` and normalises comma to period, making the form locale-agnostic
- The `Tail()` private helper is identical to the one in `Default.aspx.cs` — duplicated utility logic

---

### Batch Monitor and Manual Trigger Page

**Type**: Frontend Component + Backend Component

**Location**:
- **Frontend**: `/Batch.aspx`
- **Backend**: `/Batch.aspx.cs`

**Purpose**: Displays live batch execution status (last run timestamp, execution count, last message) from `LegacyState` and renders a table of per-customer runtime flags. Provides a button to force an immediate batch run via `NightlyBatch.RunNow()`. Auto-refreshes the page every 10 seconds via inline JavaScript.

**Key Files**:
1. `Batch.aspx` — Page markup; three `<asp:Label>` controls (`LastRun`, `Executions`, `BatchMessage`), a submit button (`Run batch now`), an `<asp:Literal ID="Flags">` for the runtime-flags table, and an inline `<script>` with a `setTimeout` 10-second page reload
2. `Batch.aspx.cs` — Code-behind class `BatchPage`; `Page_Load` calls `Refresh()` on non-postback; `Run_Click` calls `NightlyBatch.RunNow()` then `Refresh()`; `Refresh()` reads `LegacyState` properties and iterates `LegacyState.CustomerFlags` under a `lock(LegacyState.SyncRoot)` to build the flags HTML table

**Dependencies**:
- **External Modules**: Nightly Batch Processing (`NightlyBatch.RunNow()`)
- **External Modules**: Global In-Memory State (`LegacyState.LastBatchRun`, `LegacyState.BatchExecutions`, `LegacyState.LastBatchMessage`, `LegacyState.CustomerFlags`, `LegacyState.SyncRoot`)
- **Internal Submodules**: Master Page Layout and Navigation (inherits `Site.master`)
- **Libraries**: `System.Collections`, `System.Text`, `System.Web.UI.WebControls`

**Related Components**:
- **Entities**: No direct database access; reads exclusively from `LegacyState` in-memory store
- **Services**: `NightlyBatch.RunNow()` — on-demand batch execution; `LegacyState` — state source
- **APIs**: None
- **UI**: `LastRun` Label, `Executions` Label, `BatchMessage` Label, `Run` Button, `Flags` Literal (HTML table of per-customer flags), inline JavaScript auto-refresh

**Integration Points**:
- Only page that calls `NightlyBatch.RunNow()` directly from a user action
- Locks `LegacyState.SyncRoot` when reading `CustomerFlags` to avoid torn reads while the batch timer fires concurrently
- The 10-second auto-refresh is pure client-side (`window.location.reload()`) — no SignalR or polling API; each reload is a full page round-trip
- Does not write to the database or mutate any state itself; all side-effects are in `NightlyBatch`

---

### Application Stylesheet

**Type**: Utility

**Location**:
- **Styles**: `/Content/site.css`

**Purpose**: Single-file stylesheet that defines the complete visual identity of the application. Covers layout (centered `.wrap` container, top banner, navigation bar), typography, form-field layout (`.field` rows with fixed-width labels), decision-result color coding (`.ok` green, `.warn` amber, `.bad` red), grid table styles, KPI tiles, and utility classes. No JavaScript framework or build step; plain CSS loaded directly by `Site.master`.

**Key Files**:
1. `Content/site.css` — Minified single-line CSS; contains all rules for: `body`, `.top`, `.nav`, `.wrap`, `.box`, `.ok`, `.warn`, `.bad`, `.grid`, `.field`, `.btn`, `.result`, `.small`, `.kpi`

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Master Page Layout and Navigation (consumed via `<link>` in `Site.master`)
- **Libraries**: None (plain CSS; no preprocessor)

**Related Components**:
- **Entities**: None
- **Services**: None
- **APIs**: None
- **UI**: All four application pages via `Site.master`; CSS class names `ok`/`warn`/`bad` are referenced by string concatenation in `OrderEntry.aspx.cs`

**Integration Points**:
- The CSS class names `.ok`, `.warn`, and `.bad` are hardcoded as string literals in `OrderEntry.aspx.cs` (`decision.Status == "APPROVED" ? "ok" : ...`). Renaming these classes requires a coordinated change in both files.
- No responsive design; the `.wrap` container has a fixed width of `1050px`

---

## Relationship Map

### Data Flow

```text
[Browser Request]
      |
      v
[Site.master]  ←─ Content/site.css
      |
      ├──→ [Default.aspx / Default.aspx.cs]
      │         └──→ LegacyRepository.Load() ──→ [DataSet: Customers, Orders, Audit]
      │
      ├──→ [Customers.aspx / Customers.aspx.cs]
      │         └──→ LegacyRepository.Load / FindCustomer / AddAudit / Save
      │                   └──→ [DataSet: Customers, Audit]
      │
      ├──→ [OrderEntry.aspx / OrderEntry.aspx.cs]
      │         ├──→ LegacyRepository.Load / FindCustomer / AddAudit / Save
      │         └──→ CreditDecisionService.Evaluate()
      │                   └──→ [DataSet: Customers, Orders, Audit]
      │
      └──→ [Batch.aspx / Batch.aspx.cs]
                ├──→ LegacyState (read: LastBatchRun, BatchExecutions, CustomerFlags)
                └──→ NightlyBatch.RunNow() (on button click)
```

### Call Hierarchy
- `Default.aspx.cs` calls `LegacyRepository.Load()`
- `Customers.aspx.cs` calls `LegacyRepository.Load()`, `FindCustomer()`, `AddAudit()`, `Save()`
- `OrderEntry.aspx.cs` calls `LegacyRepository.Load()`, `FindCustomer()`, `AddAudit()`, `Save()`
- `OrderEntry.aspx.cs` calls `CreditDecisionService.Evaluate()`
- `Batch.aspx.cs` calls `NightlyBatch.RunNow()`
- `Batch.aspx.cs` reads `LegacyState.*` properties

### UI-Backend Mapping
| UI Component | Backend Service | DB Table |
|---|---|---|
| `Default.aspx` — KPI tiles | `LegacyRepository.Load()` | `Customers`, `Orders` |
| `Default.aspx` — OrdersGrid | `LegacyRepository.Load()` | `Orders` |
| `Default.aspx` — AuditGrid | `LegacyRepository.Load()` | `Audit` |
| `Customers.aspx` — CustomerGrid | `LegacyRepository.Load()` | `Customers` |
| `Customers.aspx` — Toggle button | `LegacyRepository.FindCustomer / AddAudit / Save` | `Customers`, `Audit` |
| `OrderEntry.aspx` — Submit button | `CreditDecisionService.Evaluate`, `LegacyRepository.*` | `Customers`, `Orders`, `Audit` |
| `OrderEntry.aspx` — RecentOrdersGrid | `LegacyRepository.Load()` | `Orders` |
| `Batch.aspx` — Status labels | `LegacyState.*` | None (in-memory) |
| `Batch.aspx` — Run button | `NightlyBatch.RunNow()` | `Customers`, `Audit` (via batch) |

---

## Completeness Verification

### File Statistics
- Total backend files in module directory: 4 (`Default.aspx.cs`, `Customers.aspx.cs`, `OrderEntry.aspx.cs`, `Batch.aspx.cs`) — Documented: 4
- Total frontend files: 5 (`Site.master`, `Default.aspx`, `Customers.aspx`, `OrderEntry.aspx`, `Batch.aspx`) — Documented: 5
- Total JavaScript files: 0 standalone (one inline `<script>` block in `Batch.aspx`) — Documented: 1 (noted within UI_BATCH submodule)
- Total CSS files: 1 (`Content/site.css`) — Documented: 1
- Total SQL scripts: 0
- **Coverage**: 100%

### Directories Analyzed
- [x] `/` (root) — 9 files (`Site.master`, `Default.aspx`, `Default.aspx.cs`, `Customers.aspx`, `Customers.aspx.cs`, `OrderEntry.aspx`, `OrderEntry.aspx.cs`, `Batch.aspx`, `Batch.aspx.cs`)
- [x] `/Content/` — 1 file (`site.css`)

---

## Technical Notes
- There are no standalone `.js` files; the only JavaScript is the 10-second auto-refresh `setTimeout` inline in `Batch.aspx`.
- The `Tail(DataTable, int)` private method is duplicated identically in both `Default.aspx.cs` and `OrderEntry.aspx.cs`. In the current project structure (flat `App_Code` compilation), this could trivially be extracted into a shared utility class; it is a notable consolidation target in any migration.
- `OrderEntry.aspx.cs` contains the 25 EUR/INVOICE minimum validation business rule. This rule is not present in `CreditDecisionService.cs` and is not reflected in the `docs/business-rules.md` notes — it is an intentional distribution of business logic across the UI layer, documented as a migration challenge.
- All pages use `AutoEventWireup="true"` — the ASP.NET runtime auto-wires `Page_Load` and event handlers by naming convention. This is standard for Web Forms but creates implicit coupling between method names and the runtime.
- The application has no client-side form validation; all validation is server-side in code-behind.
- `Site.master` wraps all page content in a single `<form runat="server">`, which means button clicks anywhere in the application trigger a full-page postback.

---

## Complexity and Prioritization

### Submodule Complexity
- **High**: Order Entry and Credit Authorization Page — integrates amount validation (including the out-of-place 25 EUR rule), `CreditDecisionService`, order ID generation, result rendering, and a duplicated `Tail()` helper; highest migration risk
- **Medium**: Customer Credit File Page — row-command handler with grid-cell index coupling; write path with audit; medium complexity
- **Medium**: Batch Monitor and Manual Trigger Page — thread-safe state reads, manual batch trigger, client-side auto-refresh; medium complexity
- **Low**: Operations Dashboard Page — read-only aggregation; straightforward but contains duplicated `Tail()` helper
- **Low**: Master Page Layout and Navigation — static HTML wrapper; minimal logic
- **Low**: Application Stylesheet — plain CSS; no build tooling or complexity

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analyzed in full detail to create the complete functional specification.
