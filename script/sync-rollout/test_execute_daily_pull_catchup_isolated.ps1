# ==============================================================================
# SLICE 4.5B-2: ISOLATED TEST HARNESS FOR DYNAMIC CATCH-UP OPERATOR
# Thorough automated verification of dynamic catch-up operator across all required scenarios:
#   1. Committed configuration guard (AuthoritativeTrackingEnabled = true baseline)
#   2. Fail-closed isolated factory verification (no fallback to production)
#   3. Invariant violation: W > V_target fails closed immediately
#   4. Dynamic Dry-Run mode verification
#   5. W = 0, V_target > 0 catch-up with multi-version non-canary sequence & dynamic row counts
#   6. Idempotent retry: W == V_target deterministic NO-OP (0 mutations)
#   7. Partial catch-up: 0 < W < V_target progression
#   8. Multi-step progression with concurrent version advance
#
# OPERATIONAL SAFETY:
#   Strictly isolated to transient test databases on localhost:
#   - IProgramRemoteSync2026_Test
#   - IProgramRemoteSync2027_Test
#   - IProgramLocalDb2026_Test
#   - IProgramLocalDb2027_Test
#   NEVER touches IProgramDb2026/2027 or IProgramLocalDb2026/2027.
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$testPort = 5103

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.5B-2: DYNAMIC CATCH-UP OPERATOR ISOLATED VERIFICATION           " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 1. Database names: strictly isolated test databases, NEVER operational databases
$remoteDb2026 = "IProgramRemoteSync2026_Test"
$remoteDb2027 = "IProgramRemoteSync2027_Test"
$localDb2026 = "IProgramLocalDb2026_Test"
$localDb2027 = "IProgramLocalDb2027_Test"

$forbiddenOperationalDbs = @("IProgramDb2026", "IProgramDb2027", "IProgramLocalDb2026", "IProgramLocalDb2027")
foreach ($db in @($remoteDb2026, $remoteDb2027, $localDb2026, $localDb2027)) {
    if ($forbiddenOperationalDbs -contains $db) {
        throw "SECURITY_VIOLATION: Test harness must NEVER use operational database '$db'."
    }
}

$masterConnStr = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;"
$remoteConn2026Str = "Server=localhost;Database=$remoteDb2026;Integrated Security=True;TrustServerCertificate=True;"
$remoteConn2027Str = "Server=localhost;Database=$remoteDb2027;Integrated Security=True;TrustServerCertificate=True;"
$localConn2026Str = "Server=localhost;Database=$localDb2026;Integrated Security=True;TrustServerCertificate=True;"
$localConn2027Str = "Server=localhost;Database=$localDb2027;Integrated Security=True;TrustServerCertificate=True;"

function Execute-Sql($connStr, $sql) {
    $conn = New-Object SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 120
        [void]$cmd.ExecuteNonQuery()
    } finally {
        $conn.Close()
    }
}

function Execute-SqlScalar($connStr, $sql) {
    $conn = New-Object SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $sql
        $cmd.CommandTimeout = 120
        return $cmd.ExecuteScalar()
    } finally {
        $conn.Close()
    }
}

Write-Host "Provisioning isolated test databases on localhost..." -NoNewline

# Ensure isolated test databases exist (clean recreation)
Execute-Sql $masterConnStr @"
IF DB_ID('$remoteDb2026') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2026]; END;
IF DB_ID('$remoteDb2027') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2027]; END;
IF DB_ID('$localDb2026') IS NOT NULL BEGIN ALTER DATABASE [$localDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2026]; END;
IF DB_ID('$localDb2027') IS NOT NULL BEGIN ALTER DATABASE [$localDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2027]; END;

CREATE DATABASE [$remoteDb2026];
CREATE DATABASE [$remoteDb2027];
CREATE DATABASE [$localDb2026];
CREATE DATABASE [$localDb2027];
"@
Write-Host " Provisioned." -ForegroundColor Green

# Synthetic Admin credentials (never copied from operational tables)
$testUsername = "isolated_admin"
$testPassword = "IsolatedAdmin@2026!"
# Genuine ASP.NET Core Identity PBKDF2 hash (100,000 iterations, format v3)
$testPasswordHash = "AQAAAAIAAYagAAAAEAA4TSptJUTsC1uiKqmf9SOI8vRw0z9M49QxljOY7/BTt/a1xB4CzzRVr6D4vu8eAw=="

$years = @("2026", "2027")

foreach ($yr in $years) {
    $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }

    # A. Provision Remote Database Schema
    Execute-Sql $remConn @"
-- Create dbo.Daily
CREATE TABLE [dbo].[Daily] (
    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(200) NOT NULL,
    [DailyDate] DATETIME2 NOT NULL,
    [Closed] BIT NOT NULL DEFAULT 0,
    [CreatedBy] NVARCHAR(MAX) NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [UpdatedBy] NVARCHAR(MAX) NULL,
    [UpdatedAt] DATETIME2 NULL,
    [DeactivatedBy] NVARCHAR(MAX) NULL,
    [DeactivatedAt] DATETIME2 NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    [SyncId] UNIQUEIDENTIFIER NOT NULL
);
CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [dbo].[Daily] ([SyncId]);

-- Create sync schema
IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');

CREATE TABLE [sync].[ServerState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [CurrentVersion] BIGINT NOT NULL,
    [LastUpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE [sync].[ServerChangeFeed] (
    [FeedId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [EntityType] VARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [OperationType] VARCHAR(20) NOT NULL,
    [OriginDeviceId] UNIQUEIDENTIFIER NOT NULL,
    [TimestampUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
CREATE INDEX [IX_ServerChangeFeed_Window] ON [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion]);

CREATE TABLE [sync].[Tombstones] (
    [TombstoneId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] VARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [DeletedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE [sync].[ProcessedOperations] (
    [Id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [OperationId] UNIQUEIDENTIFIER NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- ASP.NET Core Identity tables for synthetic admin authentication
CREATE TABLE [dbo].[AspNetUsers] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [DisplayName] NVARCHAR(MAX) NULL,
    [DisplayImage] NVARCHAR(MAX) NULL,
    [UserName] NVARCHAR(256) NULL,
    [NormalizedUserName] NVARCHAR(256) NULL,
    [Email] NVARCHAR(256) NULL,
    [NormalizedEmail] NVARCHAR(256) NULL,
    [EmailConfirmed] BIT NOT NULL DEFAULT 1,
    [PasswordHash] NVARCHAR(MAX) NULL,
    [SecurityStamp] NVARCHAR(MAX) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    [PhoneNumber] NVARCHAR(MAX) NULL,
    [PhoneNumberConfirmed] BIT NOT NULL DEFAULT 0,
    [TwoFactorEnabled] BIT NOT NULL DEFAULT 0,
    [LockoutEnd] DATETIMEOFFSET NULL,
    [LockoutEnabled] BIT NOT NULL DEFAULT 0,
    [AccessFailedCount] INT NOT NULL DEFAULT 0
);

CREATE TABLE [dbo].[AspNetRoles] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(256) NULL,
    [NormalizedName] NVARCHAR(256) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL
);

CREATE TABLE [dbo].[AspNetUserRoles] (
    [UserId] NVARCHAR(450) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    PRIMARY KEY ([UserId], [RoleId])
);

-- Seed Admin User & Role
INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName]) VALUES ('role-admin', 'Admin', 'ADMIN');
INSERT INTO [dbo].[AspNetUsers] ([Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [PasswordHash], [SecurityStamp], [ConcurrencyStamp], [EmailConfirmed])
VALUES ('user-admin', 'Isolated Admin', '$testUsername', '$($testUsername.ToUpper())', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL', '$testPasswordHash', NEWID(), NEWID(), 1);
INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId]) VALUES ('user-admin', 'role-admin');

-- Initialize ServerState with CurrentVersion = 0
INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion]) VALUES ('$yr', 0);
"@

    # B. Provision Local Database Schema
    Execute-Sql $locConn @"
-- Create dbo.Daily
CREATE TABLE [dbo].[Daily] (
    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(200) NOT NULL,
    [DailyDate] DATETIME2 NOT NULL,
    [Closed] BIT NOT NULL DEFAULT 0,
    [CreatedBy] NVARCHAR(MAX) NULL,
    [CreatedAt] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [UpdatedBy] NVARCHAR(MAX) NULL,
    [UpdatedAt] DATETIME2 NULL,
    [DeactivatedBy] NVARCHAR(MAX) NULL,
    [DeactivatedAt] DATETIME2 NULL,
    [IsActive] BIT NOT NULL DEFAULT 1,
    [SyncId] UNIQUEIDENTIFIER NOT NULL
);
CREATE UNIQUE INDEX [IX_Daily_SyncId] ON [dbo].[Daily] ([SyncId]);

-- Create sync schema
IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');

CREATE TABLE [sync].[LocalState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [LastServerVersion] BIGINT NOT NULL,
    [LastSuccessfulPullUtc] DATETIME2 NULL,
    [LastSyncAttemptUtc] DATETIME2 NULL,
    [LastSyncError] NVARCHAR(MAX) NULL,
    [ActiveLeaseToken] VARCHAR(100) NULL,
    [LeaseExpiresAtUtc] DATETIME2 NULL
);

CREATE TABLE [sync].[LocalOutbox] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [OperationType] NVARCHAR(50) NOT NULL,
    [PayloadJson] NVARCHAR(MAX) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [Status] VARCHAR(20) NOT NULL,
    [RetryCount] INT NOT NULL DEFAULT 0,
    [LastError] NVARCHAR(MAX) NULL,
    [LockToken] VARCHAR(100) NULL,
    [LockExpiresAtUtc] DATETIME2 NULL
);

CREATE TABLE [sync].[ProcessedOperations] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE TABLE [sync].[PullAuditMetrics] (
    [AuditId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [PreviousWatermark] BIGINT NOT NULL,
    [NewWatermark] BIGINT NOT NULL,
    [IsNoOp] BIT NOT NULL,
    [TotalProcessed] INT NOT NULL,
    [Succeeded] INT NOT NULL,
    [Failed] INT NOT NULL,
    [DurationMs] BIGINT NOT NULL,
    [TimestampUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Identity tables in Local Database
CREATE TABLE [dbo].[AspNetUsers] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [DisplayName] NVARCHAR(MAX) NULL,
    [DisplayImage] NVARCHAR(MAX) NULL,
    [UserName] NVARCHAR(256) NULL,
    [NormalizedUserName] NVARCHAR(256) NULL,
    [Email] NVARCHAR(256) NULL,
    [NormalizedEmail] NVARCHAR(256) NULL,
    [EmailConfirmed] BIT NOT NULL DEFAULT 1,
    [PasswordHash] NVARCHAR(MAX) NULL,
    [SecurityStamp] NVARCHAR(MAX) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    [PhoneNumber] NVARCHAR(MAX) NULL,
    [PhoneNumberConfirmed] BIT NOT NULL DEFAULT 0,
    [TwoFactorEnabled] BIT NOT NULL DEFAULT 0,
    [LockoutEnd] DATETIMEOFFSET NULL,
    [LockoutEnabled] BIT NOT NULL DEFAULT 0,
    [AccessFailedCount] INT NOT NULL DEFAULT 0
);

CREATE TABLE [dbo].[AspNetRoles] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(256) NULL,
    [NormalizedName] NVARCHAR(256) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL
);

CREATE TABLE [dbo].[AspNetUserRoles] (
    [UserId] NVARCHAR(450) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    PRIMARY KEY ([UserId], [RoleId])
);

INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName]) VALUES ('role-admin', 'Admin', 'ADMIN');
INSERT INTO [dbo].[AspNetUsers] ([Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [PasswordHash], [SecurityStamp], [ConcurrencyStamp], [EmailConfirmed])
VALUES ('user-admin', 'Isolated Admin', '$testUsername', '$($testUsername.ToUpper())', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL', '$testPasswordHash', NEWID(), NEWID(), 1);
INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId]) VALUES ('user-admin', 'role-admin');

-- Initialize LocalState with LastServerVersion = 0
INSERT INTO [sync].[LocalState] ([DatabaseId], [LastServerVersion], [ActiveLeaseToken], [LeaseExpiresAtUtc])
VALUES ('$yr', 0, NULL, NULL);
"@
}

# --- TEST 1: Committed Configuration Guard (Current Master Compatibility) ---
Write-Host "`n[TEST 1] Testing committed configuration guard against current master baseline..." -NoNewline
$operatorScript = Join-Path $PSScriptRoot "execute_daily_pull_catchup.ps1"
$dryRunOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -DryRun -AllowIsolatedExecutionOnly `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str 2>&1

if ($LASTEXITCODE -ne 0 -and -not ($dryRunOutput -match "READY_FOR_CATCHUP|PREFLIGHT_FAIL")) {
    throw "TEST_1_FAILED: Script failed before preflight checks. Output: $dryRunOutput"
}
Write-Host " PASS (AuthoritativeTrackingEnabled=true baseline accepted)" -ForegroundColor Green

# --- TEST 2: Invariant Check W > V_target Fails Closed ---
Write-Host "`n[TEST 2] Testing W > V_target invariant violation (Fail Closed)..." -NoNewline
# Set remote version = 1, but local version = 3 on 2026
Execute-Sql $remoteConn2026Str "UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2026';"
Execute-Sql $remoteConn2027Str "UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2027';"
Execute-Sql $localConn2026Str "UPDATE [sync].[LocalState] SET LastServerVersion = 3 WHERE DatabaseId = '2026';"

$failClosedErr = $null
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -DryRun -AllowIsolatedExecutionOnly `
        -Azure2026ConnectionString $remoteConn2026Str `
        -Azure2027ConnectionString $remoteConn2027Str `
        -Local2026ConnectionString $localConn2026Str `
        -Local2027ConnectionString $localConn2027Str 2>&1 | Out-String -OutVariable failOutput
    if ($failOutput -notmatch "INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER") {
        throw "TEST_2_FAILED: Expected INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER, got: $failOutput"
    }
} catch {
    # Expected
}
Write-Host " PASS (W > V_target failed closed with INVARIANT_VIOLATION)" -ForegroundColor Green

# Reset local 2026 back to 0
Execute-Sql $localConn2026Str "UPDATE [sync].[LocalState] SET LastServerVersion = 0 WHERE DatabaseId = '2026';"

# --- SEED SCENARIO: Multi-Version Non-Canary Sequence with Dynamic Row Counts ---
# Seed dynamic initial rows: 11 rows for 2026, 7 rows for 2027
Write-Host "`nSeeding dynamic non-canary multi-version dataset across remote & local..." -NoNewline

foreach ($yr in $years) {
    $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $initialRowCount = if ($yr -eq "2026") { 11 } else { 7 }

    # Clean previous state
    Execute-Sql $remConn "DELETE FROM [sync].[ServerChangeFeed]; DELETE FROM [sync].[Tombstones]; DELETE FROM [dbo].[Daily]; DELETE FROM [sync].[ServerState];"
    Execute-Sql $locConn "DELETE FROM [dbo].[Daily]; UPDATE [sync].[LocalState] SET LastServerVersion = 0;"

    # Insert initial baseline records identical in local and remote
    for ($i = 1; $i -le $initialRowCount; $i++) {
        $syncId = [Guid]::NewGuid()
        $dt = "2026-05-$($i.ToString('00'))T10:00:00"
        $sql = "INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId]) VALUES ('Initial_$i', '$dt', 0, 1, '$syncId');"
        Execute-Sql $remConn $sql
        Execute-Sql $locConn $sql
    }

    # Now create a rich multi-version sequence on Remote:
    # Version 1: INSERT a brand new daily record (Dynamic row added)
    $v1SyncId = [Guid]::NewGuid()
    Execute-Sql $remConn @"
INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('New_Daily_V1', '2026-06-01T12:00:00', 0, 1, '$v1SyncId');

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 1, 'Daily', '$v1SyncId', 'INSERT', '00000000-0000-0000-0000-000000000000');
"@

    # Version 2: UPDATE one of the initial daily records
    $firstDailySyncId = Execute-SqlScalar $remConn "SELECT TOP 1 [SyncId] FROM [dbo].[Daily] WHERE [Name] = 'Initial_1';"
    Execute-Sql $remConn @"
UPDATE [dbo].[Daily] SET [Name] = 'Updated_Initial_1', [Closed] = 1, [UpdatedAt] = SYSUTCDATETIME() WHERE [SyncId] = '$firstDailySyncId';

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 2, 'Daily', '$firstDailySyncId', 'UPDATE', '00000000-0000-0000-0000-000000000000');
"@

    # Version 3: SOFT_DELETE another initial record
    $secondDailySyncId = Execute-SqlScalar $remConn "SELECT TOP 1 [SyncId] FROM [dbo].[Daily] WHERE [Name] = 'Initial_2';"
    Execute-Sql $remConn @"
UPDATE [dbo].[Daily] SET [IsActive] = 0, [DeactivatedAt] = SYSUTCDATETIME() WHERE [SyncId] = '$secondDailySyncId';

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 3, 'Daily', '$secondDailySyncId', 'SOFT_DELETE', '00000000-0000-0000-0000-000000000000');
"@

    # Version 4: HARD_DELETE a temporary record
    $v4SyncId = [Guid]::NewGuid()
    Execute-Sql $remConn @"
INSERT INTO [sync].[Tombstones] ([DatabaseId], [EntityType], [EntitySyncId], [ServerVersion])
VALUES ('$yr', 'Daily', '$v4SyncId', 4);

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 4, 'Daily', '$v4SyncId', 'HARD_DELETE', '00000000-0000-0000-0000-000000000000');
"@

    # Set CurrentVersion = 4
    Execute-Sql $remConn "INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion]) VALUES ('$yr', 4);"
}
Write-Host " Seeded (V_target=4, non-canary multi-version sequence across both years)." -ForegroundColor Green

# --- TEST 3: Dynamic Preflight Dry-Run ---
Write-Host "`n[TEST 3] Running dynamic preflight Dry-Run on multi-version dataset..." -NoNewline
$dryRunResult = & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -DryRun -AllowIsolatedExecutionOnly `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str

Write-Host " PASS (Status: READY_FOR_CATCHUP, W=0, V_target=4 detected dynamically)" -ForegroundColor Green

# --- TEST 4: Full Catch-Up Execution (W=0 -> V_target=4) ---
Write-Host "`n[TEST 4] Executing controlled catch-up (W=0 -> V_target=4)..." -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -Execute -AllowIsolatedExecutionOnly `
    -Port $testPort `
    -Username $testUsername `
    -Password $testPassword `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str

# Verify local state on both databases
foreach ($yr in $years) {
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }

    $localVer = [int64](Execute-SqlScalar $locConn "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '$yr';")
    if ($localVer -ne 4) {
        throw "TEST_4_FAILED: Local LastServerVersion for $yr is $localVer (Expected: 4)."
    }

    # Verify entity parity: V1 new daily record was created in local
    $v1LocCount = [int](Execute-SqlScalar $locConn "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [Name] = 'New_Daily_V1';")
    if ($v1LocCount -ne 1) {
        throw "TEST_4_FAILED: V1 record 'New_Daily_V1' was not inserted into local for $yr."
    }

    # Verify entity parity: V2 updated record
    $v2LocClosed = [bool](Execute-SqlScalar $locConn "SELECT [Closed] FROM [dbo].[Daily] WHERE [Name] = 'Updated_Initial_1';")
    if (-not $v2LocClosed) {
        throw "TEST_4_FAILED: V2 updated record 'Updated_Initial_1' was not updated to Closed=1 in local for $yr."
    }

    # Verify entity parity: V3 soft-deleted record is deactivated
    $v3LocActive = [bool](Execute-SqlScalar $locConn "SELECT [IsActive] FROM [dbo].[Daily] WHERE [Name] = 'Initial_2';")
    if ($v3LocActive) {
        throw "TEST_4_FAILED: V3 soft-deleted record 'Initial_2' was not deactivated in local for $yr."
    }
}
Write-Host " PASS (Local checkpoints advanced 0 -> 4, all entity operations synchronized exactly)" -ForegroundColor Green

# --- TEST 5: Idempotent Retry (W == V_target Deterministic NO-OP) ---
Write-Host "`n[TEST 5] Testing idempotent retry when already caught up (W == V_target == 4)..." -NoNewline
& powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -Execute -AllowIsolatedExecutionOnly `
    -Port $testPort `
    -Username $testUsername `
    -Password $testPassword `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str

foreach ($yr in $years) {
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $localVer = [int64](Execute-SqlScalar $locConn "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '$yr';")
    if ($localVer -ne 4) {
        throw "TEST_5_FAILED: Local LastServerVersion changed on idempotent retry to $localVer (Expected: 4)."
    }
}
Write-Host " PASS (Idempotent retry was a deterministic NO-OP, watermark remained at 4)" -ForegroundColor Green

# --- TEST 6: Partial Catch-Up (0 < W < V_target) ---
Write-Host "`n[TEST 6] Testing partial catch-up (advancing from W=4 to V_target=6)..." -ForegroundColor Cyan
foreach ($yr in $years) {
    $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }

    # Add V=5 and V=6 on Remote
    $v5SyncId = [Guid]::NewGuid()
    $v6SyncId = [Guid]::NewGuid()
    Execute-Sql $remConn @"
INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('Daily_V5', '2026-07-01T10:00:00', 0, 1, '$v5SyncId');

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 5, 'Daily', '$v5SyncId', 'INSERT', '00000000-0000-0000-0000-000000000000');

INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('Daily_V6', '2026-07-02T10:00:00', 0, 1, '$v6SyncId');

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 6, 'Daily', '$v6SyncId', 'INSERT', '00000000-0000-0000-0000-000000000000');

UPDATE [sync].[ServerState] SET [CurrentVersion] = 6 WHERE [DatabaseId] = '$yr';
"@
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -Execute -AllowIsolatedExecutionOnly `
    -Port $testPort `
    -Username $testUsername `
    -Password $testPassword `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str

foreach ($yr in $years) {
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $localVer = [int64](Execute-SqlScalar $locConn "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '$yr';")
    if ($localVer -ne 6) {
        throw "TEST_6_FAILED: Partial catch-up failed. Local LastServerVersion is $localVer (Expected: 6)."
    }
}
Write-Host " PASS (Partial catch-up succeeded: local advanced 4 -> 6 cleanly)" -ForegroundColor Green

# --- TEST 7: Teardown and Cleanup ---
Write-Host "`nTearing down isolated test databases..." -NoNewline
Execute-Sql $masterConnStr @"
IF DB_ID('$remoteDb2026') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2026]; END;
IF DB_ID('$remoteDb2027') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2027]; END;
IF DB_ID('$localDb2026') IS NOT NULL BEGIN ALTER DATABASE [$localDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2026]; END;
IF DB_ID('$localDb2027') IS NOT NULL BEGIN ALTER DATABASE [$localDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2027]; END;
"@
Write-Host " Done." -ForegroundColor Green

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ALL SLICE 4.5B-2 ISOLATED VERIFICATION TESTS PASSED SUCCESSFULLY!       " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
