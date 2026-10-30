# Functional Specification: Project Documentation and Business Knowledge

**Parent Module**: Infrastructure, Configuration and Scripts | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Set of human-readable reference documents that describe the project's purpose, the domain's legacy business rules (intentionally incomplete), the migration challenge objectives, and operational instructions. Not consumed by the application at runtime; they exist to guide migration tool developers and testers.
- **Key Functionality**:
  - General entry point for any developer or tool approaching the project (`README.md`)
  - Partial domain knowledge source on credit decision rules (`docs/business-rules.md`)
  - Definition of recovery and validation objectives for migration tools (`docs/migration-test-objectives.md`)
  - Operational guide: local startup with/without Docker, environment reset, automated smoke test
  - Intentional documentation gap that forces recovery of real behavior from source code
- **Users**: Migration tool developers, system architects, QA engineers, and any developer needing to understand or migrate the legacy system

---

## 2. Business Logic

### Key Processes

**Guided Legacy System Onboarding**: The `README.md` provides a structured introduction that allows a new developer to understand the project, start the environment, and run validation scenarios without prior knowledge of the codebase.
1. Read the objective and legacy stack description
2. Start the environment with `docker compose up --build`
3. Access `http://localhost:8080` to verify the web interface
4. Run `./scripts/verify-docker.sh` to validate the full deployment
5. Consult `docs/` for domain knowledge and migration test objectives

**Intentional Knowledge Gap (Migration Challenge)**: `docs/business-rules.md` is deliberately incomplete by design. It documents six high-level rules but explicitly omits thresholds, rule ordering, country penalties, and exact override semantics. This forces a migration tool to recover real behavior from source code rather than documentation.
1. Read the six high-level documented rules
2. Identify "known mismatch with code" section listing undocumented aspects
3. Recover missing behavior directly from `CreditDecisionService.cs` and SQL

**Migration Objectives Assessment**: `docs/migration-test-objectives.md` defines ten verifiable capabilities that any migration tool must demonstrate to validate a successful migration.
1. Identify ten required capabilities (Web Forms discovery, data model recovery, layer separation, etc.)
2. Use each objective as an acceptance criterion for the migration output
3. Validate behavioral preservation, not only syntactic translation

### Critical Validations

| Aspect | Documented Rule | Undocumented (must be recovered from code) |
|--------|----------------|---------------------------------------------|
| Credit limit | Do not exceed without manual validation | Exact threshold, calculation logic |
| Overdue blocking | Customers with serious overdue debt blocked | Exact days threshold (`HardBlockOverdueDays=90`) |
| High-value review | High-value orders should be reviewed | Exact amount threshold (`ManualReviewAmount=7500 EUR`) |
| Risk score | Recalculated by night process | Recalculation formula, weighting |
| Manual override | Can allow exceptional operations | Exact semantics vs. hard blocking |
| Customer seniority | Long-standing customers may receive favorable treatment | Exact seniority thresholds and adjustments |

---

## 3. Components

### Backend
| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| (None) | — | Static Markdown files; no runtime backend component |

### Frontend
| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| (None) | — | Static Markdown files; no UI component |

### Documents
| File | Purpose | Key Content |
|------|---------|-------------|
| `README.md` | Main project entry point | Objective, legacy stack, DB structure, startup procedures, test scenarios, file structure, rationale for SQLite |
| `docs/business-rules.md` | Partial domain knowledge | Six high-level business rules + explicit listing of undocumented aspects |
| `docs/migration-test-objectives.md` | Migration validation guide | Ten numbered objectives a migration tool must demonstrate |

---

## 4. APIs and Endpoints

### Main Endpoints
| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| (None) | — | This submodule contains no APIs or HTTP endpoints | — |

**Documented Test Scenarios in README.md**:
```
# Recommended test scenarios (documented in README.md)
1. C001, 250 EUR, TRANSFER     → favorable case (APPROVED expected)
2. C002, 2500 EUR, INVOICE     → exposure/debt force REVIEW or BLOCKED
3. C003                        → high-risk/blocked customer
4. Order >= 7500 EUR           → REVIEW except for coded exceptions
5. Enable ManualOverride + repeat order → changes decision path
6. Execute /Batch.aspx         → recalculates risks and blockings
7. Generate BLOCKED order      → activates SQLite audit trigger
```

---

## 5. Data Flows

### Main Flow: Developer Onboarding

```
New Developer → README.md → docker compose up --build → http://localhost:8080
                     ↓
              docs/business-rules.md → Understand domain (partial)
                     ↓
       docs/migration-test-objectives.md → Define migration success criteria
                     ↓
              Source code → Recover real behavior (CreditDecisionService.cs, SQL)
```

**Steps**:
1. `README.md` provides context, instructions, and test scenarios
2. `docs/business-rules.md` provides high-level domain rules (deliberately incomplete)
3. `docs/migration-test-objectives.md` defines the ten acceptance criteria for migration
4. Source code is the authoritative source for exact business logic

### Main Flow: Migration Tool Validation

```
Migration Tool → Analyzes source code → Produces migrated system
                          ↓
         docs/migration-test-objectives.md → 10 validation checkpoints
                          ↓
              README.md test scenarios → Behavioral regression tests
                          ↓
                  Pass/Fail assessment
```

**Steps**:
1. Migration tool processes the entire codebase
2. Each of the 10 objectives is used as an acceptance criterion
3. Seven documented test scenarios from `README.md` serve as regression cases
4. `docs/business-rules.md` gap confirms behavioral recovery from code, not documentation

### Error Handling
| Error | Cause | Action |
|-------|-------|--------|
| Missing context | Developer reads only `docs/business-rules.md` | Explicit note in doc: "must be recovered from code" |
| Incomplete migration | Tool translates syntax without recovering behavior | Objectives 4–6 in `migration-test-objectives.md` specifically target this failure mode |

---

## 6. Integrations

### Internal
- **Application Bootstrap and Global Lifecycle**: `README.md` documents `Global.asax` startup behavior and batch timer initialization
- **Credit Decision Engine**: `docs/business-rules.md` references `CreditDecisionService` rules; `README.md` lists decision factors (credit limit, debt, overdue days, risk score, country, seniority, etc.)
- **Data Access and Persistence Layer**: `README.md` documents the SQLite embedded DB structure (tables, views, trigger, seed data); `migration-test-objectives.md` objective #2 targets `DataSet` schema recovery
- **Global In-Memory State**: `migration-test-objectives.md` objective #7 explicitly targets non-persisted static state (`LegacyState`)
- **Nightly Batch Processing**: `README.md` documents `Batch.aspx` and the background timer; `migration-test-objectives.md` objective #9 targets batch reproduction
- **Web User Interface**: `README.md` documents all four `.aspx` pages and their test scenarios; `migration-test-objectives.md` objective #1 targets Web Forms frontend discovery
- **All Scripts (same module)**: `README.md` documents `run-local.sh`, `reset-data.sh`, and `verify-docker.sh` with usage instructions and examples

### External
- (None) — Static documentation; no external system dependencies

---

## 7. Configuration

| Property | Purpose | Default/Value |
|----------|---------|---------------|
| (None) | These files are static Markdown; they do not read or write configuration | — |

**Referenced Configuration Properties** (documented in `README.md`, defined in `web.config`):

| Property | Purpose | Documented Value |
|----------|---------|-----------------|
| `BatchIntervalSeconds` | Batch timer cadence | 15 (seconds) |
| `HighRiskScore` | Threshold for high-risk customer classification | 72 |
| `ManualReviewAmount` | Order amount above which manual review is triggered | 7500 (EUR) |
| `HardBlockOverdueDays` | Days overdue after which customer is hard-blocked | 90 |
| `LegacyCountrySurcharge` | Country codes subject to legacy surcharge rules | PT,IT,GR |

---

## 8. Summary

- **Components**: 0 backend, 0 frontend, 3 documentation files
- **Endpoints**: 0
- **Business Rules**: 6 high-level documented rules + 10 migration validation objectives + 7 documented test scenarios
- **Integrations**: 7 internal (all other modules), 0 external

---
**Note**: This submodule is entirely static documentation. Its distinguishing feature is the **intentional incompleteness** of `docs/business-rules.md`, which is a deliberate design decision to simulate the gap between legacy operational knowledge and recoverable source-code behavior — a core challenge in any real-world migration project.
