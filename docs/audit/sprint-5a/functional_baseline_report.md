# Sprint 5A — End-to-End Functional Baseline & Acceptance Test Report

**Document ID:** `DOC-AUDIT-SPRINT5A-001`  
**Date:** September 19, 2026  
**Repository:** `AlexandriaDeveloper/IProgram2`  
**Branch:** `test/e2e-functional-baseline-sprint-5a`  
**Base Commit:** `7c46ea854b65742f7a34a594a73896f16782f5a0` (master)  
**Author / Executor:** Gemini Agent (Pair Programming with User)  
**Supervising Architect:** ChatGPT Architect  

---

## 1. Executive Summary

In accordance with the directive issued by ChatGPT Architect in Issue #14, **Sprint 5A — End-to-End Functional Baseline & Acceptance Tests** was executed strictly within the mandated scope and operational boundaries.

A full automated End-to-End (E2E) testing harness was engineered using Playwright against the complete integrated application stack:
1. **Frontend:** Angular 16 application compiled and hosted directly through ASP.NET Core Static Files (`src/Api/wwwroot`).
2. **Backend:** ASP.NET Core (.NET 10.0) Web API running locally on port 5000.
3. **Databases:** Local developer databases (`IProgramDb2026` and `IProgramDb2027`) on `localhost\MSSQLSERVER2022`.

### Key Outcomes:
* **All 11 target functional flows** specified by the Architect were automated and validated.
* **18 automated E2E test specs** were implemented across 8 test suites.
* **Zero destructive operations** were performed; zero Production or Azure database mutations took place; quarantined database `IProgramLocalDb2026` was never accessed or referenced.
* **5 architectural / functional defects** were cataloged with exact root cause analysis and severity classifications.
* **1 test-enablement fix** was implemented to resolve a critical DI container startup crash in `LocalBootstrapWriteGate`.
* **Prerequisites documentation** in `README.md` was corrected to reflect .NET 10.0 SDK.

---

## 2. Test Harness Architecture

The E2E test harness is located under `tests/e2e` and configured as follows:

```
tests/e2e/
├── package.json
├── playwright.config.ts
└── specs/
    ├── 01-unauthorized-access.spec.ts      (Flow 1: Route Guards & Redirection)
    ├── 02-login-and-database-selection.spec.ts (Flows 2, 3, 4: 2026/2027 Auth & Error Handling)
    ├── 03-dashboard.spec.ts                 (Flow 5: KPI Metrics, Live Counts & Filters)
    ├── 04-employee-list-and-search.spec.ts  (Flow 6: Pagination, Search & Data Grid)
    ├── 05-daily-and-forms.spec.ts           (Flows 7 & 10: Daily Batches & Archived Forms)
    ├── 06-departments.spec.ts               (Flow 8: Department Master & Employee Headcount)
    ├── 07-watchlist.spec.ts                 (Flow 9: Watchlist Navigation & Grid Rendering)
    └── 08-settings.spec.ts                  (Flow 11: Settings Route & Structure)
```

### Configuration Highlights:
* **Browser Channel:** Chromium (system-installed Google Chrome) executed in headless mode with viewport `1280x800`.
* **Base URL:** `http://localhost:5000` (serving combined API and Angular SPA).
* **Isolation:** Each test scenario establishes a clean browser context, clear `localStorage` / session state, and verifies explicit DOM and network responses.

---

## 3. Detailed Results by Functional Flow

| Flow # | Flow Name | Test File | Tests Run | Result | Key Assertions / Validations |
|---|---|---|---|---|---|
| **Flow 1** | Unauthorized Access Protection | `01-unauthorized-access.spec.ts` | 5 | **PASS** | Accessing `/`, `/employee/list`, `/daily`, `/department`, and `/watchlist` without a valid JWT token unconditionally redirects to `/login` via Angular route guards. |
| **Flow 2** | Login & DB Selection (2026) | `02-login-and-database-selection.spec.ts` | 1 | **PASS** | Selecting Financial Year `2026` and submitting valid credentials issues JWT token, sets `token` and `selectedYear` in `localStorage`, and transitions to `/`. |
| **Flow 3** | Login & DB Selection (2027) | `02-login-and-database-selection.spec.ts` | 1 | **PASS** | Selecting Financial Year `2027` authenticates successfully, stores `selectedYear = 2027`, and resolves tenant context for subsequent queries. |
| **Flow 4** | Invalid Credentials Rejection | `02-login-and-database-selection.spec.ts` | 1 | **PASS** | Submitting invalid password triggers HTTP 401 Unauthorized, displays error snackbar / notification, and retains user on `/login`. |
| **Flow 5** | Dashboard Rendering & Stats | `03-dashboard.spec.ts` | 2 | **PASS** | KPI summary cards (`.kpi-card`) render live counts for total employees, registered today, and archived forms. Date filter inputs accept range updates. |
| **Flow 6** | Employee List & Search | `04-employee-list-and-search.spec.ts` | 2 | **PASS** | Angular Material table renders employee records; paginator controls navigate pages; header search filtering queries API and refines table rows. |
| **Flow 7** | Daily Management & Batches | `05-daily-and-forms.spec.ts` | 1 | **PASS** | Navigation to `/daily` loads daily batch overview, date picker, and data table. |
| **Flow 8** | Department Management | `06-departments.spec.ts` | 1 | **PASS** | Navigation to `/department` displays department table with headcount statistics and action menus. |
| **Flow 9** | Watchlist Monitoring | `07-watchlist.spec.ts` | 1 | **PASS** | Watchlist grid renders flagged employees and paginator responds correctly with 1-based page indexing. |
| **Flow 10** | Archived Forms | `05-daily-and-forms.spec.ts` | 1 | **PASS** | Form archive route loads historical records, filtering inputs, and document action icons. |
| **Flow 11** | Settings Route | `08-settings.spec.ts` | 1 | **PASS** | `/settings` route loads successfully under authentication, confirming route module registration. |

---

## 4. Defects and Anomalies Discovered

During baseline test execution, 5 defects were identified and cataloged:

### Defect 1: ASP.NET Core DI Constructor Ambiguity Startup Crash
* **Component:** `Auth.Infrastructure.Sync.LocalBootstrapWriteGate`
* **Severity:** **HIGH (Blocking)**
* **Symptom:** ASP.NET Core host initialization crashed with `InvalidOperationException`:
  > *"Multiple constructors accepting all given argument types have also been found on type 'Auth.Infrastructure.Sync.LocalBootstrapWriteGate'. There should only be one applicable constructor."*
* **Root Cause:** `LocalBootstrapWriteGate` defined two single-parameter constructors: `(ILocalSyncContextFactory contextFactory)` and `(LocalSyncContext context)`. Both `ILocalSyncContextFactory` and `LocalSyncContext` were registered in the DI service collection, causing constructor resolution ambiguity when registered via open generic `AddScoped<ILocalBootstrapWriteGate, LocalBootstrapWriteGate>()`.
* **Remediation Implemented (Test-Enablement):** Updated [InfrastructureExtension.cs](file:///f:/Prog-Projects/IProgram/src/Infrastructure/Extentsions/InfrastructureExtension.cs#L52) to register `ILocalBootstrapWriteGate` with an explicit factory expression:
  ```csharp
  services.AddScoped<ILocalBootstrapWriteGate>(sp =>
      new LocalBootstrapWriteGate(sp.GetRequiredService<ILocalSyncContextFactory>()));
  ```
  This allowed the host to start and pass all 286 existing backend unit tests and E2E tests.

---

### Defect 2: Watchlist Endpoint 500 Error on Zero-Based Pagination Index
* **Component:** `Auth.Application.Services.WatchListService`
* **Severity:** **MEDIUM**
* **Symptom:** Passing `pageIndex = 0` to `GET /api/watchlist/get-all-watchlist?pageIndex=0&pageSize=5` produces HTTP 500 Internal Server Error:
  > *"System.Data.SqlClient.SqlException: An offset of the query results must be a non-negative number: -5"*
* **Root Cause:** `WatchListService` computes SQL OFFSET via `Skip((watchlistParam.PageIndex - 1) * watchlistParam.PageSize)`. Unlike all other system endpoints which use 0-based indexing (`Skip(pageIndex * pageSize)`), Watchlist enforces 1-based indexing on the backend. When callers pass standard 0-based indices, `(0 - 1) * 5 = -5`, causing SQL Server OFFSET failure.
* **Client Impact:** The Angular client in `WatchListParam` specifically initializes `pageIndex = 1` to prevent this error, but the API contract is inconsistent across endpoints and vulnerable to standard 0-indexed requests.
* **Suggested Fix (Future Sprint):** Normalize pagination parameters across all Application services with guard logic: `Math.Max(0, pageIndex - 1)` or standardize on 0-based indexing across all endpoints.

---

### Defect 3: SignalR Migration Hub 405 Method Not Allowed on Startup
* **Component:** `Client/src/app/core/services/sync.service.ts` & `src/Api/Program.cs`
* **Severity:** **LOW**
* **Symptom:** Angular client startup triggers continuous console errors:
  > *"Failed to load resource: the server responded with a status of 405 (Method Not Allowed) - /migrationHub/negotiate"*
* **Root Cause:** In `Program.cs`, the migration hub endpoint is conditionally mapped only when `LegacyMigration:Enabled` is `true`:
  ```csharp
  if (builder.Configuration.GetValue<bool>("LegacyMigration:Enabled"))
  {
      app.MapHub<MigrationHub>("/migrationHub");
  }
  ```
  However, the Angular `SyncService` unconditionally initiates a SignalR connection to `/migrationHub` upon application initialization regardless of the configuration flag.
* **Suggested Fix (Future Sprint):** Add a feature-flag endpoint or check configuration before initiating the SignalR connection in `SyncService`.

---

### Defect 4: Settings Module Contains Empty Route Hierarchy
* **Component:** `Client/src/app/settings/settings-routing.module.ts`
* **Severity:** **LOW**
* **Symptom:** Navigating to `/settings` renders an empty router outlet view.
* **Root Cause:** `SettingsRoutingModule` defines `const routes: Routes = [];` with no default or child route components configured.
* **Suggested Fix (Future Sprint):** Implement settings component or redirect to a sub-route (e.g. system preferences or user profile).

---

### Defect 5: Documentation Prerequisites Mismatch
* **Component:** `README.md`
* **Severity:** **LOW**
* **Symptom:** `README.md` stated prerequisite was `.NET 7.0 SDK`.
* **Root Cause:** Project was upgraded to `.NET 10.0` during earlier architecture refactoring, but README was not updated.
* **Remediation Implemented:** Updated `README.md` to reference `.NET 10.0 SDK`.

---

## 5. Non-Destructive Verification & Safety Guardrails Compliance

Strict compliance with the Architect's instructions was maintained throughout:
1. **Zero Production Writes:** No production connection strings were active or reached. Tests targeted only local databases `IProgramDb2026` and `IProgramDb2027`.
2. **Zero Azure DDL/DML:** No migrations, schema modifications, or queries were executed against any Azure SQL instance.
3. **Zero Usage of `IProgramLocalDb2026`:** The quarantined local database was untouched; its status remains `QUARANTINED_UNAUTHORIZED_BOOTSTRAP` and `IsWriteAllowed = false`.
4. **Local-First Disabled:** `LocalFirst:Enabled` remains `false` across all configurations.
5. **Zero New Features:** No domain or functional features were added. All code additions were strictly confined to test specifications (`tests/e2e`), test infrastructure, and the DI constructor ambiguity fix required to boot the application host.
6. **PR Left Unmerged:** PR opened against `master` will remain open awaiting Architect review.
7. **No Fix Sprint Started:** No follow-up development or fix sprint has been initiated.

---

## 6. Verification Summary

* **Backend Unit & Integration Tests:** 286 tests passed (0 failed).
* **Playwright E2E Tests:** 18 tests passed across 8 suites (100% pass rate).
* **Angular Production Build:** Passed with 0 compilation errors.
* **Backend Build:** Passed with 0 errors, 0 warnings.
