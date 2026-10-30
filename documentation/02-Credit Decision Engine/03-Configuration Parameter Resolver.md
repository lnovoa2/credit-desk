# Functional Specification: Configuration Parameter Resolver

**Parent Module**: Credit Decision Engine | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides type-safe, fallback-capable accessors for the `appSettings` keys in `web.config` that govern the numeric thresholds and country lists consumed by the Credit Decision Engine. Shields the decision logic from missing or malformed configuration entries by returning hardcoded defaults whenever a key is absent or cannot be parsed.
- **Key Functionality**:
  - Read and parse integer configuration keys with a safe fallback (`ConfigInt`)
  - Read and parse decimal configuration keys with a safe fallback (`ConfigDecimal`)
  - Evaluate whether a customer's country is subject to a risk surcharge (`IsSurchargeCountry`)
  - Expose four business-critical thresholds: `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge`
  - Guarantee engine continuity even when `web.config` is partially configured or misconfigured
- **Users**: No direct user interaction — internal component invoked exclusively from `CreditDecisionService.Evaluate` during every credit evaluation.

---

## 2. Business Logic

### Key Processes

**[Integer threshold resolution]**: Reads a named `appSettings` key and converts it to `int`; returns a hardcoded default on any failure.
1. Call `ConfigurationManager.AppSettings[key]`.
2. Attempt `Int32.TryParse` on the raw string value.
3. Return the parsed integer if successful; otherwise return `fallback`.

**[Decimal threshold resolution]**: Same pattern for monetary thresholds requiring decimal precision.
1. Call `ConfigurationManager.AppSettings[key]`.
2. Attempt `Decimal.TryParse` on the raw string value.
3. Return the parsed decimal if successful; otherwise return `fallback`.

**[Country surcharge check]**: Determines whether the customer's country code is in the configured high-risk surcharge list.
1. Read `LegacyCountrySurcharge` from `appSettings`; treat missing key as empty string.
2. Split the raw value on `,` to obtain individual country codes.
3. Perform a case-insensitive linear scan; return `true` on first match, `false` if no match found.

### Critical Validations

| Field | Validation | Behavior |
|-------|------------|----------|
| `HighRiskScore` | Must be a valid `int` in `appSettings` | Falls back to `72` if absent or unparseable |
| `ManualReviewAmount` | Must be a valid `decimal` in `appSettings` | Falls back to `7500` if absent or unparseable |
| `HardBlockOverdueDays` | Must be a valid `int` in `appSettings` | Falls back to `90` if absent or unparseable |
| `LegacyCountrySurcharge` | Comma-separated country code string | Treated as empty (no surcharge countries) if key is absent |
| Country code match | Case-insensitive string comparison after `Trim()` | Leading/trailing whitespace around codes is tolerated |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `ConfigInt` (lines 101–105, `CreditDecisionService.cs`) | Resolve integer threshold from `web.config` | Parse `appSettings` key as `int`; return default on failure |
| `ConfigDecimal` (lines 107–111, `CreditDecisionService.cs`) | Resolve decimal threshold from `web.config` | Parse `appSettings` key as `decimal`; return default on failure |
| `IsSurchargeCountry` (lines 91–98, `CreditDecisionService.cs`) | Country surcharge membership test | Read `LegacyCountrySurcharge`, split on `,`, linear case-insensitive scan |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| *(none)* | This submodule has no UI surface | — |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| `web.config → appSettings` | Runtime configuration store | `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge`, `BatchIntervalSeconds` |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| *(none)* | — | This submodule exposes no HTTP endpoints | — |

> All interactions are in-process method calls within `CreditDecisionService`. There is no REST or HTTP surface for this submodule.

**Internal call examples**:
```
// Integer resolution — called at CreditDecisionService.cs:49-50
int hardOverdue = ConfigInt("HardBlockOverdueDays", 90);   // → 90 (from web.config)
int highRisk    = ConfigInt("HighRiskScore", 72);           // → 72 (from web.config)

// Decimal resolution — called at CreditDecisionService.cs:51
decimal manualReviewAmount = ConfigDecimal("ManualReviewAmount", 7500m); // → 7500

// Country surcharge check — called at CreditDecisionService.cs:33
bool surcharge = IsSurchargeCountry("PT");  // → true (PT is in default list)
bool surcharge = IsSurchargeCountry("DE");  // → false
```

---

## 5. Data Flows

### Main Flow: Threshold Resolution during Credit Evaluation

```
CreditDecisionService.Evaluate (caller)
    │
    ├─► IsSurchargeCountry(country)          [Risk Score Computation phase, line 33]
    │       └─► ConfigurationManager.AppSettings["LegacyCountrySurcharge"]
    │               → split → linear scan → true/false
    │
    ├─► ConfigInt("HardBlockOverdueDays", 90) [Decision Rule Pipeline phase, line 49]
    │       └─► ConfigurationManager.AppSettings["HardBlockOverdueDays"]
    │               → Int32.TryParse → int (or default 90)
    │
    ├─► ConfigInt("HighRiskScore", 72)        [line 50]
    │       └─► ConfigurationManager.AppSettings["HighRiskScore"]
    │               → Int32.TryParse → int (or default 72)
    │
    └─► ConfigDecimal("ManualReviewAmount", 7500m) [line 51]
            └─► ConfigurationManager.AppSettings["ManualReviewAmount"]
                    → Decimal.TryParse → decimal (or default 7500)
```

**Steps**:
1. `IsSurchargeCountry` is called first, during the risk-score adjustment phase, to determine whether to add +7 to the customer's risk score.
2. The three threshold helpers (`ConfigInt` × 2, `ConfigDecimal` × 1) are called immediately after risk score computation, before any decision rule is evaluated.
3. The resolved values (`hardOverdue`, `highRisk`, `manualReviewAmount`) are held as local variables and used directly within the rule pipeline.
4. No resolved value is cached between requests — each `Evaluate` call re-reads `web.config` via `ConfigurationManager.AppSettings`.
5. Results feed directly into decision rules `B90`, `R72`, and `R75`.

### Error Handling

| Error | Cause | Action |
|-------|-------|--------|
| Key absent from `appSettings` | `web.config` missing the entry | `TryParse` on `null` returns `false`; hardcoded default is used silently |
| Key present but non-numeric | Misconfigured value (e.g. `"high"` instead of `72`) | `TryParse` fails; hardcoded default is used silently |
| `LegacyCountrySurcharge` absent | Key not defined in `web.config` | `?? ""` coalesces to empty string; no surcharge countries active |
| `LegacyCountrySurcharge` malformed | Extra spaces around codes | `Trim()` before comparison tolerates leading/trailing whitespace |
| No exception is ever thrown | Design choice: silent fallback | The engine continues with defaults; misconfiguration is invisible at runtime |

---

## 6. Integrations

### Internal
- **Decision Rule Pipeline** (`CreditDecisionService.cs:49–51`): Primary consumer of all three resolved threshold values (`hardOverdue`, `highRisk`, `manualReviewAmount`); uses them in rules `B90`, `R72`, and `R75`.
- **Risk Score Computation** (`CreditDecisionService.cs:33`): Consumes `IsSurchargeCountry` to conditionally add +7 to the base risk score when the customer's country is in the surcharge list.

### External
- **Infrastructure / Configuration (`web.config`)**: Sole data source. The `appSettings` section is read via `System.Configuration.ConfigurationManager` at runtime on every `Evaluate` call. No caching or hot-reload mechanism exists — a `web.config` change takes effect on the next application restart (standard ASP.NET behaviour).

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `HighRiskScore` | Minimum effective risk score that triggers a `REVIEW` (code `R72`) or blocks automatic approval | `72` |
| `ManualReviewAmount` | Order amount threshold above which non-CARD payments require manual review (code `R75`) | `7500` |
| `HardBlockOverdueDays` | Overdue days threshold that unconditionally blocks a customer (code `B90`); cannot be bypassed by `ManualOverride` | `90` |
| `LegacyCountrySurcharge` | Comma-separated list of country codes that incur a +7 risk-score surcharge | `PT,IT,GR` |
| `BatchIntervalSeconds` | Consumed by `NightlyBatch`; declared in the same `appSettings` block but not read by this submodule | `15` |

---

## 8. Summary

- **Components**: 3 backend (private static methods), 0 frontend, 1 configuration source (`web.config appSettings`)
- **Endpoints**: 0 (in-process only)
- **Business rules**: 4 (one per configuration key: `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge`)
- **Integrations**: 2 internal (Decision Rule Pipeline, Risk Score Computation), 1 external (Infrastructure/Configuration — `web.config`)

---

**Note**: Document maintained under 1500 lines. Silent fallback behaviour is the most significant migration risk: if a target system lacks equivalent defaults, threshold mismatches will occur without any runtime error signal.
