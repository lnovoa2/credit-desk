# Functional Specification: Order Entry Invoice Minimum Validation

**Parent Module**: Credit Decision Engine | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Web Forms page that is the sole entry point for order creation. It collects order data, enforces a hard commercial rule (orders below 25 EUR cannot use INVOICE payment method), delegates to the Credit Decision Engine, persists the resulting order and audit record, and renders the decision outcome to the user.
- **Key Functionality**:
  - Collects order input: customer, amount, payment method, priority
  - Enforces the invoice minimum rule (< 25 EUR + INVOICE → immediate rejection) **before** calling the credit engine
  - Calls `CreditDecisionService.Evaluate` and persists the result as an `Orders` row
  - Writes an audit trail entry for every submitted order
  - Displays the last 10 orders in a grid on every page load
- **Users**: Credit operations staff (all authenticated users of the application)

---

## 2. Business Logic

### Core Processes

**Order Submission**: User selects a customer, enters an amount, chooses a payment method and priority, then clicks "Evaluate and save".
1. Parse and validate the amount (positive decimal, comma or dot separator accepted).
2. Load the customer record from the database.
3. Apply the invoice-minimum guard: if `amount < 25 EUR` AND `payment == INVOICE`, reject immediately with a warning message — no order row is created.
4. Call `CreditDecisionService.Evaluate` with the full order parameters to obtain a decision.
5. Insert the new order row into the `Orders` table with the decision result fields.
6. Write an `Audit` record containing status, decision code, effective risk, and exposure.
7. Persist all changes to SQLite via `LegacyRepository.Save`.
8. Render the decision outcome (status, code, explanation, effective risk, exposure) with colour-coded styling.

**Recent Orders Grid**: On every page load (initial and post-back), the last 10 orders are queried and bound to `RecentOrdersGrid` in descending insertion order.

### Critical Validations

| Field | Validation Rule | Error Response |
|-------|----------------|----------------|
| Amount | Must be a positive decimal (comma or dot as decimal separator) | `"Invalid amount."` (red result box) |
| Customer | Must resolve to an existing record in the `Customers` table | `"Customer not found."` (red result box) |
| Amount + Payment | `amount < 25.00 EUR` AND `Payment == "INVOICE"` → hard rejection | `"Orders below 25 EUR cannot be invoiced."` (yellow warning box) |

### Architectural Note — Intentional Misplacement

The invoice-minimum rule (`amount < 25m && Payment == "INVOICE"`) lives in `OrderEntry.aspx.cs:42–46`, **not** in `CreditDecisionService`. This is a documented historical anomaly: the rule was added directly to the UI code-behind and was never migrated into the credit engine. As a consequence:

- `CreditDecisionService.Evaluate` never receives sub-25-EUR INVOICE orders; the boundary condition is invisible to the engine.
- Any future migration that moves the engine to a new platform must explicitly carry this rule along, as it is not discoverable from the engine code alone.

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `OrderEntry.aspx.cs` — `Submit_Click` (lines 23–68) | Order submission handler | Parse amount, validate customer, apply invoice-minimum guard, call credit engine, persist order + audit, render result |
| `OrderEntry.aspx.cs` — `Page_Load` (lines 9–21) | Page initialisation | Populate customer dropdown from `Customers` table on first load; call `BindRecentOrders` on every load |
| `OrderEntry.aspx.cs` — `BindRecentOrders` / `Tail` (lines 70–83) | Recent orders display | Retrieve last 10 orders in reverse insertion order and bind to grid |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `OrderEntry.aspx` | Order entry form | Customer dropdown, amount text box (default 250), payment method dropdown (INVOICE / TRANSFER / CARD), priority dropdown (NORMAL / URGENT), submit button, result literal, recent orders grid |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `Customers` | Source for customer dropdown and credit evaluation input | `Id`, `Name`, `CreditLimit`, `OutstandingDebt`, `OverdueDays`, `RiskScore`, `Country`, `Since`, `Blocked`, `ManualOverride` |
| `Orders` | Stores every created order with its decision result | `Id` (ORD-yyyyMMddHHmmssfff), `CustomerId`, `Amount`, `PaymentMethod`, `Priority`, `Status`, `DecisionCode`, `DecisionText`, `CreatedAt` |
| `Audit` | Immutable audit trail for all order events | `Type` = "ORDER", `Subject` = order ID, `Detail` = "STATUS/CODE; risk=N; exposure=N.NN" |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| GET | `/OrderEntry.aspx` | Load order entry form, populate dropdowns, display recent orders | Session (Web Forms) |
| POST | `/OrderEntry.aspx` | Submit order: validate, evaluate credit, persist, render result | Session (Web Forms) |

This is an ASP.NET Web Forms page; there are no REST/JSON endpoints. All interaction is synchronous HTTP POST with server-side rendering.

**POST Request (form fields)**:
```
CustomerId  = "<customer-id>"        // selected from dropdown
Amount      = "250"                  // decimal string, default 250
Payment     = "INVOICE|TRANSFER|CARD"
Priority    = "NORMAL|URGENT"
```

**POST Response (rendered HTML fragment)**:
```html
<!-- APPROVED -->
<div class='result ok'><b>APPROVED / A00</b><br/>...</div>

<!-- BLOCKED -->
<div class='result bad'><b>BLOCKED / B01</b><br/>...</div>

<!-- REVIEW -->
<div class='result warn'><b>REVIEW / R72</b><br/>...</div>

<!-- Invoice minimum violation -->
<div class='result warn'>Orders below 25 EUR cannot be invoiced.</div>
```

---

## 5. Data Flows

### Main Flow: Order Submission

```
User (Browser)
  → POST /OrderEntry.aspx
  → Submit_Click
      → Parse & validate Amount
      → LegacyRepository.Load()          [full DataSet from SQLite]
      → LegacyRepository.FindCustomer()  [lookup by CustomerId]
      → Invoice-minimum guard            [amount < 25 + INVOICE → return warning]
      → CreditDecisionService.Evaluate() [returns CreditDecision]
      → ds.Tables["Orders"].NewRow()     [build order row]
      → LegacyRepository.AddAudit()      [append audit record to DataSet]
      → LegacyRepository.Save()          [flush DataSet to SQLite]
      → Render HTML result
  → Browser renders updated page with result + refreshed grid
```

**Key Steps Summary**:
1. Full `DataSet` loaded from SQLite on every POST (no caching).
2. Invoice-minimum guard runs before the credit engine — the engine never receives invalid combinations.
3. Order ID generated as `"ORD-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")` (timestamp-based, not sequential).
4. Audit detail format: `"STATUS/CODE; risk=<effectiveRisk>; exposure=<exposureAfterOrder>"`.
5. `LegacyRepository.Save` flushes all pending `DataSet` changes (order + audit) in a single call.

### Error Handling

| Error | Cause | Response |
|-------|-------|---------|
| Invalid amount | Non-numeric, empty, or zero/negative input | Red box: `"Invalid amount."` — no order created |
| Customer not found | `CustomerId` not present in `Customers` table | Red box: `"Customer not found."` — no order created |
| Invoice minimum violation | `amount < 25 EUR` AND `Payment == "INVOICE"` | Yellow box: `"Orders below 25 EUR cannot be invoiced."` — no order created, no engine call |
| Credit engine exceptions | Unhandled exceptions propagate as ASP.NET error page | No explicit try/catch in `Submit_Click` |

---

## 6. Integrations

### Internal
- **Credit Decision Engine** (`CreditDecisionService.Evaluate`): Receives the full `DataSet`, customer `DataRow`, amount, payment method, and priority. Returns a `CreditDecision` object (Status, Code, Explanation, EffectiveRisk, ExposureAfterOrder). Called only after all pre-conditions pass.
- **Data Access and Persistence Layer** (`LegacyRepository`): `Load()` hydrates the in-memory `DataSet`; `FindCustomer()` locates the customer row; `AddAudit()` appends an audit row; `Save()` flushes all changes to SQLite. Called on every POST.
- **Web UI Module** (`Site.master`, `Content/site.css`): Master page provides the navigation shell; CSS classes `result ok`, `result bad`, `result warn`, `field`, `btn`, `grid`, `small` control visual presentation.

### External
- None. This submodule has no calls to external systems, APIs, or services.

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| Invoice minimum threshold | Hard-coded in `OrderEntry.aspx.cs:42` as `25m` | 25 EUR (not configurable via `web.config`) |
| Customer dropdown default | No pre-selection; first customer in load order is shown | First record from `Customers` table |
| Amount field default | Pre-filled in `OrderEntry.aspx:5` as `Text="250"` | 250 |

> Note: Unlike the credit engine thresholds (`HighRiskScore`, `HardBlockOverdueDays`, `ManualReviewAmount`), the 25 EUR invoice minimum is a **hard-coded literal** in the code-behind. It cannot be tuned without a code change.

---

## 8. Summary

- **Components**: 1 frontend page (`OrderEntry.aspx`), 1 code-behind with 3 logical sections (`Submit_Click`, `Page_Load`, `BindRecentOrders/Tail`)
- **Endpoints**: 2 (GET — form load; POST — order submission)
- **Business rules**: 3 (amount validity, customer existence, invoice minimum 25 EUR)
- **Integrations**: 3 internal (Credit Decision Engine, Data Access Layer, Web UI), 0 external

---
**Note**: Document under 1500 lines. Essential functional information prioritised.
