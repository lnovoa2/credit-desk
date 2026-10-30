# Submodules of Module: Application Bootstrap and Global Lifecycle

## Reference
**Source Document**: `/documentation/modules/01-modulos-plataforma.md` - Section 1
**Analysis Date**: 2026-08-10
**Parent Module**: Application Bootstrap and Global Lifecycle

## Summary
Total submodules identified: **2**

### Distribution by Type
- Backend Components: 2
- Frontend Components: 0
- Data Layer: 0
- API/Services: 0
- Configuration: 0
- Utilities: 0

## Submodule Index
1. [Application Startup Handler](#application-startup-handler) - Backend Component
2. [Application Shutdown Handler](#application-shutdown-handler) - Backend Component

---

## Submodule Detail

### Application Startup Handler

**Type**: Backend Component

**Location**:
- **Backend**: `/Global.asax` — `Application_Start` event handler (lines 3–7)

**Purpose**: Bootstraps the entire application on first request. Calls `LegacyRepository.EnsureDatabase()` to create the SQLite schema and seed initial data if it does not yet exist, then calls `NightlyBatch.Start()` to arm the background timer.

**Key Files**:
1. `Global.asax` — ASP.NET application-level event file; hosts the `Application_Start` inline C# handler

**Dependencies**:
- **External Modules**: Data Access and Persistence Layer (`LegacyRepository.EnsureDatabase`)
- **External Modules**: Nightly Batch Processing (`NightlyBatch.Start`)
- **Internal Submodules**: Application Shutdown Handler (shares the same file)
- **Libraries**: `System.Web` (ASP.NET runtime — implicit in `Global.asax`)

**Related Components**:
- **Entities**: SQLite database file (`App_Data/legacy-credit-desk.db`) — created here if absent
- **Services**: `NightlyBatch` timer — started here
- **APIs**: None
- **UI**: None

**Integration Points**:
- Entry point for the ASP.NET application lifecycle; executed once by the runtime before any HTTP request is served
- Triggers schema creation and seed data insertion in `LegacyRepository`
- Arms the `NightlyBatch` background timer that will fire on its own interval thereafter

---

### Application Shutdown Handler

**Type**: Backend Component

**Location**:
- **Backend**: `/Global.asax` — `Application_End` event handler (lines 9–12)

**Purpose**: Performs a clean shutdown of background resources when the ASP.NET application domain is unloaded. Calls `NightlyBatch.Stop()` to dispose the background timer and prevent orphaned threads.

**Key Files**:
1. `Global.asax` — ASP.NET application-level event file; hosts the `Application_End` inline C# handler

**Dependencies**:
- **External Modules**: Nightly Batch Processing (`NightlyBatch.Stop`)
- **Internal Submodules**: Application Startup Handler (shares the same file; this handler undoes what startup started)
- **Libraries**: `System.Web` (ASP.NET runtime — implicit in `Global.asax`)

**Related Components**:
- **Entities**: None
- **Services**: `NightlyBatch` timer — stopped here
- **APIs**: None
- **UI**: None

**Integration Points**:
- Invoked by the ASP.NET runtime when the application domain shuts down (IIS recycle, explicit stop, or container stop)
- Sole consumer of `NightlyBatch.Stop()`; without this, the `System.Threading.Timer` would keep firing on a torn-down domain

---

## Relationship Map

### Data Flow

```text
[ASP.NET Runtime]
      |
      v Application_Start
[Global.asax: APP_STARTUP]
      |                   \
      v                    v
[LegacyRepository        [NightlyBatch
 .EnsureDatabase()]        .Start()]
(creates DB schema         (arms timer)
 + seed data)

[ASP.NET Runtime]
      |
      v Application_End
[Global.asax: APP_SHUTDOWN]
      |
      v
[NightlyBatch.Stop()]
(disposes timer)
```

### Call Hierarchy
- `APP_STARTUP` calls `LegacyRepository.EnsureDatabase()`
- `APP_STARTUP` calls `NightlyBatch.Start()`
- `APP_SHUTDOWN` calls `NightlyBatch.Stop()`

### UI-Backend Mapping
| UI Component | Backend Service | DB Table |
|---|---|---|
| None | `LegacyRepository.EnsureDatabase` | All tables (schema creation) |
| None | `NightlyBatch.Start / Stop` | None (timer management) |

---

## Completeness Verification

### File Statistics
- Total backend files in module directory: 1 — Documented: 1
- Total frontend files: 0 — Documented: 0
- Total JavaScript files: 0 — Documented: 0
- Total CSS files: 0 — Documented: 0
- Total SQL scripts: 0 — Documented: 0
- **Coverage**: 100%

### Directories Analyzed
- [x] `/` (root) — 1 file (`Global.asax`)

---

## Technical Notes
- `Global.asax` uses an inline `<script runat="server">` code block with C# rather than a separate code-behind (`Global.asax.cs`) file. This is valid in ASP.NET Web Forms but atypical — a migration target would normally use a proper `HttpApplication` subclass.
- The file contains no `using` directives; it relies on `App_Code` classes being automatically compiled by the ASP.NET build provider and available in the application's global namespace.
- Error handling is absent from both event handlers. An exception in `Application_Start` (e.g. a failed `EnsureDatabase` call) will surface as a hard startup failure with no fallback or logging.

---

## Complexity and Prioritization

### Submodule Complexity
- **Low**: Application Startup Handler — 3 lines of code; complexity lies in the modules it delegates to, not in the handler itself
- **Low**: Application Shutdown Handler — 2 lines of code; straightforward timer teardown

---

## Next Step
This document serves as the basis for **Prompt 3**, where each submodule will be analyzed in full detail to create the complete functional specification.
