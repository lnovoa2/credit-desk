# Platform Modules

## Analysis Date
2026-08-10

## Summary
Total modules identified: **7**

## Module Directory
1. Application Bootstrap and Global Lifecycle
2. Credit Decision Engine
3. Data Access and Persistence Layer
4. Global In-Memory State
5. Nightly Batch Processing
6. Web User Interface (ASP.NET Web Forms)
7. Infrastructure, Configuration and Scripts

## Module Detail

### Application Bootstrap and Global Lifecycle

**Primary Directory**: `/` (root — `Global.asax`)

**Related Directories**:
- `/` — `Global.asax` (application event handlers)

**Description**: Manages the ASP.NET application lifecycle. On startup it calls `LegacyRepository.EnsureDatabase()` to create the SQLite schema and seed data if needed, and starts the `NightlyBatch` timer. On application shutdown it stops the background timer cleanly.

---

### Credit Decision Engine

**Primary Directory**: `/App_Code/`

**Related Directories**:
- `/App_Code/CreditDecisionService.cs`

**Description**: Core business logic module that evaluates whether a credit order should be `APPROVED`, `REVIEW`, or `BLOCKED`. It applies a sequential set of risk-scoring adjustments and decision rules based on customer attributes (credit limit, debt, overdue days, risk score, country, seniority, blocked flag, manual override) and order attributes (amount, payment method, priority). Rule ordering and short-circuit evaluation are semantically significant and not fully captured in external documentation.

---

### Data Access and Persistence Layer

**Primary Directory**: `/App_Code/`

**Related Directories**:
- `/App_Code/LegacyRepository.cs`
- `/App_Data/` — runtime SQLite database file (`legacy-credit-desk.db`)

**Description**: Encapsulates all interactions with the embedded SQLite database using ADO.NET (`Mono.Data.Sqlite`). Responsible for schema creation (`CREATE TABLE / INDEX / VIEW / TRIGGER`), seed data insertion, loading the full dataset into an in-memory `DataSet`, persisting changes via explicit INSERT/UPDATE/DELETE commands wrapped in transactions, and executing direct SQL queries for calculated aggregates (open order exposure, recent order count). Deliberately mixes `DataSet`-based access with direct SQL queries to simulate realistic legacy data access debt.

---

### Global In-Memory State

**Primary Directory**: `/App_Code/`

**Related Directories**:
- `/App_Code/LegacyState.cs`

**Description**: Provides a static, process-scoped shared state container using a `Hashtable` and primitive fields. Stores the batch execution counter, the timestamp and message of the last batch run, and per-customer runtime flags (e.g. `AUTO_BLOCKED`). All mutations are protected by a shared `SyncRoot` lock object. This state is not persisted to the database and is reset on application restart.

---

### Nightly Batch Processing

**Primary Directory**: `/App_Code/`

**Related Directories**:
- `/App_Code/NightlyBatch.cs`
- `/Batch.aspx` + `/Batch.aspx.cs` — monitoring and manual trigger UI

**Description**: Background process driven by a `System.Threading.Timer` started at application boot. At each tick (configurable interval, default 15 seconds in this fixture) it loads all customers, recalculates risk scores based on overdue days and debt-to-limit ratio, and automatically blocks customers whose overdue days reach or exceed 90. It never unblocks customers. Results are persisted via `LegacyRepository.Save()` and a batch audit record is written. Can also be triggered manually via `Batch.aspx`.

---

### Web User Interface (ASP.NET Web Forms)

**Primary Directory**: `/` (root — `.aspx` and `.aspx.cs` files)

**Related Directories**:
- `/Default.aspx` + `/Default.aspx.cs` — operations dashboard
- `/Customers.aspx` + `/Customers.aspx.cs` — customer credit file and manual override
- `/OrderEntry.aspx` + `/OrderEntry.aspx.cs` — order creation and credit decision display
- `/Batch.aspx` + `/Batch.aspx.cs` — batch status monitor and manual trigger
- `/Site.master` — shared master page layout and navigation
- `/Content/site.css` — application stylesheet

**Description**: ASP.NET Web Forms user interface composed of four pages sharing a master layout. The Dashboard displays KPI counts and recent orders/audit entries. The Customers page lists all customer credit profiles and allows toggling the commercial manual override per customer. Order Entry accepts a customer, amount, payment method, and priority, invokes the Credit Decision Engine, persists the resulting order, and renders the decision outcome. The Batch page shows batch execution history and allows a forced immediate run. Note: `OrderEntry.aspx.cs` contains a validation rule (invoice minimum of 25 EUR) that lives in the code-behind rather than in `CreditDecisionService`.

---

### Infrastructure, Configuration and Scripts

**Primary Directory**: `/` (root — `web.config`, `Dockerfile`, `docker-compose.yml`)

**Related Directories**:
- `/scripts/` — `run-local.sh`, `reset-data.sh`, `verify-docker.sh`
- `/docs/` — `business-rules.md`, `migration-test-objectives.md`
- `/documentation/` — generated pipeline output and cost records

**Description**: All infrastructure, deployment, and operational support files. `web.config` holds key tuning parameters read at runtime (`BatchIntervalSeconds`, `HighRiskScore`, `ManualReviewAmount`, `HardBlockOverdueDays`, `LegacyCountrySurcharge`). The `Dockerfile` and `docker-compose.yml` define the containerised deployment on Mono/XSP4. Helper scripts cover local startup without Docker, full data reset, and automated smoke-testing. The `docs/` directory contains intentionally incomplete business-rule notes and migration test objectives.

---

## Completeness Verification

### Directories Analyzed
- [x] `/App_Code/` — 4 source files, all assigned to modules 1–5
- [x] `/App_Data/` — runtime data directory, assigned to module 3
- [x] `/Content/` — 1 CSS file, assigned to module 6
- [x] `/docs/` — 2 documentation files, assigned to module 7
- [x] `/documentation/` — pipeline output directory, assigned to module 7
- [x] `/documentation/cost/` — cost records subdirectory, assigned to module 7
- [x] `/scripts/` — 3 shell scripts, assigned to module 7

### Root-Level Files Assigned
- [x] `Global.asax` — module 1
- [x] `Site.master` — module 6
- [x] `Default.aspx` / `Default.aspx.cs` — module 6
- [x] `Customers.aspx` / `Customers.aspx.cs` — module 6
- [x] `OrderEntry.aspx` / `OrderEntry.aspx.cs` — module 6
- [x] `Batch.aspx` / `Batch.aspx.cs` — module 6
- [x] `web.config` — module 7
- [x] `Dockerfile` — module 7
- [x] `docker-compose.yml` — module 7
- [x] `README.md` — module 7

### Statistics
- Total directories analyzed: 7
- Total directories assigned to modules: 7
- Coverage: 100%

## Notes
- The project is a compact ASP.NET 4.x Web Forms monolith (Mono/XSP4) designed as a legacy migration fixture. It has no separate frontend build, no package manager, no ORM, and no external database server.
- `App_Code/` acts as the entire business logic layer; there is no separate `/src/` tree.
- The `App_Data/` directory is empty at design time; `legacy-credit-desk.db` is created at first runtime access.
- A commercially significant validation rule (invoice minimum 25 EUR) is located in `OrderEntry.aspx.cs` rather than in `CreditDecisionService.cs` — this intentional distribution of business logic across the UI code-behind is a documented migration challenge.
- The `documentation/` directory is tool output generated by the CodeToDocAnalyzer pipeline and is not part of the application's runtime.
