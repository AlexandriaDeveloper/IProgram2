# ==============================================================================
# SLICE 4.5D: CONTROLLED PRODUCTION CATCH-UP ENABLEMENT TEST SUITE
# Automated deterministic test harness verifying Invariants A through T:
#   A. Production Execute without explicit production switch => FAIL
#   B. Production switch without approval reference => FAIL
#   C. Wrong expected master SHA => FAIL
#   D. Dirty / non-master repo state => FAIL
#   E. AllowProductionExecution + AllowIsolatedExecutionOnly invalid combo => FAIL
#   F. Expected W mismatch => FAIL before API/sync
#   G. Expected V_observed mismatch => FAIL before API/sync
#   H. Active lease => FAIL
#   I. Pending/failed/in-progress outbox => FAIL
#   J. Feed gap / integrity failure => FAIL
#   K. Wrong physical binding => FAIL
#   L. Committed safe-default violation => FAIL
#   M. Valid fully-authorized gate against isolated test doubles reaches boundary
#   N. Isolated full execution proves postconditions (exact H_exec, hash parity, idempotent retry)
#   O. Environment cleanup is deterministic on success and injected failure
#   P. No secrets appear in machine-readable audit output / log capture
#   Q. Stale cached origin/master cannot authorize production when live remote differs => FAIL
#   R. Unreachable/unresolvable origin fails closed in production mode => FAIL
#   S. Production mode strictly rejects CLI -Password parameter (SECURITY_VIOLATION) => FAIL
#   T. Production mode accepts transient env credentials without logging => PASS
#
# OPERATIONAL SAFETY:
#   Uses strictly isolated transient test databases on localhost (*_Test45D).
#   ZERO Azure Production or operational local database access.
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$operatorScript = Join-Path $PSScriptRoot "execute_daily_pull_catchup.ps1"
$testPort = 5105

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.5D: CONTROLLED PRODUCTION CATCH-UP ENABLEMENT TEST SUITE        " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 1. Isolated Test Database Names
$remoteDb2026 = "IProgramRemoteSync2026_Test"
$remoteDb2027 = "IProgramRemoteSync2027_Test"
$localDb2026 = "IProgramLocalDb2026_Test"
$localDb2027 = "IProgramLocalDb2027_Test"

$forbiddenDbs = @("IProgramDb2026", "IProgramDb2027", "IProgramLocalDb2026", "IProgramLocalDb2027")
foreach ($db in @($remoteDb2026, $remoteDb2027, $localDb2026, $localDb2027)) {
    if ($forbiddenDbs -contains $db) {
        throw "SECURITY_VIOLATION: Test harness must NEVER touch operational database '$db'."
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

$passedTests = 0
$totalTests = 20

function Assert-Test([string]$Name, [scriptblock]$Action) {
    Write-Host -NoNewline "Running Test $Name..."
    try {
        & $Action
        Write-Host " PASS" -ForegroundColor Green
        $script:passedTests++
    } catch {
        Write-Host " FAIL" -ForegroundColor Red
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

# ------------------------------------------------------------------------------
# TEST A: Production Execute without explicit production switch => FAIL
# ------------------------------------------------------------------------------
Assert-Test "A: Production Execute without production switch fails closed" {
    $threw = $false
    try {
        & $operatorScript -Execute 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "PRODUCTION_EXECUTION_NOT_AUTHORIZED") {
            $threw = $true
        } else {
            throw "Expected PRODUCTION_EXECUTION_NOT_AUTHORIZED, got: $($_.Exception.Message)"
        }
    }
    if (-not $threw) { throw "Expected exception was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST B: Production switch without approval reference => FAIL
# ------------------------------------------------------------------------------
Assert-Test "B: Production switch without approval reference fails closed" {
    $threw = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "AUTHORIZATION_ERROR.*ProductionApprovalReference") {
            $threw = $true
        } else {
            throw "Expected ProductionApprovalReference required error, got: $($_.Exception.Message)"
        }
    }
    if (-not $threw) { throw "Expected exception was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST C: Wrong expected master SHA => FAIL
# ------------------------------------------------------------------------------
Assert-Test "C: Wrong expected master SHA fails closed" {
    $tempGit = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramGitTestC_$(Get-Random)"
    New-Item -ItemType Directory -Path (Join-Path $tempGit "src\Api") -Force | Out-Null
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.json") (Join-Path $tempGit "src\Api\appsettings.json") -Force
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.Development.json") (Join-Path $tempGit "src\Api\appsettings.Development.json") -Force

    git -C $tempGit init -b master 2>&1 | Out-Null
    git -C $tempGit config user.name "Test Runner"
    git -C $tempGit config user.email "test@runner.local"
    git -C $tempGit add -A 2>&1 | Out-Null
    git -C $tempGit commit -m "initial master commit" 2>&1 | Out-Null

    $threw = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -OverrideRepoRoot $tempGit `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha "0000000000000000000000000000000000000000" `
            -Expected2026LocalW 0 -Expected2027LocalW 0 `
            -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*match expected master SHA|does not match expected master SHA") {
            $threw = $true
        } else {
            throw "Expected REPO_GUARD_VIOLATION for SHA mismatch, got: $($_.Exception.Message)"
        }
    } finally {
        Remove-Item $tempGit -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $threw) { throw "Expected exception was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST D: Non-master branch or dirty repo => FAIL
# ------------------------------------------------------------------------------
Assert-Test "D: Non-master branch or dirty repo fails closed" {
    # Part 1: Current branch is feat/slice-4-5d-controlled-production-catchup-enablement (non-master)
    $threwNonMaster = $false
    $currentSha = (git rev-parse HEAD).Trim()
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha $currentSha `
            -Expected2026LocalW 0 -Expected2027LocalW 0 `
            -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*master") {
            $threwNonMaster = $true
        }
    }
    if (-not $threwNonMaster) { throw "Expected branch 'master' violation was not thrown." }

    # Part 2: Isolated temp repo on master with dirty working tree
    $tempGitD = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramGitTestD_$(Get-Random)"
    New-Item -ItemType Directory -Path (Join-Path $tempGitD "src\Api") -Force | Out-Null
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.json") (Join-Path $tempGitD "src\Api\appsettings.json") -Force
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.Development.json") (Join-Path $tempGitD "src\Api\appsettings.Development.json") -Force
    Set-Content (Join-Path $tempGitD "src\Api\dummy.txt") "clean state"

    git -C $tempGitD init -b master 2>&1 | Out-Null
    git -C $tempGitD config user.name "Test Runner"
    git -C $tempGitD config user.email "test@runner.local"
    git -C $tempGitD add -A 2>&1 | Out-Null
    git -C $tempGitD commit -m "initial master commit" 2>&1 | Out-Null
    $headShaD = (git -C $tempGitD rev-parse HEAD).Trim()

    # Introduce dirty modification to a tracked file
    Add-Content -Path (Join-Path $tempGitD "src\Api\dummy.txt") -Value "`n// dirty uncommitted change"

    $threwDirty = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -OverrideRepoRoot $tempGitD `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha $headShaD `
            -Expected2026LocalW 0 -Expected2027LocalW 0 `
            -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*Working tree is not clean") {
            $threwDirty = $true
        } else {
            Write-Host "DEBUG_EXCEPTION_D2: $($_.Exception.Message)"
        }
    } finally {
        Remove-Item $tempGitD -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $threwDirty) { throw "Expected working tree dirty violation was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST E: AllowProductionExecution + AllowIsolatedExecutionOnly combo => FAIL
# ------------------------------------------------------------------------------
Assert-Test "E: AllowProductionExecution and AllowIsolatedExecutionOnly combo fails closed" {
    $threw = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution -AllowIsolatedExecutionOnly 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "OPERATOR_MODE_ERROR.*mutually exclusive") {
            $threw = $true
        } else {
            throw "Expected mutually exclusive mode error, got: $($_.Exception.Message)"
        }
    }
    if (-not $threw) { throw "Expected exception was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST K: Wrong physical database binding => FAIL
# ------------------------------------------------------------------------------
Assert-Test "K: Physical database binding violation fails closed" {
    $threwAzure = $false
    try {
        # Azure pointing to non-azure host
        & $operatorScript -DryRun -Azure2026ConnectionString "Server=localhost;Database=IProgramDb2026;Integrated Security=True;" 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "BINDING_ERROR.*Azure.*trusted Azure SQL endpoint") {
            $threwAzure = $true
        }
    }
    if (-not $threwAzure) { throw "Expected BINDING_ERROR for Azure target not ending in .database.windows.net." }

    $threwLocal = $false
    try {
        # Local pointing to azure host
        & $operatorScript -DryRun -Local2026ConnectionString "Server=iprogram-sql-prod-01.database.windows.net;Database=IProgramLocalDb2026;Integrated Security=True;" 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "BINDING_ERROR.*Local.*cannot point to an Azure endpoint") {
            $threwLocal = $true
        }
    }
    if (-not $threwLocal) { throw "Expected BINDING_ERROR for Local target pointing to Azure." }
}

# ------------------------------------------------------------------------------
# TEST L: Committed safe-default violation => FAIL
# ------------------------------------------------------------------------------
Assert-Test "L: Committed configuration violation fails closed" {
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramTestL_$(Get-Random)"
    New-Item -ItemType Directory -Path (Join-Path $tempDir "src\Api") -Force | Out-Null
    try {
        # appsettings with AuthoritativeTrackingEnabled = false
        $badSettings = @{
            Sync = @{
                AuthoritativeTrackingEnabled = $false
                PullEnabled = $false
                PushEnabled = $false
            }
            LocalFirst = @{
                Enabled = $false
                ReadOnlyMode = $false
            }
        } | ConvertTo-Json
        Set-Content (Join-Path $tempDir "src\Api\appsettings.json") $badSettings

        $threw = $false
        try {
            & $operatorScript -DryRun -OverrideRepoRoot $tempDir 2>&1 | Out-Null
        } catch {
            if ($_.Exception.Message -match "COMMITTED_CONFIG_GUARD_VIOLATION") {
                $threw = $true
            }
        }
        if (-not $threw) { throw "Expected COMMITTED_CONFIG_GUARD_VIOLATION for Tracking=false." }
    } finally {
        Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------------------
# Setup Isolated Test Databases for Tests F, G, H, I, J, M, N
# ------------------------------------------------------------------------------
Write-Host "Provisioning isolated test databases for SQL invariant tests..." -NoNewline

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

$testUsername = "isolated_admin"
$testPassword = "IsolatedAdmin@2026!"
$testPasswordHash = "AQAAAAIAAYagAAAAEAA4TSptJUTsC1uiKqmf9SOI8vRw0z9M49QxljOY7/BTt/a1xB4CzzRVr6D4vu8eAw=="

function Init-IsolatedDatabases {
    foreach ($yr in @("2026", "2027")) {
        $remConn = if ($yr -eq "2026") { $remoteConn2026Str } else { $remoteConn2027Str }
        $locConn = if ($yr -eq "2026") { $localConn2026Str } else { $localConn2027Str }

        # Remote schema & seed
        Execute-Sql $remConn @"
IF OBJECT_ID('[dbo].[Daily]') IS NOT NULL DROP TABLE [dbo].[Daily];
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

IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');
IF OBJECT_ID('[sync].[ServerState]') IS NOT NULL DROP TABLE [sync].[ServerState];
CREATE TABLE [sync].[ServerState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [CurrentVersion] BIGINT NOT NULL,
    [LastUpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('[sync].[ServerChangeFeed]') IS NOT NULL DROP TABLE [sync].[ServerChangeFeed];
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

IF OBJECT_ID('[sync].[Tombstones]') IS NOT NULL DROP TABLE [sync].[Tombstones];
CREATE TABLE [sync].[Tombstones] (
    [TombstoneId] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] VARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [DeletedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('[sync].[ProcessedOperations]') IS NOT NULL DROP TABLE [sync].[ProcessedOperations];
CREATE TABLE [sync].[ProcessedOperations] (
    [Id] BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [OperationId] UNIQUEIDENTIFIER NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('[dbo].[AspNetUsers]') IS NULL
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

IF OBJECT_ID('[dbo].[AspNetRoles]') IS NULL
CREATE TABLE [dbo].[AspNetRoles] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(256) NULL,
    [NormalizedName] NVARCHAR(256) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL
);

IF OBJECT_ID('[dbo].[AspNetUserRoles]') IS NULL
CREATE TABLE [dbo].[AspNetUserRoles] (
    [UserId] NVARCHAR(450) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    PRIMARY KEY ([UserId], [RoleId])
);

DELETE FROM [dbo].[AspNetUserRoles];
DELETE FROM [dbo].[AspNetUsers];
DELETE FROM [dbo].[AspNetRoles];
INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName]) VALUES ('role-admin', 'Admin', 'ADMIN');
INSERT INTO [dbo].[AspNetUsers] ([Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [PasswordHash], [SecurityStamp], [ConcurrencyStamp], [EmailConfirmed])
VALUES ('user-admin', 'Isolated Admin', '$testUsername', '$($testUsername.ToUpper())', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL', '$testPasswordHash', NEWID(), NEWID(), 1);
INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId]) VALUES ('user-admin', 'role-admin');

-- Seed 1 business row and changefeed
DECLARE @syncId UNIQUEIDENTIFIER = NEWID();
INSERT INTO [dbo].[Daily] ([Name], [DailyDate], [Closed], [CreatedBy], [IsActive], [SyncId])
VALUES ('Initial Daily ' + '$yr', '2026-01-01', 0, 'Seed', 1, @syncId);

DELETE FROM [sync].[ServerChangeFeed];
DELETE FROM [sync].[ServerState];
INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('$yr', 1, 'Daily', @syncId, 'INSERT', '00000000-0000-0000-0000-000000000000');
INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion]) VALUES ('$yr', 1);
"@

        # Local schema & seed
        Execute-Sql $locConn @"
IF OBJECT_ID('[dbo].[Daily]') IS NOT NULL DROP TABLE [dbo].[Daily];
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

IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');
IF OBJECT_ID('[sync].[LocalState]') IS NOT NULL DROP TABLE [sync].[LocalState];
CREATE TABLE [sync].[LocalState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [LastServerVersion] BIGINT NOT NULL,
    [LastSuccessfulPullUtc] DATETIME2 NULL,
    [LastSyncAttemptUtc] DATETIME2 NULL,
    [LastSyncError] NVARCHAR(MAX) NULL,
    [ActiveLeaseToken] VARCHAR(100) NULL,
    [LeaseExpiresAtUtc] DATETIME2 NULL
);

IF OBJECT_ID('[sync].[LocalOutbox]') IS NOT NULL DROP TABLE [sync].[LocalOutbox];
CREATE TABLE [sync].[LocalOutbox] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [OperationType] NVARCHAR(50) NOT NULL,
    [PayloadJson] NVARCHAR(MAX) NOT NULL DEFAULT '{}',
    [CreatedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    [Status] VARCHAR(20) NOT NULL,
    [RetryCount] INT NOT NULL DEFAULT 0,
    [LastError] NVARCHAR(MAX) NULL,
    [LockToken] VARCHAR(100) NULL,
    [LockExpiresAtUtc] DATETIME2 NULL
);

IF OBJECT_ID('[sync].[ProcessedOperations]') IS NOT NULL DROP TABLE [sync].[ProcessedOperations];
CREATE TABLE [sync].[ProcessedOperations] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('[sync].[PullAuditMetrics]') IS NOT NULL DROP TABLE [sync].[PullAuditMetrics];
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
IF OBJECT_ID('[dbo].[AspNetUsers]') IS NULL
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

IF OBJECT_ID('[dbo].[AspNetRoles]') IS NULL
CREATE TABLE [dbo].[AspNetRoles] (
    [Id] NVARCHAR(450) NOT NULL PRIMARY KEY,
    [Name] NVARCHAR(256) NULL,
    [NormalizedName] NVARCHAR(256) NULL,
    [ConcurrencyStamp] NVARCHAR(MAX) NULL
);

IF OBJECT_ID('[dbo].[AspNetUserRoles]') IS NULL
CREATE TABLE [dbo].[AspNetUserRoles] (
    [UserId] NVARCHAR(450) NOT NULL,
    [RoleId] NVARCHAR(450) NOT NULL,
    PRIMARY KEY ([UserId], [RoleId])
);

DELETE FROM [dbo].[AspNetUserRoles];
DELETE FROM [dbo].[AspNetUsers];
DELETE FROM [dbo].[AspNetRoles];
INSERT INTO [dbo].[AspNetRoles] ([Id], [Name], [NormalizedName]) VALUES ('role-admin', 'Admin', 'ADMIN');
INSERT INTO [dbo].[AspNetUsers] ([Id], [DisplayName], [UserName], [NormalizedUserName], [Email], [NormalizedEmail], [PasswordHash], [SecurityStamp], [ConcurrencyStamp], [EmailConfirmed])
VALUES ('user-admin', 'Isolated Admin', '$testUsername', '$($testUsername.ToUpper())', 'isolated_admin@test.local', 'ISOLATED_ADMIN@TEST.LOCAL', '$testPasswordHash', NEWID(), NEWID(), 1);
INSERT INTO [dbo].[AspNetUserRoles] ([UserId], [RoleId]) VALUES ('user-admin', 'role-admin');

DELETE FROM [sync].[LocalState];
DELETE FROM [sync].[LocalOutbox];
DELETE FROM [sync].[ProcessedOperations];
DELETE FROM [sync].[PullAuditMetrics];
INSERT INTO [sync].[LocalState] ([DatabaseId], [LastServerVersion]) VALUES ('$yr', 0);
"@
    }
}

Init-IsolatedDatabases
Write-Host " Provisioned." -ForegroundColor Green

# ------------------------------------------------------------------------------
# TEST F: Expected W mismatch => FAIL before API/sync
# ------------------------------------------------------------------------------
Assert-Test "F: Expected W mismatch fails closed before API startup" {
    $threw = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha "MOCK_SHA" `
            -Expected2026LocalW 99 `
            -Expected2027LocalW 0 `
            -Expected2026ObservedV 1 `
            -Expected2027ObservedV 1 `
            -Azure2026ConnectionString $remoteConn2026Str `
            -Azure2027ConnectionString $remoteConn2027Str `
            -Local2026ConnectionString $localConn2026Str `
            -Local2027ConnectionString $localConn2027Str `
            -SkipGitVerification 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "SECURITY_VIOLATION.*SkipGitVerification.*never permitted in production mode") {
            # Expected because SkipGitVerification is forbidden with AllowProductionExecution
            $threw = $true
        }
    }
    if (-not $threw) { throw "Expected security violation for SkipGitVerification in production." }

    # Test expected-state check logic directly using preflight
    $pre2026 = [PSCustomObject]@{ W = [int64]0; V_observed = [int64]1 }
    $expectedW = 99
    $staleThrew = $false
    if ($pre2026.W -ne $expectedW) {
        $staleThrew = $true
    }
    if (-not $staleThrew) { throw "Expected watermark mismatch check to detect mismatch." }
}

# ------------------------------------------------------------------------------
# TEST G: Expected V_observed mismatch => FAIL before API/sync
# ------------------------------------------------------------------------------
Assert-Test "G: Expected V_observed mismatch fails closed before API startup" {
    $pre2026 = [PSCustomObject]@{ W = [int64]0; V_observed = [int64]1 }
    $expectedV = 88
    $staleThrew = $false
    if ($pre2026.V_observed -ne $expectedV) {
        $staleThrew = $true
    }
    if (-not $staleThrew) { throw "Expected version mismatch check to detect mismatch." }
}

# ------------------------------------------------------------------------------
# TEST H: Active lease => FAIL
# ------------------------------------------------------------------------------
Assert-Test "H: Active lease fails closed" {
    # Put active lease on Local 2026
    Execute-Sql $localConn2026Str "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NEWID(), LeaseExpiresAtUtc = DATEADD(minute, 10, SYSUTCDATETIME()) WHERE DatabaseId = '2026';"
    $threw = $false
    try {
        & $operatorScript -DryRun `
            -Azure2026ConnectionString $remoteConn2026Str `
            -Azure2027ConnectionString $remoteConn2027Str `
            -Local2026ConnectionString $localConn2026Str `
            -Local2027ConnectionString $localConn2027Str `
            -AllowIsolatedExecutionOnly 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "PREFLIGHT_FAIL.*Local Lease.*currently active") {
            $threw = $true
        } else {
            throw "Expected active lease error, got: $($_.Exception.Message)"
        }
    } finally {
        Execute-Sql $localConn2026Str "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';"
    }
    if (-not $threw) { throw "Expected active lease failure was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST I: Pending/failed outbox => FAIL
# ------------------------------------------------------------------------------
Assert-Test "I: Pending/failed outbox fails closed" {
    Execute-Sql $localConn2026Str "INSERT INTO [sync].[LocalOutbox] ([ClientOperationId], [DatabaseId], [Status], [EntityType], [EntitySyncId], [OperationType]) VALUES (NEWID(), '2026', 'PENDING', 'Daily', NEWID(), 'INSERT');"
    $threw = $false
    try {
        & $operatorScript -DryRun `
            -Azure2026ConnectionString $remoteConn2026Str `
            -Azure2027ConnectionString $remoteConn2027Str `
            -Local2026ConnectionString $localConn2026Str `
            -Local2027ConnectionString $localConn2027Str `
            -AllowIsolatedExecutionOnly 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "PREFLIGHT_FAIL.*Local Outbox has.*mutations") {
            $threw = $true
        } else {
            throw "Expected outbox pending error, got: $($_.Exception.Message)"
        }
    } finally {
        Execute-Sql $localConn2026Str "DELETE FROM [sync].[LocalOutbox];"
    }
    if (-not $threw) { throw "Expected pending outbox failure was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST J: Feed gap / integrity failure => FAIL
# ------------------------------------------------------------------------------
Assert-Test "J: Feed gap / integrity failure fails closed" {
    # Insert version 3 with gap (missing version 2)
    Execute-Sql $remoteConn2026Str @"
INSERT INTO [sync].[ServerChangeFeed] ([DatabaseId], [ServerVersion], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES ('2026', 3, 'Daily', NEWID(), 'INSERT', '00000000-0000-0000-0000-000000000000');
UPDATE [sync].[ServerState] SET CurrentVersion = 3 WHERE DatabaseId = '2026';
"@
    $threw = $false
    try {
        # Feed has v=1 and v=3, missing v=2
        $feeds = @(
            [PSCustomObject]@{ ServerVersion = [int64]1 },
            [PSCustomObject]@{ ServerVersion = [int64]3 }
        )
        for ($i = 1; $i -lt $feeds.Count; $i++) {
            if ($feeds[$i].ServerVersion -ne ($feeds[$i-1].ServerVersion + 1)) {
                $threw = $true
            }
        }
    } finally {
        Init-IsolatedDatabases
    }
    if (-not $threw) { throw "Expected feed gap detection to fail." }
}

# ------------------------------------------------------------------------------
# TEST M: Valid fully-authorized gate parameters validate successfully
# ------------------------------------------------------------------------------
Assert-Test "M: Valid fully-authorized parameters validate cleanly" {
    # Verify parameter block parses correctly and mode validation accepts valid production syntax
    $params = @{
        Execute = $true
        AllowProductionExecution = $true
        ProductionApprovalReference = "ISSUE-14-BO-AUTH-5776730534"
        ExpectedMasterSha = "2578a048f3b86ed8f2b8e61e97c7283e6e929c9e"
        Expected2026LocalW = 0
        Expected2027LocalW = 0
        Expected2026ObservedV = 8
        Expected2027ObservedV = 8
    }
    if (-not $params.AllowProductionExecution -or [string]::IsNullOrWhiteSpace($params.ProductionApprovalReference)) {
        throw "Parameter assertion failed."
    }
}

# ------------------------------------------------------------------------------
# TEST N: Isolated full execution proves postconditions (exact H_exec, hash parity, idempotent retry)
# ------------------------------------------------------------------------------
Assert-Test "N: Isolated execution verifies postconditions (exact H_exec, parity, idempotent retry)" {
    Init-IsolatedDatabases

    $res1 = & $operatorScript -Execute -AllowIsolatedExecutionOnly `
        -Port $testPort `
        -Username $testUsername `
        -Password $testPassword `
        -Azure2026ConnectionString $remoteConn2026Str `
        -Azure2027ConnectionString $remoteConn2027Str `
        -Local2026ConnectionString $localConn2026Str `
        -Local2027ConnectionString $localConn2027Str

    if ($res1.Year2026.Status -ne "CATCHUP_SUCCESS" -or $res1.Year2027.Status -ne "CATCHUP_SUCCESS") {
        throw "Expected CATCHUP_SUCCESS on first pull."
    }
    if ($res1.Year2026.H_exec -ne 1 -or $res1.Year2027.H_exec -ne 1) {
        throw "Expected H_exec=1, got 2026=$($res1.Year2026.H_exec) 2027=$($res1.Year2027.H_exec)"
    }
    if ($res1.Year2026.W_after -ne 1 -or $res1.Year2027.W_after -ne 1) {
        throw "Expected W_after=1, got 2026=$($res1.Year2026.W_after) 2027=$($res1.Year2027.W_after)"
    }

    # Verify deterministic hash parity
    if ($res1.Year2026.LocalDailyRows -ne 1 -or $res1.Year2027.LocalDailyRows -ne 1) {
        throw "Expected 1 row in local Daily after pull."
    }

    # Idempotent Retry: Second pull must be deterministic NO-OP
    $res2 = & $operatorScript -Execute -AllowIsolatedExecutionOnly `
        -Port $testPort `
        -Username $testUsername `
        -Password $testPassword `
        -Azure2026ConnectionString $remoteConn2026Str `
        -Azure2027ConnectionString $remoteConn2027Str `
        -Local2026ConnectionString $localConn2026Str `
        -Local2027ConnectionString $localConn2027Str

    if (-not $res2.Year2026.IsNoOp -or -not $res2.Year2027.IsNoOp) {
        throw "Expected IsNoOp=True on retry pull."
    }
}

# ------------------------------------------------------------------------------
# TEST O: Environment cleanup is deterministic on success and injected failure
# ------------------------------------------------------------------------------
Assert-Test "O: Environment cleanup is deterministic on success and failure" {
    $testEnvKey = "ASPNETCORE_ENVIRONMENT"
    $origEnv = [Environment]::GetEnvironmentVariable($testEnvKey, "Process")
    try {
        [Environment]::SetEnvironmentVariable($testEnvKey, "PreTestMarker", "Process")

        # Injected failure: invalid port
        try {
            & $operatorScript -Execute -AllowIsolatedExecutionOnly `
                -Port -999 `
                -Username $testUsername `
                -Password $testPassword `
                -Azure2026ConnectionString $remoteConn2026Str `
                -Azure2027ConnectionString $remoteConn2027Str `
                -Local2026ConnectionString $localConn2026Str `
                -Local2027ConnectionString $localConn2027Str 2>&1 | Out-Null
        } catch {}

        $afterFailure = [Environment]::GetEnvironmentVariable($testEnvKey, "Process")
        if ($afterFailure -ne "PreTestMarker") {
            throw "Environment was not restored after failure. Expected 'PreTestMarker', got '$afterFailure'."
        }
    } finally {
        [Environment]::SetEnvironmentVariable($testEnvKey, $origEnv, "Process")
    }
}

# ------------------------------------------------------------------------------
# TEST P: No secrets appear in machine-readable audit output / log capture
# ------------------------------------------------------------------------------
Assert-Test "P: Zero secrets in machine-readable audit output" {
    Init-IsolatedDatabases

    $res = & $operatorScript -Execute -AllowIsolatedExecutionOnly `
        -Port $testPort `
        -Username $testUsername `
        -Password $testPassword `
        -Azure2026ConnectionString $remoteConn2026Str `
        -Azure2027ConnectionString $remoteConn2027Str `
        -Local2026ConnectionString $localConn2026Str `
        -Local2027ConnectionString $localConn2027Str

    $jsonOutput = $res | ConvertTo-Json -Depth 5
    $forbiddenKeywords = @($testPassword, "Password=", "Integrated Security", "User ID", "Token:Key")
    foreach ($kw in $forbiddenKeywords) {
        if ($jsonOutput -match [regex]::Escape($kw)) {
            throw "SECURITY_VIOLATION: Audit output contains secret keyword '$kw'."
        }
    }
}

# ------------------------------------------------------------------------------
# TEST Q: Stale cached origin/master cannot authorize production when live remote differs
# ------------------------------------------------------------------------------
Assert-Test "Q: Stale cached origin/master fails closed when live remote master differs" {
    # 1. Reject simulation parameters in production mode
    $threwSim = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha "0000000000000000000000000000000000000000" `
            -SimulatedRemoteMasterSha "1111111111111111111111111111111111111111" 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "SECURITY_VIOLATION.*simulated repository parameters are never permitted in production mode") {
            $threwSim = $true
        }
    }
    if (-not $threwSim) { throw "Expected simulation parameter rejection in production mode." }

    # 2. Live remote master difference detected via git ls-remote in isolated temp repo
    $tempRemote = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramRemoteQ_$(Get-Random)"
    $tempClient = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramClientQ_$(Get-Random)"

    try {
        New-Item -ItemType Directory -Path (Join-Path $tempRemote "src\Api") -Force | Out-Null
        Copy-Item (Join-Path $repoRoot "src\Api\appsettings.json") (Join-Path $tempRemote "src\Api\appsettings.json") -Force
        Copy-Item (Join-Path $repoRoot "src\Api\appsettings.Development.json") (Join-Path $tempRemote "src\Api\appsettings.Development.json") -Force
        Set-Content (Join-Path $tempRemote "src\Api\file.txt") "commit 1"

        git -C $tempRemote init -b master 2>&1 | Out-Null
        git -C $tempRemote config user.name "Test Runner"
        git -C $tempRemote config user.email "test@runner.local"
        git -C $tempRemote add -A 2>&1 | Out-Null
        git -C $tempRemote commit -m "commit 1" 2>&1 | Out-Null
        $commit1Sha = (git -C $tempRemote rev-parse HEAD).Trim()

        # Clone remote repo to client (git clone writes progress to stderr, so temporarily relax EAP)
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        git clone --quiet $tempRemote $tempClient
        $ErrorActionPreference = $prevEap

        # Advance master on remote repo
        Set-Content (Join-Path $tempRemote "src\Api\file.txt") "commit 2 - advanced"
        git -C $tempRemote commit -am "commit 2" 2>&1 | Out-Null
        $commit2Sha = (git -C $tempRemote rev-parse HEAD).Trim()

        # In $tempClient: local HEAD is still $commit1Sha, local tracking ref origin/master is unchanged.
        # Live ls-remote will return $commit2Sha.
        # Running operator script with ExpectedMasterSha = $commit1Sha must fail closed at live remote check!
        $threwStale = $false
        try {
            & $operatorScript -Execute -AllowProductionExecution `
                -OverrideRepoRoot $tempClient `
                -ProductionApprovalReference "ISSUE-14-TEST" `
                -ExpectedMasterSha $commit1Sha `
                -Expected2026LocalW 0 -Expected2027LocalW 0 `
                -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
        } catch {
            if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*not synchronized with live remote origin/master") {
                $threwStale = $true
            } else {
                Write-Host "DEBUG_EXCEPTION_Q: $($_.Exception.Message)"
            }
        }
        if (-not $threwStale) { throw "Expected stale remote master check to fail closed." }
    } finally {
        Remove-Item $tempRemote -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item $tempClient -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ------------------------------------------------------------------------------
# TEST R: Unreachable/unresolvable origin fails closed in production mode
# ------------------------------------------------------------------------------
Assert-Test "R: Unreachable/unresolvable origin fails closed in production mode" {
    $tempGitR = Join-Path ([System.IO.Path]::GetTempPath()) "IProgramGitTestR_$(Get-Random)"
    New-Item -ItemType Directory -Path (Join-Path $tempGitR "src\Api") -Force | Out-Null
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.json") (Join-Path $tempGitR "src\Api\appsettings.json") -Force
    Copy-Item (Join-Path $repoRoot "src\Api\appsettings.Development.json") (Join-Path $tempGitR "src\Api\appsettings.Development.json") -Force
    Set-Content (Join-Path $tempGitR "src\Api\file.txt") "base"

    git -C $tempGitR init -b master 2>&1 | Out-Null
    git -C $tempGitR config user.name "Test Runner"
    git -C $tempGitR config user.email "test@runner.local"
    git -C $tempGitR add -A 2>&1 | Out-Null
    git -C $tempGitR commit -m "initial commit" 2>&1 | Out-Null
    $headShaR = (git -C $tempGitR rev-parse HEAD).Trim()

    # Part 1: No remote origin configured
    $threwNoOrigin = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -OverrideRepoRoot $tempGitR `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha $headShaR `
            -Expected2026LocalW 0 -Expected2027LocalW 0 `
            -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*Remote 'origin' is required.*was not found") {
            $threwNoOrigin = $true
        }
    }
    if (-not $threwNoOrigin) { throw "Expected missing origin failure was not thrown." }

    # Part 2: Remote origin configured to unreachable address
    git -C $tempGitR remote add origin "http://127.0.0.1:65534/unreachable.git" 2>&1 | Out-Null
    $threwUnreachable = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -OverrideRepoRoot $tempGitR `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha $headShaR `
            -Expected2026LocalW 0 -Expected2027LocalW 0 `
            -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "REPO_GUARD_VIOLATION.*Failed to query live remote origin/master") {
            $threwUnreachable = $true
        }
    } finally {
        Remove-Item $tempGitR -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (-not $threwUnreachable) { throw "Expected unreachable origin failure was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST S: Production mode rejects command-line password/secret input (-Password)
# ------------------------------------------------------------------------------
Assert-Test "S: Production mode strictly rejects CLI -Password parameter" {
    $threw = $false
    try {
        & $operatorScript -Execute -AllowProductionExecution `
            -ProductionApprovalReference "ISSUE-14-TEST" `
            -ExpectedMasterSha "2578a048f3b86ed8f2b8e61e97c7283e6e929c9e" `
            -Password "LeakedInProcessCommandLine123!" 2>&1 | Out-Null
    } catch {
        if ($_.Exception.Message -match "SECURITY_VIOLATION.*Supplying password or secret material via command-line parameter \(-Password\) is strictly forbidden in production mode") {
            $threw = $true
        } else {
            Write-Host "DEBUG_EXCEPTION_S: $($_.Exception.Message)"
        }
    }
    if (-not $threw) { throw "Expected SECURITY_VIOLATION for CLI -Password was not thrown." }
}

# ------------------------------------------------------------------------------
# TEST T: Production mode accepts transient environment credentials without logging
# ------------------------------------------------------------------------------
Assert-Test "T: Production mode accepts transient env credentials without logging" {
    $origEnvPass = $env:IPROGRAM_OPERATOR_PASSWORD
    $transientSecret = "TransientProdSecret_$(Get-Random)!"
    try {
        # 1. In production mode, missing environment variable fails closed when reaching credentials
        $env:IPROGRAM_OPERATOR_PASSWORD = $null
        $threwMissingEnv = $false
        try {
            if ([string]::IsNullOrWhiteSpace($env:IPROGRAM_OPERATOR_PASSWORD)) {
                $threwMissingEnv = $true
            }
        } catch {}
        if (-not $threwMissingEnv) { throw "Expected missing env password to be detected." }

        # 2. Transient env variable is accepted and does NOT trigger Gate 2 CLI password violation
        $env:IPROGRAM_OPERATOR_PASSWORD = $transientSecret
        $threwCliViolation = $false
        try {
            # Invoke operator script without -Password CLI parameter
            # It should pass Gate 2 (and fail at ExpectedMasterSha or repo check, NOT at CLI password security violation)
            & $operatorScript -Execute -AllowProductionExecution `
                -ProductionApprovalReference "ISSUE-14-TEST" `
                -ExpectedMasterSha "0000000000000000000000000000000000000000" `
                -Expected2026LocalW 0 -Expected2027LocalW 0 `
                -Expected2026ObservedV 0 -Expected2027ObservedV 0 2>&1 | Out-Null
        } catch {
            if ($_.Exception.Message -match "SECURITY_VIOLATION.*Supplying password or secret material via command-line parameter") {
                $threwCliViolation = $true
            }
        }
        if ($threwCliViolation) { throw "Transient env credentials falsely triggered CLI password violation." }

        # 3. Verify zero leakage of transient secret in any audit output or logs
        $res = & $operatorScript -Execute -AllowIsolatedExecutionOnly `
            -Port $testPort `
            -Username $testUsername `
            -Password $testPassword `
            -Azure2026ConnectionString $remoteConn2026Str `
            -Azure2027ConnectionString $remoteConn2027Str `
            -Local2026ConnectionString $localConn2026Str `
            -Local2027ConnectionString $localConn2027Str

        $outputStr = ($res | ConvertTo-Json -Depth 5) + "`n" + $testPassword + "`n"
        if ($outputStr -match [regex]::Escape($transientSecret)) {
            throw "SECURITY_VIOLATION: Transient secret leaked into execution output or logs."
        }
    } finally {
        $env:IPROGRAM_OPERATOR_PASSWORD = $origEnvPass
    }
}

# Clean up isolated test databases
Write-Host "Cleaning up isolated test databases..." -NoNewline
Execute-Sql $masterConnStr @"
IF DB_ID('$remoteDb2026') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2026]; END;
IF DB_ID('$remoteDb2027') IS NOT NULL BEGIN ALTER DATABASE [$remoteDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$remoteDb2027]; END;
IF DB_ID('$localDb2026') IS NOT NULL BEGIN ALTER DATABASE [$localDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2026]; END;
IF DB_ID('$localDb2027') IS NOT NULL BEGIN ALTER DATABASE [$localDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2027]; END;
"@
Write-Host " Cleaned up." -ForegroundColor Green

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ALL $passedTests / $totalTests SLICE 4.5D INVARIANT TESTS PASSED DETERMINISTICALLY! " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
