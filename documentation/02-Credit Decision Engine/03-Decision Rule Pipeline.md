# Functional Specification: Decision Rule Pipeline

**Parent Module**: Credit Decision Engine | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Evaluates an ordered sequence of business rules against the adjusted risk score and computed credit exposure to produce a final credit decision for an incoming order.
- **Key Functionality**:
  - Applies 10 sequential decision rules using short-circuit evaluation (first match wins)
  - Produces one of three outcomes: `APPROVED`, `REVIEW`, or `BLOCKED`
  - Each outcome carries a typed decision code and a human-readable explanation
  - Enforces hard-block rules unconditionally, even when a commercial override is active
  - Applies a hidden long-seniority tolerance rule before the standard approval path
- **Users**: Invoked programmatically — no direct user interaction. The result is consumed by `OrderEntry.aspx.cs` and displayed to the credit operator.

---

## 2. Business Logic

### Key Processes

**Credit Decision Evaluation**: Receives pre-adjusted risk score, computed exposure, and resolved configuration thresholds; evaluates rules in strict order and returns the first match.

1. Hard-block rules checked first (unconditional or near-unconditional)
2. Human-review rules checked second
3. Long-seniority tolerance approval rule
4. Standard within-limits approval rule
5. Manual commercial override approval rule
6. Catch-all review if no prior rule matched

**Override Asymmetry** (`CreditDecisionService.cs:53–54`): The `ManualOverride` flag bypasses the `B01` (blocked flag) rule and exposure/risk review rules, but does **not** bypass `B90` (hard overdue ≥ configured threshold). This asymmetry is documented only in a code comment and is historically lost during migrations.

**Exposure Computation** (`CreditDecisionService.cs:45`): Total exposure = `OutstandingDebt` + `SumOpenOrders` + `orderAmount`. This combined figure is compared against multiples of `CreditLimit` across several rules.

### Decision Rules (in evaluation order)

| # | Code | Status | Condition | Override Bypasses? |
|---|------|--------|-----------|-------------------|
| 1 | `B01` | BLOCKED | `customer.Blocked == true` AND `ManualOverride == false` | Yes |
| 2 | `B90` | BLOCKED | `overdueDays >= HardBlockOverdueDays` (default 90) | **No** |
| 3 | `B35` | BLOCKED | `exposure > creditLimit × 1.35` AND `ManualOverride == false` | Yes |
| 4 | `R72` | REVIEW | `effectiveRisk >= HighRiskScore` (default 72) AND `ManualOverride == false` | Yes |
| 5 | `R11` | REVIEW | `exposure > creditLimit` AND `paymentMethod == INVOICE` AND `ManualOverride == false` | Yes |
| 6 | `R75` | REVIEW | `amount >= ManualReviewAmount` (default 7500) AND `paymentMethod != CARD` | **No** |
| 7 | `R20` | REVIEW | `priority == URGENT` AND `overdueDays > 20` | **No** |
| 8 | `A15` | APPROVED | `customerSeniority >= 15 years` AND `exposure <= creditLimit × 1.10` AND `effectiveRisk < 65` | N/A |
| 9 | `A00` | APPROVED | `exposure <= creditLimit` AND `effectiveRisk < HighRiskScore` | N/A |
| 10 | `AOV` | APPROVED | `ManualOverride == true` AND `exposure <= creditLimit × 1.50` | N/A |
| 11 | `R99` | REVIEW | Catch-all — no prior rule matched | N/A |

### Critical Validations

| Field | Rule | Impact |
|-------|------|--------|
| `overdueDays` | `>= HardBlockOverdueDays` triggers `B90` regardless of override | ManualOverride ineffective |
| `exposure` | Compared against `creditLimit × 1.35 / 1.10 / 1.00 / 1.50` depending on rule | Multiple thresholds |
| `effectiveRisk` | Compared against `HighRiskScore` (R72) and hardcoded `65` (A15) | Separate thresholds per path |
| `customerSeniority` | `>= 15 years` unlocks A15 tolerance rule | Undocumented in business-rules.md |
| `paymentMethod` | `INVOICE` triggers additional exposure check (R11); `CARD` exempts from R75 | Method-specific branching |
| `priority` | `URGENT` + `overdueDays > 20` triggers R20 | Urgency interacts with overdue |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `CreditDecisionService.Evaluate` (`CreditDecisionService.cs:17`) | Main entry point for all credit decisions | Receives DataSet + customer row + order params; runs risk adjustment → config resolution → rule pipeline; returns `CreditDecision` |
| Decision Rule Pipeline (`CreditDecisionService.cs:54–82`) | Ordered rule evaluator | 10 rules + catch-all; short-circuit on first match; populates `CreditDecision` via `Fill` helper |
| `Fill` helper (`CreditDecisionService.cs:85–89`) | Result constructor | Sets `Status`, `Code`, `Explanation` on the shared `CreditDecision` object and returns it |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `OrderEntry.aspx.cs:Submit_Click` | Invokes the pipeline and handles output | Calls `Evaluate`, persists order row, writes audit record, renders HTML result to operator |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `Customers` (SQLite) | Source of customer risk attributes consumed by rules | `Blocked`, `ManualOverride`, `OverdueDays`, `CreditLimit`, `Since`, `RiskScore`, `OutstandingDebt` |
| `Orders` (SQLite) | Source of open-order exposure (aggregated) | Aggregated via `LegacyRepository.SumOpenOrders` |
| `CreditDecision` POCO (`CreditDecisionService.cs:6–13`) | Carries pipeline output | `Status`, `Code`, `Explanation`, `EffectiveRisk`, `ExposureAfterOrder` |

---

## 4. APIs and Endpoints

### Main Entry Point

| Method | Signature | Purpose | Auth |
|--------|-----------|---------|------|
| Static method | `CreditDecisionService.Evaluate(DataSet, DataRow, decimal, string, string)` | Evaluate credit decision for one order | Internal (no HTTP auth) |

The pipeline has no HTTP endpoint of its own. It is triggered by `OrderEntry.aspx` via a Web Forms POST.

**Input Parameters**:
```
ds            — full DataSet (loaded from SQLite by caller)
customer      — DataRow from Customers table
amount        — order amount (decimal, EUR)
paymentMethod — "CARD" | "INVOICE" | "TRANSFER"
priority      — "NORMAL" | "URGENT"
```

**Output — CreditDecision**:
```
Status            — "APPROVED" | "REVIEW" | "BLOCKED"
Code              — decision code (e.g. "A00", "B90", "R72")
Explanation       — human-readable reason text
EffectiveRisk     — adjusted risk score after all modifications [0–100]
ExposureAfterOrder — total exposure including this order (EUR)
```

---

## 5. Data Flows

### Main Flow: Credit Decision Evaluation

```
Operator (browser)
  → HTTP POST (OrderEntry.aspx)
    → Input validation + invoice-minimum guard (< 25 EUR + INVOICE → reject before Evaluate)
    → LegacyRepository.Load() — loads DataSet from SQLite
    → CreditDecisionService.Evaluate(ds, customer, amount, paymentMethod, priority)
        → Risk Score Computation (adjustments applied to base RiskScore)
        → Configuration Parameter Resolver (reads HardBlockOverdueDays, HighRiskScore, ManualReviewAmount)
        → Decision Rule Pipeline (rules 1–11, first match short-circuits)
        → returns CreditDecision
    → LegacyRepository.Save() — inserts Orders row
    → LegacyRepository.AddAudit() — writes audit record
  → HTML result rendered to operator
```

**Summary**:
1. Order submitted via web form with customer, amount, payment method, priority
2. Pre-flight invoice-minimum check runs in code-behind (outside the engine)
3. Risk score adjusted and configuration thresholds resolved
4. Decision rules evaluated in fixed order; first match returns immediately
5. Decision persisted and displayed to operator

### Error Handling

| Error | Cause | Response |
|-------|-------|----------|
| Invalid amount | Non-numeric or ≤ 0 input | Reject before `Evaluate` is called; display "Invalid amount" |
| Customer not found | `CustomerId` not in DataSet | Reject before `Evaluate`; display "Customer not found" |
| Invoice minimum violation | Amount < 25 EUR with INVOICE method | Reject before `Evaluate`; display warning (not a decision code) |
| No rule matched | All 10 rules unevaluated without return | `R99` catch-all returned (rare in practice) |
| Config key missing/unparseable | `web.config` key absent or non-numeric | `ConfigInt`/`ConfigDecimal` returns hardcoded default silently |

---

## 6. Integrations

### Internal

- **Risk Score Computation** (`CreditDecisionService.cs:20–43`): Runs immediately before the pipeline; produces the `effectiveRisk` integer [0–100] consumed by rules R72, A15, and A00.
- **Configuration Parameter Resolver** (`CreditDecisionService.cs:49–51`): Provides `hardOverdue`, `highRisk`, `manualReviewAmount` thresholds used in rules B90, R72, R75.
- **Data Access and Persistence Layer** (`LegacyRepository`): `SumOpenOrders` and `CountRecentOrders` supply live-query inputs used in exposure computation and risk adjustment respectively. Note: these are direct SQLite queries, not reads from the in-memory DataSet — a documented mixed-access anomaly.
- **Order Entry Invoice Minimum Validation** (`OrderEntry.aspx.cs:42–46`): Pre-call guard that rejects sub-25-EUR INVOICE orders before `Evaluate` is ever invoked; this boundary condition is invisible to the pipeline itself.
- **Credit Decision Result Model** (`CreditDecision` POCO): Mutable result object instantiated at the start of `Evaluate` and populated by `Fill` before returning.

### External

- None. The pipeline has no outbound calls to external systems or services.

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `HardBlockOverdueDays` | Overdue-days threshold for unconditional B90 block | `90` |
| `HighRiskScore` | Effective risk threshold for R72 review and A00 approval boundary | `72` |
| `ManualReviewAmount` | Order amount (EUR) above which non-card orders require review (R75) | `7500` |
| `LegacyCountrySurcharge` | Comma-separated country codes receiving +7 risk penalty | `PT,IT,GR` |

All keys are read from `web.config` `appSettings`. If a key is absent or unparseable, the hardcoded default is used without logging.

---

## 8. Summary

- **Components**: 3 backend (Evaluate method, rule pipeline block, Fill helper), 1 frontend (OrderEntry code-behind), 3 data entities (Customers, Orders, CreditDecision POCO)
- **Endpoints**: 0 HTTP endpoints — pipeline is invoked as a static method
- **Business rules**: 11 (3 BLOCKED, 4 REVIEW, 3 APPROVED, 1 catch-all)
- **Integrations**: 5 internal, 0 external

### Migration Risk Notes

- The `B90` rule is unconditional — `ManualOverride` does NOT bypass it. This asymmetry is documented only in a code comment (`CreditDecisionService.cs:53`).
- Rule ordering is semantically significant. Changing sequence changes outcomes.
- The `R99` catch-all is reachable only if ManualOverride is true but `exposure > creditLimit × 1.50`. It is extremely rare in production data.
- The `A15` seniority tolerance rule (≥ 15 years, exposure ≤ 110 % of limit, risk < 65) is absent from `docs/business-rules.md` and must be migrated from code.
- The 25 EUR invoice minimum lives in `OrderEntry.aspx.cs`, not in this pipeline — a documented migration challenge.

---
**Note**: Document kept under 1500 lines. All content derived exclusively from codebase analysis.
