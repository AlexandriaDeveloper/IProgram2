# End-to-End (E2E) Functional Acceptance Test Suite

This directory contains automated end-to-end acceptance tests built with [Playwright](https://playwright.dev/) to validate the end-to-end functional baseline of the IProgram application.

---

## 1. Safety Guardrails & Non-Destructive Policy

* **Zero Production Writes:** Tests only execute non-destructive read/verification queries against approved local development databases.
* **Zero Azure Access:** All network calls to remote or Azure SQL servers are strictly prohibited.
* **Quarantined Database Bypass:** The quarantined database `IProgramLocalDb2026` is completely bypassed.
* **Isolated from Generic CI:** Because tests interact with local database engines and seeded development tenants, E2E tests are kept out of generic cloud CI runners until dedicated isolated ephemeral environments are provisioned.

---

## 2. Prerequisites

1. **.NET 10.0 SDK**
2. **Node.js 18+ and npm**
3. **Local Microsoft SQL Server instance** on `localhost` hosting `IProgramDb2026` and `IProgramDb2027`.
4. **Google Chrome** (or Chromium) installed locally.

---

## 3. Configuration

Credentials are strictly read from environment variables and must **NEVER** be committed into version control:

1. Copy `.env.example` to `.env` in this directory:
   ```bash
   cp .env.example .env
   ```
2. Set your local developer test credentials:
   ```env
   E2E_USERNAME=your_username
   E2E_PASSWORD=your_password
   E2E_BASE_URL=http://localhost:5000
   ```

---

## 4. Execution Workflow

### Step 4.1: Build Frontend (Angular SPA)
Compile the Angular client into the API static files directory (`src/Api/wwwroot`):
```bash
cd Client
npm run build
cd ..
```

### Step 4.2: Start Backend API (Port 5000)
Run the ASP.NET Core API using the Release configuration:
```bash
dotnet run --project src/Api/Auth.Api.csproj -c Release --urls "http://localhost:5000"
```

### Step 4.3: Verify Runtime DB Safety (Preflight)
Before running tests, execute the preflight safety verification script to prove that all runtime database connections resolve only to approved local databases:
```powershell
powershell -ExecutionPolicy Bypass -File tests/e2e/preflight_db_safety_check.ps1
```

### Step 4.4: Run Playwright Tests
In a separate terminal, execute the test suite:
```bash
cd tests/e2e
npx playwright test
```

To run in headed mode for visual inspection:
```bash
npx playwright test --headed
```

To view the HTML report after execution:
```bash
npx playwright show-report
```

---

## 5. Test Suite Catalog

| Spec File | Area | Flows Covered |
|---|---|---|
| `00-preflight-db-safety.spec.ts` | Safety Guardrails | Preflight DB verification & credentials validation |
| `01-unauthorized-access.spec.ts` | Security / Guards | Unauthenticated route blocking and login redirection |
| `02-login-and-database-selection.spec.ts` | Authentication | Multi-year login (2026/2027), JWT `db` claim, invalid credentials |
| `03-dashboard.spec.ts` | Analytics | KPI cards, live employee counts, date filter interaction |
| `04-employee-list-and-search.spec.ts` | Employees | Pagination, sorting, dynamic deterministic search |
| `05-daily-and-forms.spec.ts` | Transactions | Daily batches, active Form list navigation, read-only details, archived forms |
| `06-departments.spec.ts` | Departments | Department master, headcount metrics, paginator |
| `07-watchlist.spec.ts` | Monitoring | Watchlist table, paginator interaction |
| `08-settings.spec.ts` | Settings | Module route resolution & layout integrity |
