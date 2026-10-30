# Functional Specification: ASP.NET Runtime Configuration

**Parent Module**: Infrastructure, Configuration and Scripts | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Provides all runtime-tunable parameters consumed by the application at startup, plus ASP.NET/Mono framework directives. Acts as the single external control point for credit decision thresholds and batch cadence without requiring recompilation.
- **Key Functionality**:
  - Defines five `appSettings` keys that directly govern credit decision behavior and background batch timing
  - Registers the `Mono.Data.Sqlite` assembly required by the data access layer
  - Sets ASP.NET compilation and security directives (debug mode, request validation, custom errors)
  - Consumed at runtime via `ConfigurationManager.AppSettings` by `CreditDecisionService` and `NightlyBatch`
  - All threshold defaults have fallback values hardcoded in consuming code; `web.config` values take precedence when present
- **Users**: System operators and deployment engineers (no end-user interaction — file is read-only at runtime)

---

## 2. Business Logic

### Key Processes

**Configuration Loading**: When the ASP.NET/Mono runtime starts (`Application_Start` in `Global.asax`), it parses `web.config`. The two consuming classes read their keys lazily:
1. `NightlyBatch.Start()` reads `BatchIntervalSeconds` once to set the recurring timer interval
2. `CreditDecisionService.Evaluate()` reads `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, and `LegacyCountrySurcharge` on every credit evaluation call

**Threshold Application in Credit Decisions**: The five keys translate directly into business rules:
1. `HardBlockOverdueDays` (90): if a customer's `OverdueDays >= 90`, the order is unconditionally blocked (code `B90`), even if `ManualOverride` is set
2. `HighRiskScore` (72): if the computed effective risk score reaches or exceeds this value and `ManualOverride` is false, the order is sent to manual review (code `R72`)
3. `ManualReviewAmount` (7500): orders at or above this amount using any non-CARD payment method are forced to manual review (code `R75`)
4. `LegacyCountrySurcharge` (PT,IT,GR): adds +7 risk points to the effective risk score for customers from these countries before applying any decision rule
5. `BatchIntervalSeconds` (15): sets the timer tick interval for the nightly batch risk-score recalculation

### Critical Validations

| Key | Rule | Fallback if missing |
|-----|------|---------------------|
| `BatchIntervalSeconds` | Minimum enforced at 10 seconds (`if (seconds < 10) seconds = 10`) | 45 seconds |
| `HighRiskScore` | Integer parse; used as `>=` threshold | 72 |
| `ManualReviewAmount` | Decimal parse; used as `>=` threshold | 7500 |
| `HardBlockOverdueDays` | Integer parse; used as `>=` threshold | 90 |
| `LegacyCountrySurcharge` | Comma-separated string; parsed on each call | Empty string (no surcharge countries) |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| `web.config` | XML configuration file parsed by ASP.NET/Mono at startup | Defines all `appSettings` keys; registers `Mono.Data.Sqlite` assembly; sets `debug`, `validateRequest`, `customErrors`, `targetFramework` |
| `CreditDecisionService.cs` | Consumer — credit decision engine | Reads `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge` via `ConfigurationManager.AppSettings` on each `Evaluate()` call |
| `NightlyBatch.cs` | Consumer — background batch processor | Reads `BatchIntervalSeconds` once during `Start()` to initialize the `System.Threading.Timer` interval |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| None | — | `web.config` has no direct UI component |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| `appSettings` section | Runtime parameter store | `BatchIntervalSeconds`, `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge` |
| `system.web/compilation/assemblies` | Assembly binding | `Mono.Data.Sqlite` assembly registration for ADO.NET data access |

---

## 4. APIs and Endpoints

### Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| — | — | `web.config` does not expose or define any HTTP endpoints | — |

**Configuration Access Pattern**:
```
// Reading a threshold value (CreditDecisionService pattern)
int hardOverdue = ConfigInt("HardBlockOverdueDays", 90);

// where ConfigInt is:
Int32.TryParse(ConfigurationManager.AppSettings[key], out value) ? value : fallback

// Reading the surcharge country list
string raw = ConfigurationManager.AppSettings["LegacyCountrySurcharge"] ?? "";
string[] values = raw.Split(',');
```

---

## 5. Data Flows

### Main Flow: Configuration → Credit Decision

```
ASP.NET Startup → web.config parsed → appSettings loaded into ConfigurationManager
                                                        ↓
HTTP Request (OrderEntry.aspx) → CreditDecisionService.Evaluate()
                                                        ↓
                               Reads: HighRiskScore, ManualReviewAmount,
                                      HardBlockOverdueDays, LegacyCountrySurcharge
                                                        ↓
                               Applies thresholds → APPROVED / REVIEW / BLOCKED
```

**Steps**:
1. Mono/XSP4 process starts and parses `web.config` into `ConfigurationManager`
2. `Global.asax Application_Start` triggers `LegacyRepository.EnsureDatabase()` and `NightlyBatch.Start()`
3. `NightlyBatch.Start()` reads `BatchIntervalSeconds` and schedules the recurring timer
4. On each order entry, `CreditDecisionService.Evaluate()` reads the four decision-threshold keys
5. Threshold values gate BLOCKED/REVIEW/APPROVED outcomes; no HTTP call, no caching layer

### Configuration → Batch Process

```
NightlyBatch.Start() → reads BatchIntervalSeconds → Timer(interval = seconds * 1000 ms)
                                                            ↓
                                              Tick() → RunNow() → recalculate risk scores
                                                            ↓
                              Hard-block: if overdue >= 90 (value from code constant, not config)
```

> **Note**: The batch `RunNow()` method uses the literal value `90` for auto-blocking, not `HardBlockOverdueDays`. Only `CreditDecisionService` reads `HardBlockOverdueDays` from config. This is an existing inconsistency in the codebase.

### Error Handling

| Error | Cause | Response |
|-------|-------|----------|
| Missing `appSettings` key | Key absent from `web.config` | Hardcoded fallback value used; no exception thrown |
| Unparseable integer/decimal value | Non-numeric value in key | Fallback value used (`TryParse` returns false) |
| Missing `Mono.Data.Sqlite` assembly | Assembly not registered or DLL absent | `TypeLoadException` at startup; application fails to start |
| `validateRequest="false"` | Intentionally disables ASP.NET input validation | Raw user input passes to code-behind without sanitization |

---

## 6. Integrations

### Internal

- **Credit Decision Engine** (`CreditDecisionService.cs`): Reads `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge` — these four keys directly control which orders are blocked, reviewed, or approved
- **Nightly Batch Processing** (`NightlyBatch.cs`): Reads `BatchIntervalSeconds` at startup to set timer cadence; minimum enforced at 10 seconds
- **Data Access and Persistence Layer** (`LegacyRepository.cs`): Benefits from `Mono.Data.Sqlite` assembly registration; without it the `using Mono.Data.Sqlite` namespace fails to resolve at runtime
- **Application Bootstrap** (`Global.asax`): Triggers the cascade that reads `BatchIntervalSeconds` via `NightlyBatch.Start()` on `Application_Start`

### External

- **ASP.NET / Mono runtime**: Sole consumer of the `system.web` configuration block; parses file on process start — no authentication required

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `BatchIntervalSeconds` | Timer interval (seconds) for nightly batch risk-score recalculation | `15` |
| `HighRiskScore` | Risk score threshold (inclusive) that triggers manual review (`R72`) | `72` |
| `ManualReviewAmount` | Order amount threshold (EUR, inclusive) that forces manual review for non-CARD payments (`R75`) | `7500` |
| `HardBlockOverdueDays` | Overdue days threshold (inclusive) that unconditionally blocks an order (`B90`), overriding `ManualOverride` | `90` |
| `LegacyCountrySurcharge` | Comma-separated ISO country codes subject to +7 risk-score surcharge in credit evaluation | `PT,IT,GR` |
| `debug` (`compilation`) | Enables debug symbols; affects performance | `true` |
| `targetFramework` | .NET target framework version for runtime behavior | `4.0` |
| `customErrors mode` | Controls ASP.NET error page display | `Off` (full stack traces shown) |
| `validateRequest` | ASP.NET input validation for potentially dangerous markup | `false` (disabled) |
| `Mono.Data.Sqlite` assembly | Registers SQLite ADO.NET driver for use in `App_Code` | Version `4.0.0.0`, PublicKeyToken `0738eb9f132ed756` |

---

## 8. Summary

- **Components**: 1 configuration file (`web.config`), 2 backend consumers (`CreditDecisionService`, `NightlyBatch`), 0 frontend, 0 data entities
- **Endpoints**: 0
- **Business rules governed**: 5 (hard block threshold, high-risk review threshold, large-order review threshold, country surcharge list, batch cadence)
- **Integrations**: 4 internal (Credit Decision Engine, Nightly Batch, Data Access Layer, Application Bootstrap), 1 external (ASP.NET/Mono runtime)

---
**Note**: Document maintained under 1500 lines. All threshold values in `web.config` take effect on the next server start for `BatchIntervalSeconds`, and immediately on the next credit evaluation call for the remaining four keys. Changing any threshold without redeployment requires an application pool recycle or process restart to reload `ConfigurationManager`.
