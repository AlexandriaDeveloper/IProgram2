# Release Runbook — IProgram

This runbook outlines the standard operating procedure for validating, preparing, and deploying releases of **IProgram**.

---

## 1. System Architecture & Safety Principles

* **Backend:** ASP.NET Core on .NET 10 (`src/Api`, `src/Application`, `src/Infrastructure`, `src/Persistence`).
* **Frontend:** Angular 17 in `Client/`.
* **Database:** Azure SQL Database with dynamic database selection (`IDbConnectionProvider` for tenant databases such as `2026`, `2027`).
* **Core Rule — Zero Automatic Startup Migrations:**
  > **NEVER** run `Database.Migrate()` or `Database.EnsureCreated()` on application startup.
  > In a dynamic multi-database architecture, migrations must be deployed out-of-band using idempotent SQL scripts to ensure safety and auditability across all operational databases.

---

## 2. Pre-Release Verification Checklist

Before deploying any release to staging or production, verify that:

1. **GitHub Actions CI is Green:**
   - Verify that the workflow `.github/workflows/ci.yml` has succeeded on the PR or `master` commit.
   - Both `backend` (.NET 10 build & unit tests) and `frontend` (Angular 17 build) jobs must pass.

2. **Local Pre-Flight Checks (Optional / Operator Check):**
   ```bash
   # 1. Backend Build & Tests
   dotnet restore IProgram.sln
   dotnet build IProgram.sln --configuration Release
   dotnet test tests/Auth.UnitTests/Auth.UnitTests.csproj --configuration Release

   # 2. Frontend Build
   cd Client
   npm ci
   npm run build
   cd ..
   ```
   * All unit tests (238+ tests) must pass with 0 failures.

---

## 3. Database Migration Deployment (Multi-Database)

When a release includes schema or index migrations (e.g., `OptimizeHotPathIndexesSprint4B`):

### Step 3.1: Generate the Idempotent SQL Script
Run the automated script generator from the repository root:
```powershell
powershell -File script/generate-migration-script.ps1
```
* **Output:** `script/migration_idempotent.sql`
* **Idempotency Guarantee:** Every migration block begins with:
  ```sql
  IF NOT EXISTS (
      SELECT * FROM [__EFMigrationsHistory]
      WHERE [MigrationId] = N'<MigrationId>'
  )
  BEGIN
      ...
      INSERT INTO [__EFMigrationsHistory] ...
  END;
  ```
  This guarantees that applying the script multiple times or to databases at different migration levels is safe and will only execute unapplied migrations.

### Step 3.2: Inspect the SQL Script
Review `script/migration_idempotent.sql` to ensure:
- Only expected DDL operations (e.g. `CREATE INDEX`, `DROP INDEX`) are executed.
- No accidental `DROP TABLE` or destructive column alterations exist.

### Step 3.3: Execute on All Operational Databases
Connect to the database server (via SQL Server Management Studio or Azure Data Studio) using deployment credentials:
1. Target Database `2026`:
   ```sql
   USE [IProgram_2026]; -- Replace with actual database name
   GO
   -- Execute contents of script/migration_idempotent.sql
   ```
2. Target Database `2027`:
   ```sql
   USE [IProgram_2027]; -- Replace with actual database name
   GO
   -- Execute contents of script/migration_idempotent.sql
   ```
3. Repeat for any other operational databases.

### Step 3.4: Verify Migration History
Run on each database to verify the latest migration was recorded:
```sql
SELECT TOP 5 MigrationId, ProductVersion 
FROM [__EFMigrationsHistory] 
ORDER BY MigrationId DESC;
```

---

## 4. Application Deployment

### Step 4.1: Backend API Deployment
1. Publish the backend project:
   ```bash
   dotnet publish src/Api/Auth.Api.csproj -c Release -o ./publish/api
   ```
2. Deploy to Azure App Service / IIS deployment slot.
3. Warm up the application.

### Step 4.2: Frontend Deployment
1. Build the production Angular bundle:
   ```bash
   cd Client
   npm run build
   cd ..
   ```
2. Deploy static assets from `Client/dist/client/browser/` to the hosting environment (e.g. Azure Static Web Apps / CDN / IIS `wwwroot`).

---

## 5. Post-Deployment Verification (Smoke Tests)

Immediately following application deployment, perform smoke testing:

1. **Health Check Endpoint:**
   Send a request to the health check endpoint:
   ```bash
   curl -i https://<app-domain>/health
   ```
   * **Expected Response:** `HTTP/1.1 200 OK` with body `Healthy`.

2. **Core User Flows:**
   - Log in using valid credentials.
   - Switch between databases (e.g., 2026 and 2027) and verify data loads correctly.
   - Navigate to Dashboard and verify statistics aggregate properly.
   - Test Excel Import on a sample form to verify batch resolution and atomic save.

---

## 6. Rollback Protocols

In the event of an unrecoverable failure during deployment:

### Application Rollback
- **Azure App Service:** Swap deployment slots back to the previous stable build, or redeploy the previous release package.
- **Frontend:** Revert static assets on CDN / storage to the previous deployment.

### Database Migration Rollback
If a migration must be reverted:
1. Identify the target migration to revert to.
2. Generate the rollback script using `dotnet ef`:
   ```powershell
   dotnet ef migrations script <TargetMigrationToRollbackTo> <CurrentMigration> `
       --context ApplicationContext `
       --project src/Infrastructure `
       --startup-project src/Api `
       --output script/rollback.sql
   ```
3. Inspect `script/rollback.sql` to verify the `Down()` operations.
4. Execute `rollback.sql` on each affected operational database.
5. Verify `__EFMigrationsHistory` reflects the rollback.
