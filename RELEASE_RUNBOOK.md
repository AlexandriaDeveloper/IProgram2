# Release Runbook — IProgram

This runbook outlines the standard operating procedure for validating, preparing, and deploying releases of **IProgram**.

---

## 1. System Architecture & Safety Principles

* **Backend:** ASP.NET Core on .NET 10 (`src/Api`, `src/Application`, `src/Infrastructure`, `src/Persistence`).
* **Frontend:** Angular 17 in `Client/`.
* **Database:** Azure SQL Database with dynamic database selection (`IDbConnectionProvider` for tenant databases such as `2026`, `2027`).
* **Core Rule — Zero Automatic Startup Migrations:**
  > **NEVER** run `Database.Migrate()` or `Database.EnsureCreated()` on application startup.
  > In a dynamic multi-database architecture, migrations must be deployed out-of-band using controlled, targeted SQL scripts to ensure safety and auditability across all operational databases.

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

## 3. Database Migration Deployment (Multi-Database & Sprint 4B Targeted)

> [!IMPORTANT]
> **No Absolute Idempotency Guarantee:**
> EF Core idempotent scripts rely strictly on the accuracy of the `[__EFMigrationsHistory]` table.
> If the database history table is inconsistent with the physical schema:
> **STOP — DO NOT run the script automatically.** Manual database administrator inspection is required.

When deploying Sprint 4B index optimizations (`20260918185849_OptimizeHotPathIndexesSprint4B`):

### Step 3.1: Sprint 4B Targeted Migration Command
Do NOT generate or apply a full-history migration script to existing operational databases. Generate only the targeted delta between the preceding migration (`20260917213000_AddSummaryReviewMethod`) and Sprint 4B:

```powershell
# Directly via dotnet ef CLI:
dotnet ef migrations script `
  20260917213000_AddSummaryReviewMethod `
  20260918185849_OptimizeHotPathIndexesSprint4B `
  --idempotent `
  --context ApplicationContext `
  --project src/Infrastructure `
  --startup-project src/Api `
  --output <operator-selected-path>
```

Or using the automated repository helper script:
```powershell
powershell -File script/generate-migration-script.ps1 -OutputFile script/sprint4b_targeted_migration.sql
```

### Step 3.2: Inspect the Generated SQL Script
Review the generated SQL file prior to execution. For Sprint 4B, the script must contain **ONLY**:
1. `DROP INDEX [IX_FormDetails_FormId] ON [FormDetails];`
2. `DROP INDEX [IX_Form_DailyId] ON [Form];`
3. `CREATE INDEX [IX_FormDetails_FormId_IsActive_EmployeeId] ON [FormDetails] ([FormId], [IsActive], [EmployeeId]) INCLUDE ([Amount], [OrderNum]);`
4. `CREATE INDEX [IX_Form_DailyId_IsActive_Index] ON [Form] ([DailyId], [IsActive], [Index]);`
5. `CREATE INDEX [IX_Form_IsActive_CreatedAt] ON [Form] ([IsActive], [CreatedAt]);`
6. `INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion]) VALUES (N'20260918185849_OptimizeHotPathIndexesSprint4B', N'8.0.11');`

**Verify Absence of Destructive Statements:**
- NO `DROP TABLE`
- NO `DROP COLUMN`
- NO `ALTER COLUMN`
- NO data transformation or DML mutations

### Step 3.3: Backup & Point-in-Time Restore (PITR) Pre-Deployment Verification
Before executing DDL on any operational database:
1. **Verify Restore Capability:**
   Verify that Azure SQL Database automated backup retention and Point-in-Time Restore (PITR) are available and healthy on the target server.
2. **Record Pre-Migration Deployment Metadata:**
   Record the following operational metadata in deployment audit records:
   - Target database name (e.g., `IProgram_2026`, `IProgram_2027`)
   - Azure SQL Logical Server name
   - Pre-migration deployment timestamp in UTC (`YYYY-MM-DD HH:mm:ssZ`) and Local Time
   - Available restore window (earliest and latest restore points)
3. **Mandatory Pre-DDL Gate:**
   > **DO NOT** begin DDL operations until the rollback/restore path is verified and deployment timestamp is logged.

### Step 3.4: Preflight Inspection on Operational Databases (Read-Only)
For each operational database (e.g. `2026`, `2027`):

> [!WARNING]
> In Azure SQL Database, **do NOT use `USE [DatabaseName]`** to switch databases.
> Always open a direct database connection targeting the specific operational database, and verify the connection context (`SELECT DB_NAME()`) before executing any queries.

#### A. Migration History Preflight
Execute the following read-only query:
```sql
SELECT DB_NAME() AS CurrentDatabase;

-- Verify migration history state
SELECT MigrationId, ProductVersion
FROM [__EFMigrationsHistory]
WHERE MigrationId IN (
    '20260917213000_AddSummaryReviewMethod',
    '20260918185849_OptimizeHotPathIndexesSprint4B'
)
ORDER BY MigrationId;
```

**Migration History Validation Rules:**
1. `20260917213000_AddSummaryReviewMethod` **MUST** exist in the result.
2. `20260918185849_OptimizeHotPathIndexesSprint4B` **MUST NOT** exist in the result.
3. If rule 1 or 2 is violated: **STOP immediately**. Investigate history divergence before proceeding.

#### B. Physical Index State Preflight (Read-Only Schema Inspection)
Execute the following read-only query using `sys.indexes` and `sys.tables` to verify current physical index state:
```sql
SELECT 
    t.name AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique
FROM sys.indexes i
JOIN sys.tables t ON i.object_id = t.object_id
WHERE (t.name = 'FormDetails' AND i.name IN ('IX_FormDetails_FormId', 'IX_FormDetails_FormId_IsActive_EmployeeId'))
   OR (t.name = 'Form' AND i.name IN ('IX_Form_DailyId', 'IX_Form_DailyId_IsActive_Index', 'IX_Form_IsActive_CreatedAt'))
ORDER BY t.name, i.name;
```

**Physical Index Pre-Sprint 4B Validation Rules:**
* **MUST EXIST physically:**
  - `IX_FormDetails_FormId` on `[FormDetails]`
  - `IX_Form_DailyId` on `[Form]`
* **MUST NOT EXIST physically:**
  - `IX_FormDetails_FormId_IsActive_EmployeeId` on `[FormDetails]`
  - `IX_Form_DailyId_IsActive_Index` on `[Form]`
  - `IX_Form_IsActive_CreatedAt` on `[Form]`

> [!CAUTION]
> If physical index state does NOT match these exact preconditions:
> **STOP — investigate schema drift before executing the migration.**
> Do NOT attempt automatic repair.

### Step 3.5: Execute Targeted Script on Operational Databases
Once both preflights pass:
1. Open a direct connection to operational database `2026`.
2. Execute the verified targeted script.
3. Open a direct connection to operational database `2027`.
4. Execute the verified targeted script.
5. Repeat for any other operational databases.

### Step 3.6: Post-Migration Verification
Run on each database to confirm registration and physical index creation:
```sql
-- 1. Migration History
SELECT TOP 3 MigrationId, ProductVersion 
FROM [__EFMigrationsHistory] 
ORDER BY MigrationId DESC;

-- 2. Physical Index State Post-Migration
SELECT 
    t.name AS TableName,
    i.name AS IndexName
FROM sys.indexes i
JOIN sys.tables t ON i.object_id = t.object_id
WHERE (t.name = 'FormDetails' AND i.name = 'IX_FormDetails_FormId_IsActive_EmployeeId')
   OR (t.name = 'Form' AND i.name IN ('IX_Form_DailyId_IsActive_Index', 'IX_Form_IsActive_CreatedAt'))
ORDER BY t.name, i.name;
```
Expected:
- Top migration history record: `20260918185849_OptimizeHotPathIndexesSprint4B`.
- All 3 new composite indexes exist physically on `[FormDetails]` and `[Form]`.
- Old single-column indexes `IX_FormDetails_FormId` and `IX_Form_DailyId` no longer exist.

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
If Sprint 4B indexes must be reverted:
1. Generate the targeted rollback script using `dotnet ef`:
   ```powershell
   dotnet ef migrations script `
       20260918185849_OptimizeHotPathIndexesSprint4B `
       20260917213000_AddSummaryReviewMethod `
       --idempotent `
       --context ApplicationContext `
       --project src/Infrastructure `
       --startup-project src/Api `
       --output script/rollback_sprint4b.sql
   ```
2. Inspect `script/rollback_sprint4b.sql` to verify the `Down()` operations (drops new composite indexes and recreates original single-column indexes).
3. Connect directly to each operational database and execute `rollback_sprint4b.sql`.
4. Verify `__EFMigrationsHistory` reflects the removal of `20260918185849_OptimizeHotPathIndexesSprint4B`.
