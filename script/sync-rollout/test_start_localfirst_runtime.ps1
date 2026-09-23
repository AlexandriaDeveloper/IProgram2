# ==============================================================================
# TEST SUITE: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER (REVISED)
# Completely isolated automated regression suite validating all P0 and P1 fixes:
#   P0-1: Steady-state vs initial baseline validation; restart with PENDING outbox
#   P0-2: Git safety gate (master, clean tree, origin/master match)
#   P0-3: Physical local binding validation & CLI override guards
#   P0-4: Token key secret boundary (no user-secrets enumeration)
#   P0-5: Structured runtime state & foreign PID reuse safety
#   P0-6: Test suite physical isolation using transient fixture DBs (ZERO operational DB access)
#   P0-7: Artifact identity & local build verification
#   P1:   Log lifecycle and sanitization
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$launcherScript = Join-Path $PSScriptRoot "start_localfirst_runtime.ps1"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  TEST SUITE: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER (ISOLATED)          " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# Load pure functions for testing
. $launcherScript -ExportFunctionsOnly

$passedTests = 0
$totalTests = 0

function Assert-Test([string]$Name, [scriptblock]$Block) {
    $script:totalTests++
    Write-Host "[TEST $script:totalTests] $Name..." -NoNewline
    try {
        & $Block
        $script:passedTests++
        Write-Host " PASS" -ForegroundColor Green
    } catch {
        Write-Host " FAIL" -ForegroundColor Red
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

# --- Fixture Helpers for Complete Isolation (P0-6) ---
$fixtureDb2026 = "IProgramLocalDb2026_Test"
$fixtureDb2027 = "IProgramLocalDb2027_Test"
$fixtureConn2026 = "Server=localhost;Database=$fixtureDb2026;Trusted_Connection=True;TrustServerCertificate=True;"
$fixtureConn2027 = "Server=localhost;Database=$fixtureDb2027;Trusted_Connection=True;TrustServerCertificate=True;"

function Initialize-TestFixtureDatabases {
    $masterConn = New-Object SqlConnection("Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;")
    $masterConn.Open()
    try {
        foreach ($dbName in @($fixtureDb2026, $fixtureDb2027)) {
            $yr = if ($dbName -match "2026") { "2026" } else { "2027" }
            $cmd = $masterConn.CreateCommand()
            $cmd.CommandText = @"
IF DB_ID('$dbName') IS NOT NULL 
BEGIN
    ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$dbName];
END;
CREATE DATABASE [$dbName];
"@
            $cmd.ExecuteNonQuery() | Out-Null

            $cmd.CommandText = @"
USE [$dbName];
ALTER DATABASE [$dbName] SET COMPATIBILITY_LEVEL = 120;
EXEC('CREATE SCHEMA [sync]');

CREATE TABLE [sync].[BootstrapManifest] (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Status NVARCHAR(50) NOT NULL,
    IsWriteAllowed BIT NOT NULL,
    CreatedAtUtc DATETIME2 NOT NULL
);

CREATE TABLE [sync].[LocalState] (
    DatabaseId NVARCHAR(50) PRIMARY KEY,
    LastServerVersion BIGINT NOT NULL,
    ActiveLeaseToken NVARCHAR(MAX) NULL,
    LeaseExpiresAtUtc DATETIME2 NULL
);

CREATE TABLE [sync].[LocalOutbox] (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    DatabaseId NVARCHAR(50) NOT NULL,
    EventType NVARCHAR(50) NOT NULL,
    EntityName NVARCHAR(50) NOT NULL,
    SyncId UNIQUEIDENTIFIER NOT NULL,
    PayloadJson NVARCHAR(MAX) NOT NULL,
    Status NVARCHAR(50) NOT NULL,
    AttemptCount INT NOT NULL,
    CreatedAtUtc DATETIME2 NOT NULL
);

CREATE TABLE [dbo].[Daily] (
    SyncId UNIQUEIDENTIFIER PRIMARY KEY,
    Name NVARCHAR(MAX) NULL,
    DailyDate DATETIME2 NULL,
    Closed BIT NOT NULL,
    CreatedAt DATETIME2 NULL,
    CreatedBy NVARCHAR(MAX) NULL,
    UpdatedAt DATETIME2 NULL,
    UpdatedBy NVARCHAR(MAX) NULL,
    DeactivatedAt DATETIME2 NULL,
    DeactivatedBy NVARCHAR(MAX) NULL,
    IsActive BIT NOT NULL
);

CREATE TABLE [dbo].[AspNetUsers] (
    Id NVARCHAR(450) PRIMARY KEY,
    UserName NVARCHAR(256) NULL,
    NormalizedUserName NVARCHAR(256) NULL,
    Email NVARCHAR(256) NULL,
    NormalizedEmail NVARCHAR(256) NULL,
    EmailConfirmed BIT NOT NULL DEFAULT 0,
    PasswordHash NVARCHAR(MAX) NULL,
    SecurityStamp NVARCHAR(MAX) NULL,
    ConcurrencyStamp NVARCHAR(MAX) NULL,
    PhoneNumber NVARCHAR(MAX) NULL,
    PhoneNumberConfirmed BIT NOT NULL DEFAULT 0,
    TwoFactorEnabled BIT NOT NULL DEFAULT 0,
    LockoutEnd DATETIMEOFFSET NULL,
    LockoutEnabled BIT NOT NULL DEFAULT 0,
    AccessFailedCount INT NOT NULL DEFAULT 0
);

CREATE TABLE [dbo].[AspNetRoles] (
    Id NVARCHAR(450) PRIMARY KEY,
    Name NVARCHAR(256) NULL,
    NormalizedName NVARCHAR(256) NULL,
    ConcurrencyStamp NVARCHAR(MAX) NULL
);

INSERT INTO [sync].[BootstrapManifest] (Status, IsWriteAllowed, CreatedAtUtc) VALUES ('VERIFIED_READY', 1, SYSUTCDATETIME());
INSERT INTO [sync].[LocalState] (DatabaseId, LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc) VALUES ('$yr', 8, NULL, NULL);
INSERT INTO [dbo].[AspNetUsers] (Id, UserName, NormalizedUserName) VALUES ('test-user-id-$yr', 'testuser', 'TESTUSER');
INSERT INTO [dbo].[AspNetRoles] (Id, Name, NormalizedName) VALUES ('admin-role-id-$yr', 'Admin', 'ADMIN');
INSERT INTO [dbo].[Daily] (SyncId, Name, DailyDate, Closed, IsActive) VALUES (NEWID(), 'Fixture Daily $yr', SYSUTCDATETIME(), 0, 1);
"@
            $cmd.ExecuteNonQuery() | Out-Null
        }
    } finally {
        $masterConn.Close()
    }
}

function Remove-TestFixtureDatabases {
    $masterConn = New-Object SqlConnection("Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;")
    $masterConn.Open()
    try {
        foreach ($dbName in @($fixtureDb2026, $fixtureDb2027)) {
            $cmd = $masterConn.CreateCommand()
            $cmd.CommandText = @"
IF DB_ID('$dbName') IS NOT NULL 
BEGIN
    ALTER DATABASE [$dbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$dbName];
END;
"@
            $cmd.ExecuteNonQuery() | Out-Null
        }
    } finally {
        $masterConn.Close()
    }
}

try {
    # Initialize isolated test fixtures
    Initialize-TestFixtureDatabases

    # --- TEST 1: Child Environment Composition Invariants ---
    Assert-Test "Child process environment composition verifies strict LocalFirst & tripwire invariants" {
        $envMap = Get-LocalFirstChildEnvironment -Port 5000 `
            -Local2026ConnStr $fixtureConn2026 `
            -Local2027ConnStr $fixtureConn2027 `
            -TokenKey "test_token_key_64_characters_long_for_hmac_sha256_validation_12345678" `
            -IsTestMode $true

        if ($envMap["LocalFirst__Enabled"] -ne "true") { throw "LocalFirst__Enabled must be 'true'" }
        if ($envMap["LocalFirst__ReadOnlyMode"] -ne "false") { throw "LocalFirst__ReadOnlyMode must be 'false'" }
        if ($envMap["Sync__PullEnabled"] -ne "false") { throw "Sync__PullEnabled must be 'false'" }
        if ($envMap["Sync__PushEnabled"] -ne "false") { throw "Sync__PushEnabled must be 'false'" }
        if ($envMap["Sync__AuthoritativeTrackingEnabled"] -ne "true") { throw "Sync__AuthoritativeTrackingEnabled must be 'true'" }
        if ($envMap["LegacyMigration__Enabled"] -ne "false") { throw "LegacyMigration__Enabled must be 'false'" }
        if ($envMap["ASPNETCORE_URLS"] -ne "http://127.0.0.1:5000") { throw "ASPNETCORE_URLS must be http://127.0.0.1:5000" }

        # Tripwires: Remote connection strings MUST NOT point to Azure
        if ($envMap["ConnectionStrings__DefaultConnection"] -match "(?i)\.database\.windows\.net") {
            throw "Tripwire DefaultConnection contains Azure host!"
        }
        if ($envMap["ConnectionStrings__DefaultConnection"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
            throw "Tripwire DefaultConnection must reference DISABLED_REMOTE_TRIPWIRE"
        }
        if ($envMap["ConnectionStrings__CON2027"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
            throw "Tripwire CON2027 must reference DISABLED_REMOTE_TRIPWIRE"
        }

        # Port range checks
        foreach ($p in @(0, -1, 70000)) {
            $failed = $false
            try {
                Get-LocalFirstChildEnvironment -Port $p -Local2026ConnStr $fixtureConn2026 -Local2027ConnStr $fixtureConn2027 -TokenKey "key" | Out-Null
            } catch {
                if ($_.Exception.Message -match "INVALID_PORT") { $failed = $true }
            }
            if (-not $failed) { throw "Expected INVALID_PORT for port $p" }
        }
    }

    # --- TEST 2: Physical Local Binding & Catalog Validation (P0-3) ---
    Assert-Test "Assert-LocalPhysicalBinding enforces local endpoints, exact catalogs, and rejects Azure/remote" {
        # A. Azure host rejection
        $azureStr = "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramLocalDb2026;Integrated Security=True;"
        $failedAzure = $false
        try {
            Assert-LocalPhysicalBinding -ConnStr $azureStr -ExpectedYear "2026" -IsTestMode $false | Out-Null
        } catch {
            if ($_.Exception.Message -match "forbidden Azure host") { $failedAzure = $true }
        }
        if (-not $failedAzure) { throw "Failed to reject Azure host!" }

        # B. Remote non-local endpoint rejection
        $remoteStr = "Server=192.168.1.150;Initial Catalog=IProgramLocalDb2026;Integrated Security=True;"
        $failedRemote = $false
        try {
            Assert-LocalPhysicalBinding -ConnStr $remoteStr -ExpectedYear "2026" -IsTestMode $false | Out-Null
        } catch {
            if ($_.Exception.Message -match "non-local host") { $failedRemote = $true }
        }
        if (-not $failedRemote) { throw "Failed to reject remote IP endpoint!" }

        # C. Wrong catalog in operational mode rejection
        $wrongCatalogStr = "Server=localhost;Initial Catalog=WrongDbCatalog2026;Integrated Security=True;"
        $failedCatalog = $false
        try {
            Assert-LocalPhysicalBinding -ConnStr $wrongCatalogStr -ExpectedYear "2026" -IsTestMode $false | Out-Null
        } catch {
            if ($_.Exception.Message -match "must target catalog 'IProgramLocalDb2026'") { $failedCatalog = $true }
        }
        if (-not $failedCatalog) { throw "Failed to reject incorrect operational catalog!" }

        # D. Test mode guard: rejects operational catalogs in test mode (P0-6 guard)
        $opInTestStr = "Server=localhost;Initial Catalog=IProgramLocalDb2026;Integrated Security=True;"
        $failedOpInTest = $false
        try {
            Assert-LocalPhysicalBinding -ConnStr $opInTestStr -ExpectedYear "2026" -IsTestMode $true | Out-Null
        } catch {
            if ($_.Exception.Message -match "forbidden from targeting operational catalog") { $failedOpInTest = $true }
        }
        if (-not $failedOpInTest) { throw "Failed to guard against operational catalog in test mode!" }

        # E. Valid fixture binding passes in test mode
        $validFixture = Assert-LocalPhysicalBinding -ConnStr $fixtureConn2026 -ExpectedYear "2026" -IsTestMode $true
        if ($validFixture.InitialCatalog -ne $fixtureDb2026) { throw "Fixture binding mismatch" }
    }

    # --- TEST 3: CLI Connection String & Git Override Guards (P0-3 & P0-A) ---
    Assert-Test "Operational mode strictly rejects CLI connection string and git bypass flags" {
        # A. Connection string override rejected
        $failedConn = $false
        try {
            & $launcherScript Status -OverrideLocal2026ConnStr "Server=localhost;Database=fake;" | Out-Null
        } catch {
            if ($_.Exception.Message -match "CLI_OVERRIDE_FORBIDDEN") { $failedConn = $true }
        }
        if (-not $failedConn) { throw "Failed to reject CLI connection string override in operational mode!" }

        # B. -SkipGitVerification rejected in operational mode
        $failedSkipGit = $false
        try {
            & $launcherScript Status -SkipGitVerification | Out-Null
        } catch {
            if ($_.Exception.Message -match "CLI_OVERRIDE_FORBIDDEN") { $failedSkipGit = $true }
        }
        if (-not $failedSkipGit) { throw "Failed to reject -SkipGitVerification in operational mode!" }

        # C. -AllowNonMaster rejected in operational mode
        $failedAllowNonMaster = $false
        try {
            & $launcherScript Status -AllowNonMaster | Out-Null
        } catch {
            if ($_.Exception.Message -match "CLI_OVERRIDE_FORBIDDEN") { $failedAllowNonMaster = $true }
        }
        if (-not $failedAllowNonMaster) { throw "Failed to reject -AllowNonMaster in operational mode!" }
    }

    # --- TEST 4: Git Operational Safety Gate (P0-2 & P0-A) ---
    Assert-Test "Git safety gate validates master branch, rejects bypass flags in operational mode, and allows them in test mode" {
        # A. In operational mode (IsTestMode = false), passing SkipGit throws CLI_OVERRIDE_FORBIDDEN
        $failedOpSkip = $false
        try {
            Assert-GitOperationalSafety -RepoRoot $repoRoot -SkipGit $true -IsTestMode $false | Out-Null
        } catch {
            if ($_.Exception.Message -match "CLI_OVERRIDE_FORBIDDEN") { $failedOpSkip = $true }
        }
        if (-not $failedOpSkip) { throw "Assert-GitOperationalSafety failed to reject SkipGit in operational mode!" }

        # B. In operational mode (IsTestMode = false), passing AllowNonMaster throws CLI_OVERRIDE_FORBIDDEN
        $failedOpNonMaster = $false
        try {
            Assert-GitOperationalSafety -RepoRoot $repoRoot -AllowNonMaster $true -IsTestMode $false | Out-Null
        } catch {
            if ($_.Exception.Message -match "CLI_OVERRIDE_FORBIDDEN") { $failedOpNonMaster = $true }
        }
        if (-not $failedOpNonMaster) { throw "Assert-GitOperationalSafety failed to reject AllowNonMaster in operational mode!" }

        # C. Non-master branch without bypass throws fail-closed
        $currentBranch = (git -C $repoRoot branch --show-current).Trim()
        if ($currentBranch -ne "master") {
            $failedBranch = $false
            try {
                Assert-GitOperationalSafety -RepoRoot $repoRoot -SkipGit $false -AllowNonMaster $false -IsTestMode $false | Out-Null
            } catch {
                if ($_.Exception.Message -match "must run from 'master' branch") { $failedBranch = $true }
            }
            if (-not $failedBranch) { throw "Failed to reject non-master branch in operational mode!" }
        }

        # D. In test mode (IsTestMode = true), AllowNonMaster passes
        $testPass = Assert-GitOperationalSafety -RepoRoot $repoRoot -SkipGit $false -AllowNonMaster $true -IsTestMode $true
        if (-not $testPass) { throw "Test mode Git bypass returned false" }

        # E. In test mode (IsTestMode = true), SkipGit passes
        $testSkipPass = Assert-GitOperationalSafety -RepoRoot $repoRoot -SkipGit $true -IsTestMode $true
        if (-not $testSkipPass) { throw "Test mode SkipGit returned false" }
    }

    # --- TEST 5: Token Key Secret Boundary (P0-4) ---
    Assert-Test "Token key resolution fails closed without user-secrets enumeration in operational mode" {
        # Temporarily clear Token__Key env vars in current scope
        $origKey1 = $env:Token__Key
        $origKey2 = $env:Token_Key
        $env:Token__Key = $null
        $env:Token_Key = $null

        $failedToken = $false
        try {
            # Start in operational mode with missing token key
            & $launcherScript Start | Out-Null
        } catch {
            if ($_.Exception.Message -match "TOKEN_KEY_REQUIRED") { $failedToken = $true }
        } finally {
            $env:Token__Key = $origKey1
            $env:Token_Key = $origKey2
        }

        if (-not $failedToken) { throw "Failed to require Token__Key in operational mode!" }
    }

    # --- TEST 6: Structured PID Safety & Foreign PID Reuse Rejection (P0-5) ---
    Assert-Test "Stop and Status refuse to touch foreign processes on PID reuse" {
        $testStateFile = [System.IO.Path]::GetTempFileName()
        
        try {
            # Craft fake state file pointing to current powershell process (which is NOT dotnet and has wrong start time)
            $foreignPid = $PID
            $fakeState = [ordered]@{
                pid          = $foreignPid
                startTimeUtc = "2020-01-01T00:00:00.0000000Z" # Mismatched start time
                processName  = "dotnet"
                port         = 5198
                gitCommitSha = "testsha"
                logPath      = "fake.log"
            }
            $fakeState | ConvertTo-Json | Out-File -FilePath $testStateFile -Force

            # A. Status must detect FOREIGN_PID_REUSED
            $statusRes = & $launcherScript Status -OverrideStateFilePath $testStateFile
            if ($statusRes.Status -ne "FOREIGN_PID_REUSED") {
                throw "Expected Status=FOREIGN_PID_REUSED, got $($statusRes.Status)"
            }

            # B. Stop must throw SAFETY_REFUSAL and NOT kill the process
            $failedSafety = $false
            try {
                & $launcherScript Stop -OverrideStateFilePath $testStateFile | Out-Null
            } catch {
                if ($_.Exception.Message -match "SAFETY_REFUSAL") { $failedSafety = $true }
            }

            if (-not $failedSafety) { throw "Stop failed to throw SAFETY_REFUSAL for foreign PID!" }

            # Verify current powershell process is still alive!
            $procCheck = Get-Process -Id $foreignPid -ErrorAction SilentlyContinue
            if ($null -eq $procCheck) {
                throw "CRITICAL FAILURE: Foreign process $foreignPid was killed!"
            }
        } finally {
            Remove-Item $testStateFile -Force -ErrorAction SilentlyContinue
        }
    }

    # --- TEST 7: Steady-State vs Initial Cutover Baseline Preflight (P0-1) ---
    Assert-Test "Steady-state preflight allows PENDING outbox rows while Initial Baseline rejects them" {
        # A. Insert a PENDING outbox row in fixture 2026 (simulating offline Daily edit)
        $conn = New-Object SqlConnection($fixtureConn2026)
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
INSERT INTO [sync].[LocalOutbox] (DatabaseId, EventType, EntityName, SyncId, PayloadJson, Status, AttemptCount, CreatedAtUtc)
VALUES ('2026', 'UPSERT', 'Daily', NEWID(), '{"test":true}', 'PENDING', 0, SYSUTCDATETIME());
"@
        $cmd.ExecuteNonQuery() | Out-Null
        $conn.Close()

        # B. Steady-state preflight (ValidateInitialCutoverBaseline = false) MUST PASS!
        $steadyPass = Assert-LocalFirstLauncherPreflight -RepoRoot $repoRoot `
            -Local2026ConnStr $fixtureConn2026 `
            -Local2027ConnStr $fixtureConn2027 `
            -ValidateInitialCutoverBaseline $false `
            -SkipGit $true `
            -IsTestMode $true

        if (-not $steadyPass) { throw "Steady-state preflight failed with PENDING outbox row!" }

        # C. Initial Cutover Baseline (ValidateInitialCutoverBaseline = true) MUST FAIL with pending rows!
        $failedBaseline = $false
        try {
            Assert-LocalFirstLauncherPreflight -RepoRoot $repoRoot `
                -Local2026ConnStr $fixtureConn2026 `
                -Local2027ConnStr $fixtureConn2027 `
                -ValidateInitialCutoverBaseline $true `
                -SkipGit $true `
                -IsTestMode $true | Out-Null
        } catch {
            if ($_.Exception.Message -match "Initial Cutover Baseline requires 0 pending outbox rows") {
                $failedBaseline = $true
            }
        }
        if (-not $failedBaseline) { throw "Initial baseline failed to reject pending outbox row!" }

        # Clean out the pending row for subsequent tests
        $conn = New-Object SqlConnection($fixtureConn2026)
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';"
        $cmd.ExecuteNonQuery() | Out-Null
        $conn.Close()
    }

    # --- TEST 8: Full Steady-State Lifecycle with PENDING Outbox & Durability (P0-1 & P0-6) ---
    Assert-Test "Process lifecycle starts and restarts cleanly with PENDING outbox on isolated fixture" {
        $lifecyclePort = 5197
        $testStateFile = [System.IO.Path]::GetTempFileName()
        Remove-Item $testStateFile -Force -ErrorAction SilentlyContinue

        # Seed a PENDING outbox row to prove restart durability in offline write state
        $conn = New-Object SqlConnection($fixtureConn2026)
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
INSERT INTO [sync].[LocalOutbox] (DatabaseId, EventType, EntityName, SyncId, PayloadJson, Status, AttemptCount, CreatedAtUtc)
VALUES ('2026', 'UPSERT', 'Daily', NEWID(), '{"Name":"Legitimate Offline Daily Write"}', 'PENDING', 0, SYSUTCDATETIME());
"@
        $cmd.ExecuteNonQuery() | Out-Null
        $conn.Close()

        try {
            # 1. Start Runtime on test port pointing to fixture databases
            $startRes = & $launcherScript Start `
                -Port $lifecyclePort `
                -AllowIsolatedTestMode `
                -SkipGitVerification `
                -OverrideLocal2026ConnStr $fixtureConn2026 `
                -OverrideLocal2027ConnStr $fixtureConn2027 `
                -OverrideStateFilePath $testStateFile

            if ($startRes.Status -ne "RUNNING") {
                throw "Expected Status=RUNNING, got $($startRes.Status)"
            }
            $procPid = $startRes.Pid

            # 2. Verify /health responds 200 OK
            $healthResp = Invoke-WebRequest -Uri "http://127.0.0.1:$lifecyclePort/health" -Method Get -TimeoutSec 5 -UseBasicParsing
            if ($healthResp.StatusCode -ne 200) {
                throw "Health check returned status code $($healthResp.StatusCode)"
            }

            # 3. Verify Status
            $statusRes = & $launcherScript Status -Port $lifecyclePort -OverrideStateFilePath $testStateFile
            if ($statusRes.Status -ne "RUNNING" -or $statusRes.Pid -ne $procPid) {
                throw "Status mismatch: $($statusRes.Status)"
            }

            # 4. Stop Runtime
            $stopRes = & $launcherScript Stop -Port $lifecyclePort -OverrideStateFilePath $testStateFile
            Start-Sleep -Seconds 1

            $deadProc = Get-Process -Id $procPid -ErrorAction SilentlyContinue
            if ($null -ne $deadProc) {
                throw "Process $procPid still running after Stop command!"
            }

            # 5. Verify PENDING outbox row was NOT mutated or deleted during runtime
            $conn = New-Object SqlConnection($fixtureConn2026)
            $conn.Open()
            $cmd = $conn.CreateCommand()
            $cmd.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026' AND Status = 'PENDING';"
            $stillPending = [int]$cmd.ExecuteScalar()
            $conn.Close()

            if ($stillPending -ne 1) {
                throw "PENDING outbox row was unexpectedly modified/cleared during shutdown (Found: $stillPending)!"
            }

            # 6. Restart Runtime with the PENDING outbox row present!
            $restartRes = & $launcherScript Start `
                -Port $lifecyclePort `
                -AllowIsolatedTestMode `
                -SkipGitVerification `
                -OverrideLocal2026ConnStr $fixtureConn2026 `
                -OverrideLocal2027ConnStr $fixtureConn2027 `
                -OverrideStateFilePath $testStateFile

            if ($restartRes.Status -ne "RUNNING") {
                throw "Expected Status=RUNNING on restart, got $($restartRes.Status)"
            }

            # Re-verify /health after restart
            $healthResp2 = Invoke-WebRequest -Uri "http://127.0.0.1:$lifecyclePort/health" -Method Get -TimeoutSec 5 -UseBasicParsing
            if ($healthResp2.StatusCode -ne 200) {
                throw "Post-restart health check returned $($healthResp2.StatusCode)"
            }

            # Stop after restart
            & $launcherScript Stop -Port $lifecyclePort -OverrideStateFilePath $testStateFile | Out-Null
        } finally {
            if (Test-Path $testStateFile) {
                $remState = Get-RuntimeState $testStateFile
                if ($remState -and $remState.pid) {
                    $p = Get-Process -Id ([int]$remState.pid) -ErrorAction SilentlyContinue
                    if ($p -and $p.ProcessName -ieq "dotnet") { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
                }
                Remove-Item $testStateFile -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # --- TEST 9: Artifact Identity & Deterministic Build (P0-7 & P0-B) ---
    Assert-Test "Assert-LocalReleaseArtifact unconditionally executes local build in operational mode" {
        # A. In operational mode (IsTestMode = false), Assert-LocalReleaseArtifact executes build and returns valid DLL
        $builtDll = Assert-LocalReleaseArtifact -RepoRoot $repoRoot -IsTestMode $false
        if (-not (Test-Path $builtDll)) {
            throw "Assert-LocalReleaseArtifact failed to produce DLL at $builtDll"
        }

        # B. In test mode (IsTestMode = true), fast path succeeds without error
        $testDll = Assert-LocalReleaseArtifact -RepoRoot $repoRoot -IsTestMode $true
        if ($testDll -ne $builtDll) {
            throw "Test mode artifact mismatch"
        }
    }

    # --- TEST 10: ManualSyncRemote Inheritance & Safety Invariants (Comment #5801971999) ---
    Assert-Test "ManualSyncRemote connection string inheritance from User/Process scope when present without disclosure" {
        $fakeRemote2026 = "Server=127.0.0.1;Database=TestRemote2026;Integrated Security=True;"
        $fakeRemote2027 = "Server=127.0.0.1;Database=TestRemote2027;Integrated Security=True;"

        # Save existing process/user values if any
        $origProc2026 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", "Process")
        $origProc2027 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", "Process")

        try {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $fakeRemote2026, "Process")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $fakeRemote2027, "Process")

            $envMap = Get-LocalFirstChildEnvironment -Port 5000 `
                -Local2026ConnStr $fixtureConn2026 `
                -Local2027ConnStr $fixtureConn2027 `
                -TokenKey "test_token_key_64_characters_long_for_hmac_sha256_validation_12345678" `
                -IsTestMode $true

            if ($envMap["ConnectionStrings__ManualSyncRemote2026"] -ne $fakeRemote2026) {
                throw "ManualSyncRemote2026 was not properly inherited into child environment!"
            }
            if ($envMap["ConnectionStrings__ManualSyncRemote2027"] -ne $fakeRemote2027) {
                throw "ManualSyncRemote2027 was not properly inherited into child environment!"
            }

            # Invariant: Tripwires must remain strictly untouched
            if ($envMap["ConnectionStrings__DefaultConnection"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
                throw "Tripwire DefaultConnection was corrupted!"
            }
            if ($envMap["ConnectionStrings__CON2027"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
                throw "Tripwire CON2027 was corrupted!"
            }
            if ($envMap["Sync__PullEnabled"] -ne "false" -or $envMap["Sync__PushEnabled"] -ne "false") {
                throw "Sync pull/push flags must remain false!"
            }
        } finally {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $origProc2026, "Process")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $origProc2027, "Process")
        }
    }

    # --- TEST 11: ManualSyncRemote Absence Valid Startup & Fail-Closed Omission ---
    Assert-Test "ManualSyncRemote absence remains valid for LocalFirst startup and omits manual remote keys" {
        $origUser2026 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", "User")
        $origUser2027 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", "User")
        $origProc2026 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", "Process")
        $origProc2027 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", "Process")

        try {
            # Temporarily clear both User and Process scopes
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $null, "User")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $null, "User")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $null, "Process")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $null, "Process")

            $envMap = Get-LocalFirstChildEnvironment -Port 5000 `
                -Local2026ConnStr $fixtureConn2026 `
                -Local2027ConnStr $fixtureConn2027 `
                -TokenKey "test_token_key_64_characters_long_for_hmac_sha256_validation_12345678" `
                -IsTestMode $true

            if ($envMap.ContainsKey("ConnectionStrings__ManualSyncRemote2026")) {
                throw "ManualSyncRemote2026 must be absent from child environment when not configured!"
            }
            if ($envMap.ContainsKey("ConnectionStrings__ManualSyncRemote2027")) {
                throw "ManualSyncRemote2027 must be absent from child environment when not configured!"
            }

            # Invariant: Basic LocalFirst settings and tripwires remain valid
            if ($envMap["LocalFirst__Enabled"] -ne "true") { throw "LocalFirst__Enabled must be true" }
            if ($envMap["ConnectionStrings__DefaultConnection"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
                throw "Tripwire DefaultConnection must reference DISABLED_REMOTE_TRIPWIRE"
            }
        } finally {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $origUser2026, "User")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $origUser2027, "User")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $origProc2026, "Process")
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2027", $origProc2027, "Process")
        }
    }

    # --- TEST 12: Start and Restart Lifecycle Rehydration & Zero Secret Leakage ---
    Assert-Test "Start and Restart lifecycle rehydrates manual sync remote keys with parity and zero secret leakage into state/logs" {
        $secretTestMarker = "SECRET_TOKEN_DO_NOT_LEAK_TO_DISK_OR_LOGS_987654321"
        $fakeRemoteSecret = "Server=127.0.0.1;Database=TestRemoteLeakCheck;Password=$secretTestMarker;"

        $testPort = 5195
        $testStateFile = [System.IO.Path]::GetTempFileName()
        Remove-Item $testStateFile -Force -ErrorAction SilentlyContinue

        $origProc2026 = [Environment]::GetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", "Process")
        try {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $fakeRemoteSecret, "Process")

            # 1. Start runtime on isolated port
            $startRes = & $launcherScript Start `
                -Port $testPort `
                -AllowIsolatedTestMode `
                -SkipGitVerification `
                -OverrideLocal2026ConnStr $fixtureConn2026 `
                -OverrideLocal2027ConnStr $fixtureConn2027 `
                -OverrideStateFilePath $testStateFile

            if ($startRes.Status -ne "RUNNING") {
                throw "Start failed: status=$($startRes.Status)"
            }

            # Verify /health responded
            $h1 = Invoke-WebRequest -Uri "http://127.0.0.1:$testPort/health" -Method Get -TimeoutSec 5 -UseBasicParsing
            if ($h1.StatusCode -ne 200) { throw "Initial health check failed: $($h1.StatusCode)" }

            # 2. Restart runtime on isolated port (testing Start/Restart parity)
            & $launcherScript Restart `
                -Port $testPort `
                -AllowIsolatedTestMode `
                -SkipGitVerification `
                -OverrideLocal2026ConnStr $fixtureConn2026 `
                -OverrideLocal2027ConnStr $fixtureConn2027 `
                -OverrideStateFilePath $testStateFile

            # Verify /health after restart
            $h2 = Invoke-WebRequest -Uri "http://127.0.0.1:$testPort/health" -Method Get -TimeoutSec 5 -UseBasicParsing
            if ($h2.StatusCode -ne 200) { throw "Post-restart health check failed: $($h2.StatusCode)" }

            # 3. Read state file and verify ZERO secret leakage
            if (-not (Test-Path $testStateFile)) { throw "State file not found at $testStateFile" }
            $stateContent = Get-Content $testStateFile -Raw
            if ($stateContent -match $secretTestMarker) {
                throw "SECURITY VIOLATION: Secret marker was leaked into structured state file!"
            }

            $stateObj = $stateContent | ConvertFrom-Json
            if ($stateObj.logPath -and (Test-Path $stateObj.logPath)) {
                $logContent = Get-Content $stateObj.logPath -Raw
                if ($logContent -match $secretTestMarker) {
                    throw "SECURITY VIOLATION: Secret marker was leaked into stdout runtime log!"
                }
            }
            if ($stateObj.errPath -and (Test-Path $stateObj.errPath)) {
                $errContent = Get-Content $stateObj.errPath -Raw
                if ($errContent -match $secretTestMarker) {
                    throw "SECURITY VIOLATION: Secret marker was leaked into stderr runtime log!"
                }
            }

            # 4. Stop runtime cleanly
            & $launcherScript Stop -Port $testPort -OverrideStateFilePath $testStateFile | Out-Null
        } finally {
            [Environment]::SetEnvironmentVariable("ConnectionStrings__ManualSyncRemote2026", $origProc2026, "Process")
            if (Test-Path $testStateFile) {
                $remState = Get-RuntimeState $testStateFile
                if ($remState -and $remState.pid) {
                    $p = Get-Process -Id ([int]$remState.pid) -ErrorAction SilentlyContinue
                    if ($p -and $p.ProcessName -ieq "dotnet") { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
                }
                Remove-Item $testStateFile -Force -ErrorAction SilentlyContinue
            }
        }
    }
} finally {
    # Complete cleanup of transient fixture databases (P0-6)
    Remove-TestFixtureDatabases
    Write-Host "Transient test fixture databases dropped cleanly." -ForegroundColor Gray
}

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ALL $passedTests / $totalTests LAUNCHER SUITE TESTS PASSED DETERMINISTICALLY! " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
