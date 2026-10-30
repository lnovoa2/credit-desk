# Functional Specification: Application Stylesheet - Web User Interface (ASP.NET Web Forms)

**Parent Module**: Web User Interface (ASP.NET Web Forms) | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Single-file plain CSS stylesheet that defines the complete visual identity of the Legacy Credit Desk application. It is the sole source of all layout, typography, color, and component-level styling rules.
- **Key Functionality**:
  - Defines the page layout structure: centered container, top banner, and navigation bar
  - Provides color-coded visual feedback for credit decision outcomes (approved / review / blocked)
  - Styles data grids, KPI tiles, form fields, and action buttons consistently across all pages
  - Is loaded once by the master page and applied globally to every page in the application
  - Contains no JavaScript, no CSS preprocessor, and no external dependencies
- **Users**: All application users (operations staff, credit analysts, batch administrators) — the stylesheet is passive infrastructure; no user interacts with it directly

---

## 2. Business Logic

### Key Processes

**Credit Decision Color Coding**: Maps the three possible credit decision statuses to distinct colors for immediate visual identification.

1. `CreditDecisionService.Evaluate()` returns a `CreditDecision` with `Status` = `APPROVED`, `REVIEW`, or `BLOCKED`
2. `OrderEntry.aspx.cs` maps status to CSS class: `APPROVED → "ok"`, `BLOCKED → "bad"`, `REVIEW → "warn"`
3. The result panel is rendered as `<div class='result ok|warn|bad'>` — the stylesheet supplies the color
4. The user reads the outcome from the colored panel without needing to parse text

**KPI Dashboard Highlighting**: The `.kpi` tile component visually groups and emphasizes the four operational KPI values on the Dashboard page, making health indicators immediately scannable.

### Critical Validations

| Element | Rule | Impact |
|---------|------|--------|
| `.ok` / `.warn` / `.bad` class names | Hardcoded as string literals in `OrderEntry.aspx.cs` line 64 | Renaming these CSS classes breaks decision rendering without a compile error |
| `.wrap` fixed width | Set to `1050px`; no responsive breakpoints | Display degrades on viewports narrower than 1050px |

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|----------------------|
| `OrderEntry.aspx.cs` (line 64) | Sole code-behind that references CSS class names programmatically | Selects `ok`, `warn`, or `bad` class at runtime based on `CreditDecision.Status`; any CSS class rename must be coordinated here |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `Content/site.css` | Global application stylesheet | Defines all visual rules: layout, navigation, typography, decision colors, grids, forms, KPIs |
| `Site.master` | Consumes the stylesheet | Links `site.css` via `<link rel="stylesheet">` in `<head>`; provides structural HTML elements (`.top`, `.nav`, `.wrap`) that the CSS targets |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|------------|
| N/A | No data entities | The stylesheet has no data model; it is a pure presentation resource |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| GET | `/Content/site.css` | Static file delivery by the ASP.NET/XSP4 host | No |

**No request/response payload** — the file is a static CSS asset served directly by the web host.

---

## 5. Data Flows

### Main Flow: Page Rendering with Stylesheet

```
Browser Request → Site.master renders HTML → <link> tag requests site.css
    → Web host serves Content/site.css → Browser applies CSS rules to DOM
    → Styled page displayed to user
```

**Steps**:
1. Any page request causes `Site.master` to emit `<link rel="stylesheet" href="Content/site.css">`
2. Browser requests `GET /Content/site.css` as a sub-resource
3. XSP4/Mono static file handler returns the file (typically cached by browser after first load)
4. Browser applies all CSS rules to the rendered DOM elements
5. Decision result panels, grids, KPI tiles, and navigation receive their visual styles

### Decision Color Rendering Flow

```
CreditDecision.Status → OrderEntry.aspx.cs maps to CSS class → Result Literal rendered
    → Browser applies .ok / .warn / .bad color rule from site.css
```

### Error Handling

| Error | Cause | Response |
|-------|-------|----------|
| `site.css` file missing | File deleted or path changed | Browser renders unstyled HTML; application is functional but visually broken |
| Wrong CSS class name in code-behind | CSS class renamed without updating `OrderEntry.aspx.cs` | Decision result renders without color coding; no runtime exception |

---

## 6. Integrations

### Internal

- **Master Page Layout and Navigation** (`Site.master`): Consumes `site.css` via `<link>` tag — provides the HTML container elements (`.top`, `.nav`, `.wrap`) targeted by CSS layout rules
- **Order Entry and Credit Authorization Page** (`OrderEntry.aspx.cs`): Programmatically references class names `.ok`, `.warn`, `.bad`, and `.result` to render decision outcome panels with color coding
- **Operations Dashboard Page** (`Default.aspx`): Uses `.kpi`, `.grid` classes for KPI tiles and data grids
- **Customer Credit File Page** (`Customers.aspx`): Uses `.grid`, `.btn`, `.field` classes for the customer grid and toggle button
- **Batch Monitor Page** (`Batch.aspx`): Uses `.grid`, `.small`, `.btn` classes for the batch flags table and controls

### External

- None — the stylesheet has no external dependencies, CDN references, or third-party frameworks

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `.wrap` width | Controls the page content container width | `1050px` (fixed; no responsive breakpoints) |
| `body` font-family | Application-wide typography | `Tahoma, Arial, sans-serif` |
| `.top` background | Top banner brand color | `#17365d` (dark navy) |
| `.ok` color | APPROVED decision text color | `#176b2c` (dark green) |
| `.warn` color | REVIEW decision text color | `#9a5b00` (amber) |
| `.bad` color | BLOCKED decision text color | `#9f1d20` (dark red) |
| `body` font-size | Base font size for all text | `13px` |

---

## 8. Summary

- **Components**: 0 backend services, 1 frontend stylesheet (`Content/site.css`), 0 data entities
- **Endpoints**: 1 (static file GET `/Content/site.css`)
- **Business rules**: 1 (credit decision status → CSS class name mapping, tightly coupled to `OrderEntry.aspx.cs`)
- **Integrations**: 5 internal (all UI pages via `Site.master` + direct class-name reference in `OrderEntry.aspx.cs`), 0 external

---

**Note**: Document maintained under 1500 lines. Prioritizes functional information essential for understanding visual identity and the programmatic coupling between CSS class names and the credit decision rendering logic.
