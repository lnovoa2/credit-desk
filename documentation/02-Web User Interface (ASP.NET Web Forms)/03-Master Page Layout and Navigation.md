# Functional Specification: Master Page Layout and Navigation

**Parent Module**: Web User Interface (ASP.NET Web Forms) | **Date**: 2026-08-10

## 1. Executive Summary

- **Purpose**: Defines the shared HTML shell inherited by all four application pages. Provides the outer document structure, top banner, navigation bar, and the single content placeholder where each page injects its specific UI.
- **Key Functionality**:
  - Renders the application title and subtitle on every page ("Legacy Credit Desk 2.7 — Wholesale credit authorization console")
  - Provides a navigation bar with four links: Dashboard, Customers, New Order, Night Batch
  - Declares the `MainContent` `ContentPlaceHolder` that all child pages use to embed their content
  - Injects the single global stylesheet (`Content/site.css`) for the entire application
  - Wraps all page content in a single top-level `<form runat="server">`, making every child page participate in one ASP.NET postback form
- **Users**: All users with access to the application; the master page is transparent infrastructure — users interact only with the child pages it hosts

---

## 2. Business Logic

### Key Processes

**Navigation Provision**: Renders a consistent navigation bar on every page load.
1. Browser requests any `.aspx` page
2. ASP.NET runtime merges the child page markup into the `MainContent` placeholder
3. The master page HTML (banner + nav + wrapper) is emitted once, surrounding the child content
4. The user sees the same header and navigation regardless of which page is active

### Critical Validations

| Field | Validation | Error |
|-------|------------|-------|
| N/A | No server-side logic; the master page contains no code-behind | N/A |

> The master page has no `.master.cs` code-behind file. All logic is static markup rendered by the ASP.NET runtime.

---

## 3. Components

### Backend

| Component | Purpose | Key Responsibilities |
|-----------|---------|---------------------|
| ASP.NET Master Page Runtime | Merges child page content into the master template at request time | Resolves `MasterPageFile` directive, populates `ContentPlaceHolder`, handles the shared `<form runat="server">` lifecycle |

### Frontend

| Component | Purpose | Main Functionality |
|-----------|---------|-------------------|
| `Site.master` | Shared HTML document shell | Emits `<!DOCTYPE html>`, `<head>` (with stylesheet link), top banner (`div.top`), navigation bar (`div.nav`), content wrapper (`div.wrap`), and `<asp:ContentPlaceHolder ID="MainContent">` |
| `div.top` | Application header banner | Displays application name "Legacy Credit Desk 2.7" and subtitle "Wholesale credit authorization console" with dark-blue background |
| `div.nav` | Navigation bar | Four anchor links: Dashboard → `Default.aspx`, Customers → `Customers.aspx`, New Order → `OrderEntry.aspx`, Night Batch → `Batch.aspx` |
| `div.wrap` | Page content container | Fixed-width (1050 px) centered white panel that contains the `MainContent` placeholder |
| `<form id="form1" runat="server">` | Single top-level ASP.NET form | Wraps the entire page body; all server controls in child pages participate in this single form and trigger full-page postbacks |

### Data

| Entity | Purpose | Key Fields |
|--------|---------|-----------|
| N/A | The master page accesses no data entities | — |

---

## 4. APIs and Endpoints

### Main Endpoints

| Method | URL | Purpose | Auth |
|--------|-----|---------|------|
| N/A | N/A | The master page defines no endpoints; it is a purely presentational template | N/A |

> Navigation links are plain HTML anchors (`<a href="...">`), not API calls. No HTTP endpoints are declared in this submodule.

---

## 5. Data Flows

### Main Flow: Page Rendering

```
Browser Request → ASP.NET Runtime
    → Resolves MasterPageFile="~/Site.master"
    → Merges child page content into MainContent placeholder
    → Renders: DOCTYPE + <head> (site.css link) + <body>
        → div.top (banner)
        → div.nav (navigation links)
        → div.wrap → [child page content]
    → HTTP Response delivered to browser
```

**Steps**:
1. Any `.aspx` request triggers the ASP.NET page lifecycle
2. The runtime identifies `Site.master` as the master template via the `MasterPageFile` directive on each child page
3. The child page content regions are inserted into `MainContent`
4. The full merged HTML document (master shell + child content) is streamed to the browser
5. The browser applies `Content/site.css` for all visual styling

### Error Handling

| Error | Cause | Action |
|-------|-------|--------|
| Missing `Site.master` file | File deleted or path wrong | ASP.NET runtime throws `HttpException` at page compile time; no page in the application can render |
| Missing `site.css` | Stylesheet not found | Browser silently loads the page unstyled; no server-side error |
| Broken navigation link target | Referenced `.aspx` page missing | Browser receives a 404 from the server; master page itself renders successfully |

---

## 6. Integrations

### Internal
- **Application Stylesheet (`Content/site.css`)**: Linked via `<link rel="stylesheet">` in `<head>`; all CSS classes used across child pages (`.ok`, `.warn`, `.bad`, `.grid`, `.kpi`, `.field`, etc.) are defined there
- **Operations Dashboard Page (`Default.aspx`)**: Inherits this master via `MasterPageFile="~/Site.master"`; embeds its content in `MainContent`
- **Customer Credit File Page (`Customers.aspx`)**: Inherits this master via `MasterPageFile="~/Site.master"`; embeds its content in `MainContent`
- **Order Entry and Credit Authorization Page (`OrderEntry.aspx`)**: Inherits this master via `MasterPageFile="~/Site.master"`; embeds its content in `MainContent`
- **Batch Monitor and Manual Trigger Page (`Batch.aspx`)**: Inherits this master via `MasterPageFile="~/Site.master"`; embeds its content in `MainContent`

### External
- None. The master page has no dependencies on external systems, APIs, or third-party libraries beyond the standard ASP.NET Web Forms runtime (`System.Web`).

---

## 7. Configuration

| Property | Purpose | Default |
|----------|---------|---------|
| `MasterPageFile="~/Site.master"` | Declared on each child page `@Page` directive; binds the child to this master template | `~/Site.master` (hardcoded in each `.aspx`) |
| `AutoEventWireup="true"` | Set on each child page; ASP.NET auto-wires `Page_Load` and event handlers by method name convention | `true` (ASP.NET default) |
| `div.wrap` fixed width | CSS rule in `site.css` sets `.wrap` to `1050px` | `1050px` — no responsive breakpoints |

---

## 8. Summary

- **Components**: 0 backend code-behind, 1 frontend template file (`Site.master`), 0 data entities
- **Endpoints**: 0 (navigation links are plain HTML anchors)
- **Business Rules**: 0 (the master page contains no server-side logic)
- **Integrations**: 5 internal (4 child pages + 1 stylesheet), 0 external

---

**Note**: Document kept under 1500 lines. The Master Page Layout and Navigation submodule is intentionally minimal — it is a static structural template with no business logic, no data access, and no code-behind. Its significance lies in being the single point of change for application-wide layout and navigation: any update to the banner, navigation links, or page structure requires editing only `Site.master`.
