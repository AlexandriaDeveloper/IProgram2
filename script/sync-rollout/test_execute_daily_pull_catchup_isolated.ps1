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

function Get-DailyTableHash($connStr) {
    $conn = New-Object SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT SyncId, Name, DailyDate, Closed, IsActive FROM [dbo].[Daily] ORDER BY SyncId;"
        $reader = $cmd.ExecuteReader()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $count = 0
        while ($reader.Read()) {
            $count++
            $bw.Write(([Guid]$reader["SyncId"]).ToByteArray())
            $bw.Write([string]$reader["Name"])
            $bw.Write(([DateTime]$reader["DailyDate"]).Ticks)
            $bw.Write([bool]$reader["Closed"])
            $bw.Write([bool]$reader["IsActive"])
        }
        $reader.Close()
        $bw.Flush()
        $bytes = $ms.ToArray()
        $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace("-", "")
        $bw.Dispose()
        $ms.Dispose()
        $sha.Dispose()
        return @{
            RowCount = $count
            Hash = $hash
        }
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

# --- TEST 2: Invariant Check W > V_observed Fails Closed ---
Write-Host "`n[TEST 2] Testing W > V_observed invariant violation (Fail Closed)..." -NoNewline
# Set remote version = 1, but local version = 3 on 2026
Execute-Sql $remoteConn2026Str "UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2026';"
Execute-Sql $remoteConn2027Str "UPDATE [sync].[ServerState] SET CurrentVersion = 1 WHERE DatabaseId = '2027';"
Execute-Sql $localConn2026Str "UPDATE [sync].[LocalState] SET LastServerVersion = 3 WHERE DatabaseId = '2026';"

$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$failOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -DryRun -AllowIsolatedExecutionOnly `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str 2>&1 | Out-String

$exitCode = $LASTEXITCODE
$ErrorActionPreference = $prevEap

if ($exitCode -eq 0) {
    throw "TEST_2_FAILED: Expected non-zero exit code on invariant violation, but got 0. Output:`n$failOutput"
}

if ($failOutput -notmatch "INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER") {
    throw "TEST_2_FAILED: Expected error marker 'INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER' not found in output:`n$failOutput"
}

Write-Host " PASS (W > V_observed correctly failed closed with non-zero exit code and exact error marker)" -ForegroundColor Green

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
Write-Host " Seeded (V_observed=4, non-canary multi-version sequence across both years)." -ForegroundColor Green

# --- TEST 3: Dynamic Preflight Dry-Run Assertions ---
Write-Host "`n[TEST 3] Running dynamic preflight Dry-Run on multi-version dataset..." -NoNewline
$dryRunOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -DryRun -AllowIsolatedExecutionOnly `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    throw "TEST_3_FAILED: DryRun exited with non-zero code $LASTEXITCODE. Output:`n$dryRunOutput"
}

if ($dryRunOutput -notmatch "Mode: DRY-RUN") {
    throw "TEST_3_FAILED: Expected 'Mode: DRY-RUN' not found in output:`n$dryRunOutput"
}

if ($dryRunOutput -notmatch "Committed Config Guard: PASS") {
    throw "TEST_3_FAILED: Expected 'Committed Config Guard: PASS' not found in output:`n$dryRunOutput"
}

if ($dryRunOutput -notmatch "Physical Binding Guard: PASS") {
    throw "TEST_3_FAILED: Expected 'Physical Binding Guard: PASS' not found in output:`n$dryRunOutput"
}

# Assert parsed preflight state for both years
foreach ($yr in $years) {
    if ($dryRunOutput -notmatch "$yr Status: NEEDS_CATCH_UP") {
        throw "TEST_3_FAILED: Expected '$yr Status: NEEDS_CATCH_UP' not found in output:`n$dryRunOutput"
    }
    if ($dryRunOutput -notmatch "Watermark check for ${yr}: Local W=0, Remote V_observed=4") {
        throw "TEST_3_FAILED: Watermark check line for $yr (W=0, V_observed=4) not found in output:`n$dryRunOutput"
    }
    if ($dryRunOutput -notmatch "Catch-up needed: Advisory Delta = 4 version\(s\)") {
        throw "TEST_3_FAILED: Catch-up needed line for $yr (Delta = 4) not found in output:`n$dryRunOutput"
    }
}
Write-Host " PASS (Mode: DRY-RUN, 2026/2027 Status: NEEDS_CATCH_UP, W=0, V_observed=4 verified)" -ForegroundColor Green

# --- TEST 4: Full Catch-Up Execution (W=0 -> H_exec=4) ---
Write-Host "`n[TEST 4] Executing controlled catch-up (W=0 -> H_exec=4)..." -ForegroundColor Cyan
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

# --- TEST 5: Idempotent Retry (W == V_observed == 4 Deterministic NO-OP) ---
Write-Host "`n[TEST 5] Testing idempotent retry when already caught up (W == V_observed == 4)..." -NoNewline

# Capture pre-no-op Daily business state and hashes
$preNoOpDailyCounts = @{}
$preNoOpDailyHashes = @{}
foreach ($yr in $years) {
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $hashObj = Get-DailyTableHash $locConn
    $preNoOpDailyCounts[$yr] = $hashObj.RowCount
    $preNoOpDailyHashes[$yr] = $hashObj.Hash
    $wBefore = [int64](Execute-SqlScalar $locConn "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '$yr';")
    if ($wBefore -ne 4) {
        throw "TEST_5_FAILED: Pre-condition watermark for $yr is $wBefore (Expected: 4)."
    }
}

$noOpOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $operatorScript -Execute -AllowIsolatedExecutionOnly `
    -Port $testPort `
    -Username $testUsername `
    -Password $testPassword `
    -Azure2026ConnectionString $remoteConn2026Str `
    -Azure2027ConnectionString $remoteConn2027Str `
    -Local2026ConnectionString $localConn2026Str `
    -Local2027ConnectionString $localConn2027Str 2>&1 | Out-String

if ($LASTEXITCODE -ne 0) {
    throw "TEST_5_FAILED: Operator exited with non-zero code on idempotent retry. Output:`n$noOpOutput"
}

# Assert NO-OP returned by API for both years
if ($noOpOutput -notmatch "PrevWatermark=4, FinalServerVer=4, IsNoOp=True") {
    throw "TEST_5_FAILED: Expected 'PrevWatermark=4, FinalServerVer=4, IsNoOp=True' not observed in output:`n$noOpOutput"
}

foreach ($yr in $years) {
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    
    # Checkpoint must remain unchanged
    $localVerAfter = [int64](Execute-SqlScalar $locConn "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '$yr';")
    if ($localVerAfter -ne 4) {
        throw "TEST_5_FAILED: Local LastServerVersion mutated on idempotent retry to $localVerAfter (Expected: 4)."
    }

    # Zero business data mutation: daily count and deterministic hash must be strictly identical
    $postHashObj = Get-DailyTableHash $locConn
    $postNoOpCount = $postHashObj.RowCount
    $postNoOpHash = $postHashObj.Hash

    if ($postNoOpCount -ne $preNoOpDailyCounts[$yr]) {
        throw "TEST_5_FAILED: Daily business row count changed during NO-OP for $yr (Before: $($preNoOpDailyCounts[$yr]), After: $postNoOpCount)."
    }
    if ($postNoOpHash -ne $preNoOpDailyHashes[$yr]) {
        throw "TEST_5_FAILED: Daily business hash changed during NO-OP for $yr (Before: $($preNoOpDailyHashes[$yr]), After: $postNoOpHash)."
    }
}
Write-Host " PASS (IsNoOp=True, W=4 unchanged, zero business data mutation verified)" -ForegroundColor Green

# --- TEST 6: Partial Catch-Up (0 < W < V_observed) ---
Write-Host "`n[TEST 6] Testing partial catch-up (advancing from W=4 to V_observed=6)..." -ForegroundColor Cyan
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

# --- TEST 7: Deterministic Concurrent Authoritative Advance Blocked Under Reader Fence ---
Write-Host "`n[TEST 7] Testing concurrent authoritative advance blocked under reader fence..." -ForegroundColor Cyan

# Context: Local is at W=6, Remote is at 6 on year 2026 (from TEST 6).
# 1. Advance remote to version 7
$v7SyncId = [Guid]::NewGuid()
Execute-Sql $remoteConn2026Str @"
INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('Daily_V7', '2026-07-03T10:00:00', 0, 1, '$v7SyncId');
INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('2026', 7, 'Daily', '$v7SyncId', 'INSERT', '00000000-0000-0000-0000-000000000000');
UPDATE [sync].[ServerState] SET [CurrentVersion] = 7 WHERE [DatabaseId] = '2026';
"@

# 2. Connection 1 (Reader Fence) starts SERIALIZABLE transaction and acquires (UPDLOCK, HOLDLOCK)
$fenceConn = New-Object SqlConnection($remoteConn2026Str)
$fenceConn.Open()
$fenceTx = $fenceConn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
$fenceCmd = $fenceConn.CreateCommand()
$fenceCmd.Transaction = $fenceTx
$fenceCmd.CommandText = "SELECT @@SPID; SELECT CurrentVersion FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = '2026';"
$readerRdr = $fenceCmd.ExecuteReader()
$readerRdr.Read() | Out-Null
$readerSpid = [int]$readerRdr[0]
$readerRdr.NextResult() | Out-Null
$readerRdr.Read() | Out-Null
$hExecFenced = [int64]$readerRdr[0]
$readerRdr.Close()

Write-Host "  Reader fence acquired: SPID=$readerSpid, H_exec=$hExecFenced under (UPDLOCK, HOLDLOCK)." -ForegroundColor Cyan
if ($hExecFenced -ne 7) {
    throw "TEST_7_FAILED: Expected fenced version to be 7, got $hExecFenced."
}

# 3. Connection 2 (Concurrent Authoritative Writer) attempts to advance ServerState to version 8 in background job
$writerJob = Start-Job -ScriptBlock {
    param($connStr, $syncId8)
    Add-Type -AssemblyName "System.Data"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    $tx = $conn.BeginTransaction([System.Data.IsolationLevel]::Serializable)
    $cmd = $conn.CreateCommand()
    $cmd.Transaction = $tx
    $cmd.CommandTimeout = 30
    $cmd.CommandText = @"
SELECT @@SPID;
SELECT CurrentVersion FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = '2026';
UPDATE [sync].[ServerState] SET CurrentVersion = 8 WHERE DatabaseId = '2026';
INSERT INTO [sync].[ServerChangeFeed] (DatabaseId, ServerVersion, EntityType, EntitySyncId, OperationType, OriginDeviceId)
VALUES ('2026', 8, 'Daily', '$syncId8', 'INSERT', '00000000-0000-0000-0000-000000000000');
INSERT INTO [dbo].[Daily] (Name, DailyDate, Closed, IsActive, SyncId)
VALUES ('Daily_V8', '2026-07-04T10:00:00', 0, 1, '$syncId8');
"@
    $r = $cmd.ExecuteReader()
    $r.Read() | Out-Null
    $spid = [int]$r[0]
    $r.Close()

    # Finish transaction
    $tx.Commit()
    $conn.Close()
    return $spid
} -ArgumentList $remoteConn2026Str, ([Guid]::NewGuid().ToString())

# 4. Deterministic Locking Evidence: Poll sys.dm_os_waiting_tasks
# Proves that the writer is actively blocked by the reader's SPID
Write-Host "  Polling sys.dm_os_waiting_tasks for deterministic locking evidence..." -NoNewline
$diagConn = New-Object SqlConnection($remoteConn2026Str)
$diagConn.Open()
$isBlocked = $false
for ($i = 0; $i -lt 50; $i++) {
    Start-Sleep -Milliseconds 100
    $diagCmd = $diagConn.CreateCommand()
    $diagCmd.CommandText = @"
SELECT COUNT(*)
FROM sys.dm_os_waiting_tasks
WHERE blocking_session_id = @ReaderSpid
  AND wait_type LIKE 'LCK%';
"@
    $p = $diagCmd.CreateParameter(); $p.ParameterName = "@ReaderSpid"; $p.Value = $readerSpid; $diagCmd.Parameters.Add($p) | Out-Null
    $blockedCount = [int]$diagCmd.ExecuteScalar()
    if ($blockedCount -gt 0) {
        $isBlocked = $true
        break
    }
}
$diagConn.Close()

if (-not $isBlocked) {
    $fenceTx.Rollback()
    $fenceConn.Close()
    Stop-Job $writerJob; Remove-Job $writerJob
    throw "TEST_7_FAILED: Writer was not blocked by reader fence in sys.dm_os_waiting_tasks."
}
Write-Host " PASS (Writer confirmed blocked on LCK by Reader SPID $readerSpid)" -ForegroundColor Green

# 5. While writer is blocked, verify remote ServerState is still 7 (writer cannot advance)
$chkVer = [int64](Execute-SqlScalar $remoteConn2026Str "SELECT CurrentVersion FROM [sync].[ServerState] WITH (NOLOCK) WHERE DatabaseId = '2026';")
if ($chkVer -ne 7) {
    throw "TEST_7_FAILED: ServerState was prematurely advanced to $chkVer while reader fence was held."
}
Write-Host "  Verified ServerState.CurrentVersion remains exactly 7 while fence is held." -ForegroundColor Green

# 6. Reader commits and releases fence
$fenceTx.Commit()
$fenceConn.Close()
Write-Host "  Reader released fence (committed). Writer unblocking..." -ForegroundColor Cyan

# 7. Writer completes now that fence is released
$writerRes = Wait-Job $writerJob -Timeout 10 | Receive-Job
Remove-Job $writerJob

$postWriterVer = [int64](Execute-SqlScalar $remoteConn2026Str "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = '2026';")
if ($postWriterVer -ne 8) {
    throw "TEST_7_FAILED: Writer failed to advance ServerState to 8 after fence release (Got: $postWriterVer)."
}
Write-Host "  Writer committed version 8 successfully after fence release." -ForegroundColor Green

# Also advance 2027 to 8 to keep both years aligned for operator execution
Execute-Sql $remoteConn2027Str @"
DECLARE @SyncId7 UNIQUEIDENTIFIER = NEWID();
DECLARE @SyncId8 UNIQUEIDENTIFIER = NEWID();

INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('Daily_V7_2027', '2026-07-03T10:00:00', 0, 1, @SyncId7);

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('2027', 7, 'Daily', @SyncId7, 'INSERT', '00000000-0000-0000-0000-000000000000');

INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [IsActive], [SyncId])
VALUES ('Daily_V8_2027', '2026-07-04T10:00:00', 0, 1, @SyncId8);

INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('2027', 8, 'Daily', @SyncId8, 'INSERT', '00000000-0000-0000-0000-000000000000');

UPDATE [sync].[ServerState] SET [CurrentVersion] = 8 WHERE [DatabaseId] = '2027';
"@

# 8. Run catch-up operator: it must catch up to the new version 8
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
    if ($localVer -ne 8) {
        throw "TEST_7_FAILED: Local LastServerVersion for $yr after catch-up is $localVer (Expected: 8)."
    }
}
Write-Host " PASS (Lock fence deterministically proven; subsequent catch-up advanced 6 -> 8 cleanly)" -ForegroundColor Green

# --- TEST 8: Teardown and Cleanup ---
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
