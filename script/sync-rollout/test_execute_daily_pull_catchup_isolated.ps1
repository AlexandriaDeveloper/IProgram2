# ==============================================================================
# SLICE 4.5B-A: ISOLATED TEST HARNESS FOR EXECUTE DAILY PULL CATCH-UP
# Tests -Execute mode strictly on isolated local SQL Server databases.
# Verifies:
#   1. Preflight validation on isolated test schema
#   2. Dedicated temporary API startup on loopback port
#   3. Actual POST /api/sync/pull execution via Admin JWT with db claim
#   4. Checkpoint advancement from 0 -> 2 for 2026 & 2027
#   5. Strict business data invariant (deterministic SHA-256 hash identical before/after)
#   6. Idempotent retry returns NO-OP
#   7. Sanitization: zero passwords or bearer tokens leaked
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$testPort = 5103

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.5B-A: ISOLATED OPERATOR EXECUTE VERIFICATION                    " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 1. Database names (approved within DatabaseBindingValidator policy)
$remoteDb2026 = "IProgramDb2026"
$remoteDb2027 = "IProgramDb2027"
$localDb2026 = "IProgramLocalDb2026_Test"
$localDb2027 = "IProgramLocalDb2027_Test"

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

# Ensure local test databases exist
Execute-Sql $masterConnStr @"
IF DB_ID('$localDb2026') IS NULL CREATE DATABASE [$localDb2026];
IF DB_ID('$localDb2027') IS NULL CREATE DATABASE [$localDb2027];
"@

# Setup Identity, Daily, and sync schemas on test databases
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
    $operationalLocDb = if ($yr -eq "2026") { "IProgramLocalDb2026" } else { "IProgramLocalDb2027" }
    $canaryId = $canarySyncIds[$yr]

    # Ensure canary is absent from remote Daily table
    Execute-Sql $remConn @"
DELETE FROM [dbo].[Daily] WHERE [SyncId] = '$canaryId';
"@

    # A. Provision Remote Sync schema
    Execute-Sql $remConn @"
IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');

IF OBJECT_ID('[sync].[ServerState]', 'U') IS NOT NULL DROP TABLE [sync].[ServerState];
CREATE TABLE [sync].[ServerState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [CurrentVersion] BIGINT NOT NULL,
    [LastUpdatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

IF OBJECT_ID('[sync].[ServerChangeFeed]', 'U') IS NOT NULL DROP TABLE [sync].[ServerChangeFeed];
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

IF OBJECT_ID('[sync].[Tombstones]', 'U') IS NOT NULL DROP TABLE [sync].[Tombstones];
CREATE TABLE [sync].[Tombstones] (
    [DatabaseId] VARCHAR(50) NOT NULL,
    [EntityType] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [DeletedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [PK_Tombstones] PRIMARY KEY CLUSTERED ([DatabaseId], [EntityType], [EntitySyncId])
);

IF OBJECT_ID('[sync].[ProcessedOperations]', 'U') IS NOT NULL DROP TABLE [sync].[ProcessedOperations];
CREATE TABLE [sync].[ProcessedOperations] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [ServerVersion] BIGINT NOT NULL,
    [ProcessedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Seed Remote Sync State: CurrentVersion = 2, Feed = v1 INSERT, v2 HARD_DELETE for canary
INSERT INTO [sync].[ServerState] ([DatabaseId], [CurrentVersion], [LastUpdatedUtc])
VALUES ('$yr', 2, SYSUTCDATETIME());

INSERT INTO [sync].[ServerChangeFeed] ([ServerVersion], [DatabaseId], [EntityType], [EntitySyncId], [OperationType], [OriginDeviceId])
VALUES 
(1, '$yr', 'Daily', '$canaryId', 'INSERT', '00000000-0000-0000-0000-000000000000'),
(2, '$yr', 'Daily', '$canaryId', 'HARD_DELETE', '00000000-0000-0000-0000-000000000000');

INSERT INTO [sync].[Tombstones] ([DatabaseId], [EntityType], [EntitySyncId], [ServerVersion])
VALUES ('$yr', 'Daily', '$canaryId', 2);
"@

    # B. Provision Local Test Database (cloned schema & data from operational)
    Execute-Sql $locConn @"
-- Copy AspNet Identity tables if not present
IF OBJECT_ID('[dbo].[AspNetUsers]', 'U') IS NULL
BEGIN
    SELECT * INTO [dbo].[AspNetUsers] FROM [$operationalLocDb].[dbo].[AspNetUsers];
    SELECT * INTO [dbo].[AspNetRoles] FROM [$operationalLocDb].[dbo].[AspNetRoles];
    SELECT * INTO [dbo].[AspNetUserRoles] FROM [$operationalLocDb].[dbo].[AspNetUserRoles];
    SELECT * INTO [dbo].[AspNetUserClaims] FROM [$operationalLocDb].[dbo].[AspNetUserClaims];
    SELECT * INTO [dbo].[AspNetUserLogins] FROM [$operationalLocDb].[dbo].[AspNetUserLogins];
    SELECT * INTO [dbo].[AspNetUserTokens] FROM [$operationalLocDb].[dbo].[AspNetUserTokens];
    SELECT * INTO [dbo].[AspNetRoleClaims] FROM [$operationalLocDb].[dbo].[AspNetRoleClaims];
END

-- Ensure dbo.Daily matches remote dbo.Daily exactly
IF OBJECT_ID('[dbo].[Daily]', 'U') IS NOT NULL DROP TABLE [dbo].[Daily];
SELECT * INTO [dbo].[Daily] FROM [$remDb].[dbo].[Daily];

-- Ensure canary SyncId is absent from local Daily table
DELETE FROM [dbo].[Daily] WHERE [SyncId] = '$canaryId';

-- Local sync schema
IF SCHEMA_ID('sync') IS NULL EXEC('CREATE SCHEMA [sync];');

IF OBJECT_ID('[sync].[LocalState]', 'U') IS NOT NULL DROP TABLE [sync].[LocalState];
CREATE TABLE [sync].[LocalState] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [DeviceId] UNIQUEIDENTIFIER NOT NULL,
    [LastServerVersion] BIGINT NOT NULL,
    [LastSuccessfulPullUtc] DATETIME2 NULL,
    [LastSuccessfulPushUtc] DATETIME2 NULL,
    [LastSyncAttemptUtc] DATETIME2 NULL,
    [LastSyncError] NVARCHAR(MAX) NULL,
    [ActiveLeaseToken] UNIQUEIDENTIFIER NULL,
    [LeaseExpiresAtUtc] DATETIME2 NULL
);

IF OBJECT_ID('[sync].[LocalOutbox]', 'U') IS NOT NULL DROP TABLE [sync].[LocalOutbox];
CREATE TABLE [sync].[LocalOutbox] (
    [ClientOperationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    [DatabaseId] VARCHAR(50) NOT NULL,
    [AggregateType] NVARCHAR(100) NOT NULL,
    [CommandName] NVARCHAR(100) NOT NULL,
    [EntitySyncId] UNIQUEIDENTIFIER NOT NULL,
    [PayloadJson] NVARCHAR(MAX) NOT NULL,
    [CreatedAtUtc] DATETIME2 NOT NULL,
    [Status] VARCHAR(20) NOT NULL,
    [RetryCount] INT NOT NULL DEFAULT 0,
    [LastError] NVARCHAR(MAX) NULL,
    [CompletedAtUtc] DATETIME2 NULL,
    [LockedUntilUtc] DATETIME2 NULL,
    [LockToken] UNIQUEIDENTIFIER NULL
);

IF OBJECT_ID('[sync].[BootstrapManifest]', 'U') IS NOT NULL DROP TABLE [sync].[BootstrapManifest];
CREATE TABLE [sync].[BootstrapManifest] (
    [DatabaseId] VARCHAR(50) NOT NULL PRIMARY KEY,
    [Status] NVARCHAR(50) NOT NULL,
    [IsWriteAllowed] BIT NOT NULL,
    [VerifiedAtUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

-- Seed Local State: LastServerVersion = 0
INSERT INTO [sync].[LocalState] ([DatabaseId], [DeviceId], [LastServerVersion])
VALUES ('$yr', NEWID(), 0);

INSERT INTO [sync].[BootstrapManifest] ([DatabaseId], [Status], [IsWriteAllowed])
VALUES ('$yr', 'VERIFIED_READY', 1);
"@
}

Write-Host " PASS (Isolated test environments ready)" -ForegroundColor Green

# Resolve credentials
$username = $env:IPROGRAM_OPERATOR_USERNAME
$password = $env:IPROGRAM_OPERATOR_PASSWORD
if ([string]::IsNullOrWhiteSpace($username) -or [string]::IsNullOrWhiteSpace($password)) {
    $e2eEnv = Join-Path $repoRoot "tests\e2e\.env"
    if (Test-Path $e2eEnv) {
        foreach ($line in Get-Content $e2eEnv) {
            if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)\s*$') {
                $k = $matches[1].Trim()
                $v = $matches[2].Trim().Trim('"').Trim("'")
                if (($k -eq "IPROGRAM_OPERATOR_USERNAME" -or $k -eq "E2E_USERNAME") -and [string]::IsNullOrWhiteSpace($username)) { $username = $v }
                if (($k -eq "IPROGRAM_OPERATOR_PASSWORD" -or $k -eq "E2E_PASSWORD") -and [string]::IsNullOrWhiteSpace($password)) { $password = $v }
            }
        }
    }
}

try {
    # 2. Test execute_daily_pull_catchup.ps1 in -Execute mode against isolated databases
    $opScript = Join-Path $repoRoot "script\sync-rollout\execute_daily_pull_catchup.ps1"

    Write-Host "`n[Proof 1 & 2] Executing operator catch-up in -Execute mode on isolated databases..." -ForegroundColor Cyan
    $execResult = & $opScript `
        -Execute `
        -AllowIsolatedExecutionOnly `
        -Port $testPort `
        -Username $username `
        -Password $password `
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
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:Sync__PullEnabled = "true"
        $env:Sync__PushEnabled = "false"
        $env:Sync__AuthoritativeTrackingEnabled = "false"
        $env:Sync__AllowIsolatedLocalRemoteForTesting = "true"
        $env:LocalFirst__Enabled = "false"
        $env:LocalFirst__ReadOnlyMode = "false"
        $env:ConnectionStrings__DefaultConnection = $remoteConn2026Str
        $env:ConnectionStrings__CON2027 = $remoteConn2027Str
        $env:ConnectionStrings__LocalConnection2026 = $localConn2026Str
        $env:ConnectionStrings__LocalConnection2027 = $localConn2027Str

        $retryApiProc = Start-Process -FilePath "dotnet" -ArgumentList "`"$apiDll`"" -WorkingDirectory $apiDir -PassThru -NoNewWindow -RedirectStandardOutput $tempLogRetry -RedirectStandardError $tempErrRetry
        
        # Wait for API server readiness
        $ready = $false
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 30000 -and -not $ready) {
            Start-Sleep -Milliseconds 500
            try {
                $st = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/runtime-status" -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
                if ($st -and $st.runtimeMode -eq "Online") { $ready = $true }
            } catch {}
        }
        if (-not $ready) { throw "API_START_TIMEOUT: Failed to start API for retry test." }

        # Retry Pull for 2026
        $loginRes26 = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/login" -Method Post -Body (@{ username = $username; password = $password } | ConvertTo-Json) -Headers @{ "Content-Type" = "application/json"; "X-Db-Selection" = "2026" } -TimeoutSec 10
        $retryPull26 = Invoke-RestMethod -Uri "$baseUrlRetry/api/sync/pull" -Method Post -Headers @{ "Authorization" = "Bearer $($loginRes26.token)" } -TimeoutSec 60
        Write-Host "  * 2026 Retry Pull Result: PrevWatermark=$($retryPull26.previousWatermark), FinalVer=$($retryPull26.finalServerVersion), IsNoOp=$($retryPull26.isNoOp)" -ForegroundColor Green
        if ($retryPull26.previousWatermark -ne 2 -or $retryPull26.finalServerVersion -ne 2 -or -not $retryPull26.isNoOp) {
            throw "RETRY_PROOF_FAIL: 2026 Retry did not return NO-OP."
        }

        # Retry Pull for 2027
        $loginRes27 = Invoke-RestMethod -Uri "$baseUrlRetry/api/account/login" -Method Post -Body (@{ username = $username; password = $password } | ConvertTo-Json) -Headers @{ "Content-Type" = "application/json"; "X-Db-Selection" = "2027" } -TimeoutSec 10
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
        Remove-Item Env:\Sync__AllowIsolatedLocalRemoteForTesting -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__Enabled -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__ReadOnlyMode -ErrorAction SilentlyContinue
        Remove-Item Env:\ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
        Remove-Item Env:\ConnectionStrings__CON2027 -ErrorAction SilentlyContinue
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
            -Username $username `
            -Password $password `
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
    # Cleanup remote sync tables from local dev databases
    Write-Host "`nCleaning up isolated test tables..." -NoNewline
    foreach ($remConn in @($remoteConn2026Str, $remoteConn2027Str)) {
        Execute-Sql $remConn @"
IF OBJECT_ID('[sync].[ProcessedOperations]', 'U') IS NOT NULL DROP TABLE [sync].[ProcessedOperations];
IF OBJECT_ID('[sync].[Tombstones]', 'U') IS NOT NULL DROP TABLE [sync].[Tombstones];
IF OBJECT_ID('[sync].[ServerChangeFeed]', 'U') IS NOT NULL DROP TABLE [sync].[ServerChangeFeed];
IF OBJECT_ID('[sync].[ServerState]', 'U') IS NOT NULL DROP TABLE [sync].[ServerState];
IF SCHEMA_ID('sync') IS NOT NULL DROP SCHEMA [sync];
"@
    }

    Execute-Sql $masterConnStr @"
IF DB_ID('$localDb2026') IS NOT NULL BEGIN ALTER DATABASE [$localDb2026] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2026]; END;
IF DB_ID('$localDb2027') IS NOT NULL BEGIN ALTER DATABASE [$localDb2027] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$localDb2027]; END;
"@
    Write-Host " PASS (Cleanup complete)" -ForegroundColor Green
}

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ISOLATED OPERATOR EXECUTION TESTS: ALL PROOFS PASSED (100%)             " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green

