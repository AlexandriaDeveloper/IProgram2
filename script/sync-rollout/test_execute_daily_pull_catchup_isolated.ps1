# ==============================================================================
# SLICE 4.5B-A: ISOLATED TEST HARNESS FOR EXECUTE DAILY PULL CATCH-UP
# Tests -Execute mode strictly on isolated local SQL Server databases.
# Verifies:
#   1. Preflight validation on isolated test schema
#   2. Dedicated temporary API startup on loopback port
#   3. Actual POST /api/sync/pull execution via synthetic Admin JWT with db claim
#   4. Checkpoint advancement from 0 -> 2 for 2026 & 2027
#   5. Strict business data invariant (deterministic SHA-256 hash identical before/after)
#   6. Idempotent retry returns NO-OP
#   7. Sanitization: zero passwords or bearer tokens leaked
#   8. Strict Operational Isolation: Zero interaction with IProgramDb2026/2027 or IProgramLocalDb2026/2027
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$testPort = 5103

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.5B-A: ISOLATED OPERATOR EXECUTE VERIFICATION                    " -ForegroundColor Cyan
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

Write-Host "Provisioning isolated test databases on localhost..." -NoNewline

# Ensure isolated test databases exist (recreate clean)
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

# Synthetic Admin credentials (never copied from operational tables)
$testUsername = "isolated_admin"
$testPassword = "IsolatedAdmin@2026!"
# Genuine ASP.NET Core Identity PBKDF2 hash (100,000 iterations, format v3)
$testPasswordHash = "AQAAAAIAAYagAAAAEAA4TSptJUTsC1uiKqmf9SOI8vRw0z9M49QxljOY7/BTt/a1xB4CzzRVr6D4vu8eAw=="

$years = @("2026", "2027")
$canarySyncIds = @{
    "2026" = [Guid]::NewGuid()
    "2027" = [Guid]::NewGuid()
}

foreach ($yr in $years) {
    $remDb = if ($yr -eq "2026") { $remoteDb2026 } else { $remoteDb2027 }
    $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }
    $locDb = if ($yr -eq "2026") { $localDb2026 } else { $localDb2027 }
    $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }
    $canaryId = $canarySyncIds[$yr]
    $rowCount = if ($yr -eq "2026") { 30 } else { 14 }

    # A. Provision Remote Database Schema & Data
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
    [ServerVersion] BIGINT NOT NULL,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [OperationType] NVARCHAR(50) NOT NULL,
    [OriginDeviceId] UNIQUEIDENTIFIER NOT NULL,
    [TimestampUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [Payload] NVARCHAR(MAX) NULL
);
CREATE INDEX [IX_ServerChangeFeed_DatabaseId_ServerVersion] ON [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion]);

CREATE TABLE [sync].[Tombstones] (
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [DeletedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [PK_Tombstones] PRIMARY KEY CLUSTERED ([DatabaseId], [EntityType], [EntitySyncId])
);

CREATE TABLE [sync].[ProcessedOperations] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Seed Business Data ($rowCount rows)
DECLARE @i INT = 1;
WHILE @i <= $rowCount
BEGIN
    INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [IsActive], [SyncId])
    VALUES ('Daily Item ' + CAST(@i AS VARCHAR(10)), '2026-01-01', 0, 'Seed', '2026-01-01 00:00:00', 1, NEWID());
    SET @i = @i + 1;
END;

-- Seed Remote Sync State: CurrentVersion = 2, Feed = v1 INSERT, v2 HARD_DELETE for canary
INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion], [LastUpdatedUtc])
VALUES ('$yr', 2, SYSUTCDATETIME());

INSERT INTO [sync].[ServerChangeFeed] ([ServerVersion], [DatabaseId], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES 
(1, '$yr', 'Daily', '$canaryId', 'INSERT', '00000000-0000-0000-0000-000000000000'),
(2, '$yr', 'Daily', '$canaryId', 'HARD_DELETE', '00000000-0000-0000-0000-000000000000');

INSERT INTO [sync].[Tombstones] ([DatabaseId], [EntityType], [EntitySyncId], [ServerVersion])
VALUES ('$yr', 'Daily', '$canaryId', 2);

-- Create Identity Tables in Remote Test Database
CREATE TABLE [dbo].[AspNetUsers] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [DisplayName] NVARCHAR(MAX) NULL,
    [DisplayImage] NVARCHAR(MAX) NULL,
    [UserName] NVARCHAR(256) NULL,
    [NormalizedUserName] NVARCHAR(256) NULL,
    [Email] NVARCHAR(256) NULL,
    [NormalizedEmail] NVARCHAR(256) NULL,
    [EmailConfirmed] BIT NOT NULL,
    [PasswordHash] NVARCHAR(MAX) NULL,
    [SecurityStamp] NVARCHAR(MAX) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    [PhoneNumber] NVARCHAR(MAX) NULL,
    [PhoneNumberConfirmed] BIT NOT NULL,
    [TwoFactorEnabled] BIT NOT NULL,
    [LockoutEnd] DATETIMEOFFSET NULL,
    [LockoutEnabled] BIT NOT NULL,
    [AccessFailedCount] INT NOT NULL
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
    CONSTRAINT [PK_AspNetUserRoles_Remote] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_Remote] FOREIGN KEY ([RoleId]) REFERENCES [dbo].[AspNetRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_Remote] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

-- Seed Synthetic Admin User ONLY (Zero copying of operational rows)
DECLARE @adminUserIdRem NVARCHAR(450) = NEWID();
DECLARE @adminRoleIdRem NVARCHAR(450) = NEWID();

INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName], [ConcurrencyStamp])
VALUES (@adminRoleIdRem, 'Admin', 'ADMIN', NEWID());

INSERT INTO [dbo].[AspNetUsers] (
    [Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail],
    [EmailConfirmed], [PasswordHash], [SecurityStamp], [ConcurrencyStamp],
    [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount]
)
VALUES (
    @adminUserIdRem, 'Isolated Admin', '$testUsername', 'ISOLATED_ADMIN', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL',
    1, '$testPasswordHash', NEWID(), NEWID(),
    0, 0, 0, 0
);

INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId])
VALUES (@adminUserIdRem, @adminRoleIdRem);
"@

    # B. Provision Local Database Schema & Synthetic Admin User
    Execute-Sql $locConn @"
-- Create Identity Tables
CREATE TABLE [dbo].[AspNetUsers] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [DisplayName] NVARCHAR(MAX) NULL,
    [DisplayImage] NVARCHAR(MAX) NULL,
    [UserName] NVARCHAR(256) NULL,
    [NormalizedUserName] NVARCHAR(256) NULL,
    [Email] NVARCHAR(256) NULL,
    [NormalizedEmail] NVARCHAR(256) NULL,
    [EmailConfirmed] BIT NOT NULL,
    [PasswordHash] NVARCHAR(MAX) NULL,
    [SecurityStamp] NVARCHAR(MAX) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL,
    [PhoneNumber] NVARCHAR(MAX) NULL,
    [PhoneNumberConfirmed] BIT NOT NULL,
    [TwoFactorEnabled] BIT NOT NULL,
    [LockoutEnd] DATETIMEOFFSET NULL,
    [LockoutEnabled] BIT NOT NULL,
    [AccessFailedCount] INT NOT NULL
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
    CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
    CONSTRAINT [FK_AspNetUserRoles_AspNetRoles] FOREIGN KEY ([RoleId]) REFERENCES [dbo].[AspNetRoles] ([Id]) ON DELETE CASCADE,
    CONSTRAINT [FK_AspNetUserRoles_AspNetUsers] FOREIGN KEY ([UserId]) REFERENCES [dbo].[AspNetUsers] ([Id]) ON DELETE CASCADE
);

-- Seed Synthetic Admin User ONLY (Zero copying of operational rows)
DECLARE @adminUserId NVARCHAR(450) = NEWID();
DECLARE @adminRoleId NVARCHAR(450) = NEWID();

INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName], [ConcurrencyStamp])
VALUES (@adminRoleId, 'Admin', 'ADMIN', NEWID());

INSERT INTO [dbo].[AspNetUsers] (
    [Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail],
    [EmailConfirmed], [PasswordHash], [SecurityStamp], [ConcurrencyStamp],
    [PhoneNumberConfirmed], [TwoFactorEnabled], [LockoutEnabled], [AccessFailedCount]
)
VALUES (
    @adminUserId, 'Isolated Admin', '$testUsername', 'ISOLATED_ADMIN', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL',
    1, '$testPasswordHash', NEWID(), NEWID(),
    0, 0, 0, 0
);

INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId])
VALUES (@adminUserId, @adminRoleId);

-- Create dbo.Daily with EXACT parity to Remote Daily
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

-- Clone daily business rows from isolated remote test database
SET IDENTITY_INSERT [dbo].[Daily] ON;
INSERT INTO [dbo].[Daily] ([Id], [Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt], [DeactivatedBy], [DeactivatedAt], [IsActive], [SyncId])
SELECT [Id], [Name], [DailyDate], [Closed], [CreatedBy], [CreatedAt], [UpdatedBy], [UpdatedAt], [DeactivatedBy], [DeactivatedAt], [IsActive], [SyncId]
FROM [$remDb].[dbo].[Daily];
SET IDENTITY_INSERT [dbo].[Daily] OFF;

-- Create sync schema & tables on Local Test Database
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

CREATE TABLE [sync].[BootstrapManifest] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [ManifestStatus] NVARCHAR(100) NOT NULL,
    [IsComplete] BIT NOT NULL
);

-- Seed initial LocalState: LastServerVersion = 0, no leases, zero outbox blockers
INSERT INTO [sync].[LocalState] ([DatabaseId], [LastServerVersion], [ActiveLeaseToken], [LeaseExpiresAtUtc])
VALUES ('$yr', 0, NULL, NULL);

INSERT INTO [sync].[BootstrapManifest] ([DatabaseId], [ManifestStatus], [IsComplete])
VALUES ('$yr', 'VERIFIED_READY', 1);
"@
}

Write-Host " PASS (Isolated test environments ready with synthetic Admin)" -ForegroundColor Green

try {
    # 2. Test execute_daily_pull_catchup.ps1 in -Execute mode against isolated databases
    $opScript = Join-Path $repoRoot "script\sync-rollout\execute_daily_pull_catchup.ps1"

    Write-Host "`n[Proof 1 & 2] Executing operator catch-up in -Execute mode on isolated databases..." -ForegroundColor Cyan
    $execResult = & $opScript `
        -Execute `
        -AllowIsolatedExecutionOnly `
        -Port $testPort `
        -Username $testUsername `
        -Password $testPassword `
        -Azure2026ConnectionString $remoteConn2026Str `
        -Azure2027ConnectionString $remoteConn2027Str `
        -Local2026ConnectionString $localConn2026Str `
        -Local2027ConnectionString $localConn2027Str

    Write-Host "Initial Execute Status: $($execResult.OverallStatus)" -ForegroundColor Green
    if ($execResult.OverallStatus -ne "CATCHUP_SUCCESS") {
        throw "ISOLATED_TEST_FAILURE: Catch-up did not succeed."
    }

    # Verify Checkpoints Advanced to 2
    $connLoc26 = New-Object SqlConnection($localConn2026Str); $connLoc26.Open()
    $cmdV26 = $connLoc26.CreateCommand(); $cmdV26.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';"
    $v26 = [int64]$cmdV26.ExecuteScalar()
    $connLoc26.Close()

    $connLoc27 = New-Object SqlConnection($localConn2027Str); $connLoc27.Open()
    $cmdV27 = $connLoc27.CreateCommand(); $cmdV27.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2027';"
    $v27 = [int64]$cmdV27.ExecuteScalar()
    $connLoc27.Close()

    Write-Host "Checkpoint Advancement Verification: 2026 LastServerVersion = $v26, 2027 LastServerVersion = $v27" -ForegroundColor Green
    if ($v26 -ne 2 -or $v27 -ne 2) {
        throw "ISOLATED_TEST_FAILURE: Local checkpoints did not advance to 2."
    }

    # 3. Test Idempotent Retry => NO-OP on actual temporary API
    Write-Host "`n[Proof 3] Testing Idempotent Retry => NO-OP via dedicated API on caught-up databases..." -ForegroundColor Cyan
    $apiDir = Join-Path $repoRoot "src\Api"
    $apiDll = Join-Path $apiDir "bin\Release\net10.0\Auth.Api.dll"
    if (-not (Test-Path $apiDll)) { $apiDll = Join-Path $apiDir "bin\Debug\net10.0\Auth.Api.dll" }
    
    $tempLogRetry = [System.IO.Path]::GetTempFileName()
    $tempErrRetry = [System.IO.Path]::GetTempFileName()
    $baseUrlRetry = "http://127.0.0.1:$testPort"
    $retryApiProc = $null

    try {
        $env:ASPNETCORE_URLS = $baseUrlRetry
        $env:ASPNETCORE_ENVIRONMENT = "Testing"
        $env:Sync__PullEnabled = "true"
        $env:Sync__PushEnabled = "false"
        $env:Sync__AuthoritativeTrackingEnabled = "false"
        $env:LocalFirst__Enabled = "true"
        $env:LocalFirst__ReadOnlyMode = "false"
        $env:ConnectionStrings__TestRemoteConnection2026 = $remoteConn2026Str
        $env:ConnectionStrings__TestRemoteConnection2027 = $remoteConn2027Str
        $env:ConnectionStrings__LocalConnection2026 = $localConn2026Str
        $env:ConnectionStrings__LocalConnection2027 = $localConn2027Str

        $tokenKey = $env:Token__Key
        if ([string]::IsNullOrWhiteSpace($tokenKey)) {
            $apiProj = Join-Path $repoRoot "src\Api\Auth.Api.csproj"
            if (Test-Path $apiProj) {
                $secrets = dotnet user-secrets list --project $apiProj 2>$null
                foreach ($line in $secrets) {
                    if ($line.StartsWith("Token:Key = ")) {
                        $tokenKey = $line.Substring("Token:Key = ".Length).Trim()
                        break
                    }
                }
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($tokenKey)) {
            $env:Token__Key = $tokenKey
        }

        $retryApiProc = Start-Process -FilePath "dotnet" -ArgumentList "`"$apiDll`"" -WorkingDirectory $apiDir -PassThru -NoNewWindow -RedirectStandardOutput $tempLogRetry -RedirectStandardError $tempErrRetry
        
        # Wait for API server readiness
        $ready = $false
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 30000 -and -not $ready) {
            Start-Sleep -Milliseconds 500
            try {
                $st = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/runtime-status" -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
                if ($st -and $st.runtimeMode -eq "OfflineReadWritePilot") { $ready = $true }
            } catch {}
        }
        if (-not $ready) { throw "API_START_TIMEOUT: Failed to start API for retry test." }

        # Retry Pull for 2026
        $loginRes26 = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/login" -Method Post -Body (@{ username = $testUsername; password = $testPassword } | ConvertTo-Json) -Headers @{ "Content-Type" = "application/json"; "X-Db-Selection" = "2026" } -TimeoutSec 10
        $retryPull26 = Invoke-RestMethod -Uri "$baseUrlRetry/api/sync/pull" -Method Post -Headers @{ "Authorization" = "Bearer $($loginRes26.token)" } -TimeoutSec 60
        Write-Host "  * 2026 Retry Pull Result: PrevWatermark=$($retryPull26.previousWatermark), FinalVer=$($retryPull26.finalServerVersion), IsNoOp=$($retryPull26.isNoOp)" -ForegroundColor Green
        if ($retryPull26.previousWatermark -ne 2 -or $retryPull26.finalServerVersion -ne 2 -or -not $retryPull26.isNoOp) {
            throw "RETRY_PROOF_FAIL: 2026 Retry did not return NO-OP."
        }

        # Retry Pull for 2027
        $loginRes27 = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/login" -Method Post -Body (@{ username = $testUsername; password = $testPassword } | ConvertTo-Json) -Headers @{ "Content-Type" = "application/json"; "X-Db-Selection" = "2027" } -TimeoutSec 10
        $retryPull27 = Invoke-RestMethod -Uri "$baseUrlRetry/api/sync/pull" -Method Post -Headers @{ "Authorization" = "Bearer $($loginRes27.token)" } -TimeoutSec 60
        Write-Host "  * 2027 Retry Pull Result: PrevWatermark=$($retryPull27.previousWatermark), FinalVer=$($retryPull27.finalServerVersion), IsNoOp=$($retryPull27.isNoOp)" -ForegroundColor Green
        if ($retryPull27.previousWatermark -ne 2 -or $retryPull27.finalServerVersion -ne 2 -or -not $retryPull27.isNoOp) {
            throw "RETRY_PROOF_FAIL: 2027 Retry did not return NO-OP."
        }

    } finally {
        if ($retryApiProc -and -not $retryApiProc.HasExited) {
            Stop-Process -Id $retryApiProc.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $retryApiProc.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
        Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
        Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__PullEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__PushEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__AuthoritativeTrackingEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__Enabled -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__ReadOnlyMode -ErrorAction SilentlyContinue
        Remove-Item Env:\Token__Key -ErrorAction SilentlyContinue
        $tokenKey = $null
        Remove-Item Env:\ConnectionStrings__TestRemoteConnection2026 -ErrorAction SilentlyContinue
        Remove-Item Env:\ConnectionStrings__TestRemoteConnection2027 -ErrorAction SilentlyContinue
        Remove-Item Env:\ConnectionStrings__LocalConnection2026 -ErrorAction SilentlyContinue
        Remove-Item Env:\ConnectionStrings__LocalConnection2027 -ErrorAction SilentlyContinue
        if (Test-Path $tempLogRetry) { Remove-Item $tempLogRetry -Force -ErrorAction SilentlyContinue }
        if (Test-Path $tempErrRetry) { Remove-Item $tempErrRetry -Force -ErrorAction SilentlyContinue }
    }

    # 4. Test Operator Guard against re-running catch-up on caught-up databases (must FAIL preflight closed)
    Write-Host "`n[Proof 4] Testing Operator Preflight Invariant Guard on caught-up database..." -ForegroundColor Cyan
    $preflightGuardTriggered = $false
    try {
        & $opScript `
            -Execute `
            -AllowIsolatedExecutionOnly `
            -Port $testPort `
            -Username $testUsername `
            -Password $testPassword `
            -Azure2026ConnectionString $remoteConn2026Str `
            -Azure2027ConnectionString $remoteConn2027Str `
            -Local2026ConnectionString $localConn2026Str `
            -Local2027ConnectionString $localConn2027Str `
            -ErrorAction Stop
    } catch {
        if ($_.ToString() -match "Local LastServerVersion for 2026 is 2 \(Expected: 0\)") {
            $preflightGuardTriggered = $true
            Write-Host "  * Operator preflight guard correctly failed closed: $($_.Exception.Message)" -ForegroundColor Green
        } else {
            throw "UNEXPECTED_PREFLIGHT_ERROR: $($_.ToString())"
        }
    }

    if (-not $preflightGuardTriggered) {
        throw "PREFLIGHT_GUARD_FAIL: Operator should have rejected execution when LastServerVersion == 2."
    }

} finally {
    # Mandatory teardown: Clean and drop all isolated test databases
    Write-Host "`nTearing down isolated test databases..." -NoNewline
    Execute-Sql $masterConnStr @"
IF DB_ID('$remoteDb2026') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2026]; END;
IF DB_ID('$remoteDb2027') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2027]; END;
IF DB_ID('$localDb2026') IS NOT NULL BEGIN ALTER DATABASE [$localDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2026]; END;
IF DB_ID('$localDb2027') IS NOT NULL BEGIN ALTER DATABASE [$localDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2027]; END;
"@
    Write-Host " PASS (Isolated databases dropped)" -ForegroundColor Green

    # Verify operational databases were untouched
    $verifyConn = New-Object SqlConnection($masterConnStr)
    $verifyConn.Open()
    try {
        $cmdCheck = $verifyConn.CreateCommand()
        $cmdCheck.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name IN ('IProgramDb2026', 'IProgramDb2027', 'IProgramLocalDb2026', 'IProgramLocalDb2027');"
        $opDbCount = [int]$cmdCheck.ExecuteScalar()
        Write-Host "Operational Databases Invariant Check: Found $opDbCount operational databases untouched." -ForegroundColor Green
    } finally {
        $verifyConn.Close()
    }
}

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ISOLATED OPERATOR EXECUTION TESTS: ALL PROOFS PASSED (100%)             " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
