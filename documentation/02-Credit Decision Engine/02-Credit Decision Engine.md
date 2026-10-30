# Submodules of Module: Credit Decision Engine

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 2
**Analysis Date**: 2026-08-10
**Parent Module**: Credit Decision Engine

## Summary
Total submodules identified: **5**

### Distribution by Type
- Backend Components: 4
- Frontend Components: 1
- Data Layer: 0
- API/Services: 0
- Configuration: 0
- Utilities: 0

## Submodule Index
1. [Credit Decision Result Model](#credit-decision-result-model) - Backend Component
2. [Risk Score Computation](#risk-score-computation) - Backend Component
3. [Decision Rule Pipeline](#decision-rule-pipeline) - Backend Component
4. [Configuration Parameter Resolver](#configuration-parameter-resolver) - Backend Component
5. [Order Entry Invoice Minimum Validation](#order-entry-invoice-minimum-validation) - Frontend Component

---

## Submodule Detail

### Credit Decision Result Model

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/CreditDecisionService.cs` — class `CreditDecision` (lines 6–13)

**Purpose**: Plain data-transfer object that carries the full result of a credit evaluation: the approval status (`APPROVED`, `REVIEW`, or `BLOCKED`), the decision code (e.g. `A00`, `B01`, `R72`), the human-readable explanation text, the final effective risk score after all adjustments, and the computed total exposure after the prospective order is included.

**Key Files**:
1. `CreditDecisionService.cs` — defines the `CreditDecision` class used as the return value of `CreditDecisionService.Evaluate`

**Dependencies**:
- **External Modules**: None
- **Internal Submodules**: Decision Rule Pipeline (producer), Risk Score Computation (writes `EffectiveRisk`)
- **Libraries**: None (POCO class with no base class or interfaces)

**Related Components**:
- **Entities**: `Orders` table — `Status`, `DecisionCode`, `DecisionText` columns are populated from this object
- **Services**: `CreditDecisionService.Evaluate` — creates and returns an instance
- **APIs**: None
- **UI**: `OrderEntry.aspx.cs` — reads all five fields to build the result HTML and the order row

**Integration Points**:
- Returned by `CreditDecisionService.Evaluate` and consumed immediately by `OrderEntry.aspx.cs:Submit_Click` (lines 48–66)
- `EffectiveRisk` and `ExposureAfterOrder` are written to the audit trail detail string

---

### Risk Score Computation

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/CreditDecisionService.cs` — inside `CreditDecisionService.Evaluate`, lines 20–43

**Purpose**: Applies a sequential series of additive and subtractive adjustments to the customer's base risk score before any decision rule is evaluated. Adjustments cover country surcharge, recent-order frequency, debt-to-limit ratio, customer seniority, payment method, order priority, and order amount. The result is clamped to [0, 100].

**Key Files**:
1. `CreditDecisionService.cs` — contains the adjustment logic (lines 33–43) and the private helper `IsSurchargeCountry` (lines 91–98)

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer — `LegacyRepository.SumOpenOrders` and `LegacyRepository.CountRecentOrders` provide the live query inputs (lines 29–30)
- **Internal Submodules**: Configuration Parameter Resolver — not used directly here; thresholds are inline literals at this stage
- **Libraries**: `System.Configuration.ConfigurationManager` (via `IsSurchargeCountry` for the country surcharge list), `System`

**Related Components**:
- **Entities**: `Customers` table columns: `RiskScore`, `OutstandingDebt`, `CreditLimit`, `OverdueDays`, `Country`, `Since`, `ManualOverride`; `Orders` table (aggregated via `SumOpenOrders` / `CountRecentOrders`)
- **Services**: None beyond the repository helpers
- **APIs**: None
- **UI**: None

**Integration Points**:
- Runs unconditionally at the start of every `Evaluate` call before any decision rule tests
- The adjusted `risk` integer is consumed in-place by the Decision Rule Pipeline in the same method scope
- `IsSurchargeCountry` reads `web.config` key `LegacyCountrySurcharge` (comma-separated country codes; default `PT,IT,GR`)

---

### Decision Rule Pipeline

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/CreditDecisionService.cs` — inside `CreditDecisionService.Evaluate`, lines 44–82; private helper `Fill` at lines 85–89

**Purpose**: Evaluates an ordered sequence of business rules against the adjusted risk score and computed exposure. Rules short-circuit: the first rule that matches immediately returns a `CreditDecision` and no subsequent rules are checked. The pipeline produces one of three outcomes — `BLOCKED` (hard stop), `REVIEW` (requires human action), or `APPROVED` (automatic approval). Rule ordering is semantically significant and not negotiable.

**Key Files**:
1. `CreditDecisionService.cs` — contains the full rule sequence (lines 54–82) and the `Fill` helper (lines 85–89)

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer — exposure inputs (`openOrders`) computed from repository queries
- **Internal Submodules**: Risk Score Computation (consumes the adjusted `risk` value and `exposure` computed at lines 45–46), Configuration Parameter Resolver (reads `hardOverdue`, `highRisk`, `manualReviewAmount` at lines 49–51)
- **Libraries**: `System` (`DateTime.Now` for seniority check on line 72)

**Related Components**:
- **Entities**: `Customers` table — `Blocked`, `ManualOverride`, `OverdueDays`, `CreditLimit`, `Since`; `Orders` table — aggregated `openOrders`
- **Services**: None beyond the helpers within this file
- **APIs**: None
- **UI**: `OrderEntry.aspx.cs` — calls `Evaluate` and receives the result; also contains the pre-call invoice-minimum check that is logically part of the same decision flow

**Integration Points**:
- Sole callable entry point is the `public static CreditDecision Evaluate(DataSet, DataRow, decimal, string, string)` method (line 17)
- Decision codes produced: `B01`, `B90`, `B35` (BLOCKED); `R72`, `R11`, `R75`, `R20`, `R99` (REVIEW); `A15`, `A00`, `AOV` (APPROVED)
- The `manualOverride` flag does NOT bypass the `B90` (hard overdue) rule — a historically significant exception documented only in a code comment (line 53)

---

### Configuration Parameter Resolver

**Type**: Backend Component

**Location**:
- **Backend**: `/App_Code/CreditDecisionService.cs` — private helpers `ConfigInt` (lines 101–105), `ConfigDecimal` (lines 107–111), and `IsSurchargeCountry` (lines 91–98)

**Purpose**: Provides type-safe, fallback-capable accessors for the four `web.config` `appSettings` keys that control decision thresholds (`HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`) and the country surcharge list (`LegacyCountrySurcharge`). Returns hardcoded defaults when the key is absent or unparseable, making the engine resilient to configuration omissions.

**Key Files**:
1. `CreditDecisionService.cs` — contains all three helper methods
2. `web.config` — defines the four keys consumed: `HighRiskScore` (default 72), `ManualReviewAmount` (default 7500), `HardBlockOverdueDays` (default 90), `LegacyCountrySurcharge` (default `PT,IT,GR`)

**Dependencies**:
- **External Modules**: Infrastructure, Configuration and Scripts (`web.config`)
- **Internal Submodules**: Decision Rule Pipeline (consumer of the resolved values)
- **Libraries**: `System.Configuration.ConfigurationManager`

**Related Components**:
- **Entities**: None
- **Services**: None
- **APIs**: None
- **UI**: None

**Integration Points**:
- Called within `Evaluate` at lines 49–51 to resolve thresholds immediately after risk score computation
- `IsSurchargeCountry` is also called from within the Risk Score Computation step (line 33), making it shared across two submodules in the same method

---

### Order Entry Invoice Minimum Validation

**Type**: Frontend Component

**Location**:
- **Frontend**: `/OrderEntry.aspx` — form markup (lines 1–13)
- **Backend**: `/OrderEntry.aspx.cs` — `Submit_Click` handler (lines 23–68), specifically the invoice-minimum guard at lines 42–46

**Purpose**: Web Forms page that collects order input (customer, amount, payment method, priority), enforces a hard commercial rule — orders below 25 EUR cannot use the INVOICE payment method — and then delegates to `CreditDecisionService.Evaluate`. This validation rule lives deliberately outside `CreditDecisionService`, representing a historically misplaced business rule that is a documented migration challenge.

**Key Files**:
1. `OrderEntry.aspx` — ASP.NET markup; defines `CustomerId` dropdown, `Amount` text box, `Payment` dropdown, `Priority` dropdown, `Submit` button, `Result` literal, and `RecentOrdersGrid`
2. `OrderEntry.aspx.cs` — code-behind; `Page_Load` populates the customer list; `Submit_Click` validates input, applies the 25 EUR minimum, calls the decision engine, persists the order, and renders the result; `BindRecentOrders` / `Tail` support the recent-orders grid

**Dependencies**:
- **External Modules**: Credit Decision Engine — `CreditDecisionService.Evaluate` (line 48)
- **External Modules**: Data Access and Persistence Layer — `LegacyRepository.Load`, `FindCustomer`, `AddAudit`, `Save` (lines 12, 33, 34, 61, 62)
- **Internal Submodules**: None
- **Libraries**: `System.Web.UI.WebControls`, `System.Globalization`, `System`

**Related Components**:
- **Entities**: `Customers` table (dropdown population, customer lookup); `Orders` table (new row insertion, recent orders grid); `Audit` table (audit record insertion)
- **Services**: `CreditDecisionService.Evaluate`
- **APIs**: None
- **UI**: `Site.master` (master page layout); `Content/site.css` (CSS classes `field`, `btn`, `grid`, `result ok/bad/warn`, `small`)

**Integration Points**:
- Entry point for all order-creation operations; no other page or service creates `Orders` rows except `NightlyBatch` (which only updates `Customers`)
- The invoice-minimum check at line 42 runs before `Evaluate`, meaning the decision engine never sees sub-25-EUR INVOICE orders — a boundary condition invisible to `CreditDecisionService` itself
- Audit trail written via `LegacyRepository.AddAudit` with type `ORDER`, subject = order ID, detail = status/code/risk/exposure

---

## Relationship Map

### Data Flow

```text
[Browser / User]
      |
      v HTTP POST
[OrderEntry.aspx / OrderEntry.aspx.cs]
      |
      | 1. Load DataSet (LegacyRepository.Load)
      | 2. Invoice-minimum guard (< 25 EUR + INVOICE → reject)
      |
      v
[CreditDecisionService.Evaluate]
      |
      |-- [Risk Score Computation]
      |        |
      |        +-- LegacyRepository.SumOpenOrders  → SQLite (Orders)
      |        +-- LegacyRepository.CountRecentOrders → SQLite (Orders)
      |        +-- IsSurchargeCountry → web.config (LegacyCountrySurcharge)
      |
      |-- [Configuration Parameter Resolver]
      |        +-- web.config (HighRiskScore, ManualReviewAmount, HardBlockOverdueDays)
      |
      |-- [Decision Rule Pipeline]
      |        +-- returns CreditDecision (Status, Code, Explanation, EffectiveRisk, ExposureAfterOrder)
      |
      v
[OrderEntry.aspx.cs]
      |-- persist Orders row (LegacyRepository.Save)
      |-- persist Audit row  (LegacyRepository.AddAudit)
      +-- render HTML result to browser
```

### Call Hierarchy
- `OrderEntry.aspx.cs:Submit_Click` calls `CreditDecisionService.Evaluate`
- `CreditDecisionService.Evaluate` calls `LegacyRepository.SumOpenOrders`
- `CreditDecisionService.Evaluate` calls `LegacyRepository.CountRecentOrders`
- `CreditDecisionService.Evaluate` calls `IsSurchargeCountry` (reads `web.config`)
- `CreditDecisionService.Evaluate` calls `ConfigInt` / `ConfigDecimal` (reads `web.config`)
- `CreditDecisionService.Evaluate` calls `Fill` (internal result constructor)
- `OrderEntry.aspx.cs:Submit_Click` calls `LegacyRepository.AddAudit`
- `OrderEntry.aspx.cs:Submit_Click` calls `LegacyRepository.Save`

### UI-Backend Mapping
| UI Component | Backend Service | DB Table |
|---|---|---|
| `OrderEntry.aspx` (Submit button) | `CreditDecisionService.Evaluate` | `Customers` (read), `Orders` (read for exposure) |
| `OrderEntry.aspx` (Submit button) | `LegacyRepository.Save` | `Orders` (insert), `Audit` (insert) |
| `OrderEntry.aspx` (Customer dropdown) | `LegacyRepository.Load` | `Customers` (read) |
| `OrderEntry.aspx` (RecentOrdersGrid) | `LegacyRepository.Load` | `Orders` (read, last 10) |

---

## Completeness Verification

### File Statistics
- Total backend files in module directory (`/App_Code/CreditDecisionService.cs`): 1 — Documented: 1
- Total frontend files (`OrderEntry.aspx`, `OrderEntry.aspx.cs`): 2 — Documented: 2
- Total JavaScript files: 0 — Documented: 0
- Total CSS files: 0 — Documented: 0 (CSS lives in `Content/site.css`, belongs to Web UI module)
- Total SQL scripts: 0 — Documented: 0
- **Coverage**: 100%

### Directories Analyzed
- [x] `/App_Code/` — 1 file (`CreditDecisionService.cs`)
- [x] `/` (root) — 2 files (`OrderEntry.aspx`, `OrderEntry.aspx.cs`) — included because `OrderEntry.aspx.cs` contains a business rule belonging to this module's decision flow

---

## Technical Notes
- The `CreditDecision` class (result model) is a public mutable POCO with no encapsulation. Any caller can mutate the returned object after `Evaluate` returns; this is a migration risk if the target system expects immutability.
- `CreditDecisionService.Evaluate` accepts a `DataSet` parameter but delegates the two live queries (`SumOpenOrders`, `CountRecentOrders`) back to SQLite rather than reading from the in-memory `DataSet`. This intentional mixed-access pattern is a documented legacy debt that can cause stale-read anomalies under concurrent load.
- Rule short-circuiting means later rules are unreachable when an earlier one matches. The final catch-all `R99` rule is only reached if the manual-override path at line 79 also fails. This makes the `R99` code extremely rare in practice.
- The `manualOverride` flag bypasses `B01` (blocked flag) and exposure checks, but the `B90` (hard overdue ≥ 90 days) rule at line 56 is unconditional — `manualOverride` has no effect on it. This asymmetry is documented only in a code comment and has historically been lost during migrations.
- `IsSurchargeCountry` performs a case-insensitive linear scan of the comma-separated config value on every call. With the current 3-country list this is negligible; a large list would make it measurable.

---

## Complexity and Prioritization

### Submodule Complexity
- **High**: Decision Rule Pipeline — 10 distinct rules with order-sensitive short-circuit logic; two hidden invariants (override does not bypass `B90`; seniority tolerance at line 73); catch-all `R99` path
- **High**: Risk Score Computation — 9 sequential adjustments, mixed in-memory/live-SQL inputs, country-config dependency; semantics must be replicated exactly in migration target
- **Medium**: Order Entry Invoice Minimum Validation — straightforward guard but its placement outside the engine is architecturally significant for migration scope
- **Low**: Configuration Parameter Resolver — simple config-read helpers with safe fallbacks; critical only if defaults diverge from target system configuration
- **Low**: Credit Decision Result Model — trivial POCO; migration concern is mutability and field naming

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analyzed in full detail to create the complete functional specification.
