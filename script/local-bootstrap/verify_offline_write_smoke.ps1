# Slice 4.3B — Offline Write + Transactional Outbox Pilot Comprehensive Verification Script
# Validates offline read-write pilot execution across 4 distinct tiers:
# 1. SQL Compatibility (120) & Local Isolation Setup (IProgramLocalDb2026_SmokeTest & IProgramLocalDb2027_SmokeTest)
# 2. Mandatory Atomic Rollback Proof via Fault Injection (Trigger-induced failure -> zero business write, zero outbox row, full rollback)
# 3. Positive Outbox Integration Matrix for 2026 & 2027 (Add, Edit, Close, Unclose, SoftDelete -> deterministic Envelope V1)
# 4. Fail-Closed Scope Rejection Matrix (Form, Employee, Password, CopyDaily, Beneficiaries, PDF, ResetReviews -> 403 OFFLINE_WRITE_SCOPE_BLOCKED)

param(
    [string]$OutputJsonPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $OutputJsonPath) {
    $OutputJsonPath = Join-Path $repoRoot "docs\audit\sync-slice-4-3b\offline_write_smoke_report.json"
}

$auditDir = Split-Path $OutputJsonPath -Parent
if (-not (Test-Path $auditDir)) {
    New-Item -ItemType Directory -Path $auditDir -Force | Out-Null
}

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.3B: OFFLINE WRITE + TRANSACTIONAL OUTBOX PILOT VERIFICATION    " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

Add-Type -AssemblyName 'System.Data'

# Securely load credentials from environment or tests/e2e/.env
$e2eUsername = $env:E2E_USERNAME
$e2ePassword = $env:E2E_PASSWORD
if (-not $e2eUsername -or -not $e2ePassword) {
    $envFile = Join-Path $repoRoot "tests\e2e\.env"
    if (Test-Path $envFile) {
        foreach ($line in (Get-Content $envFile)) {
            $trimmed = $line.Trim()
            if ($trimmed -and -not $trimmed.StartsWith('#') -and $trimmed.Contains('=')) {
                $parts = $trimmed.Split('=', 2)
                $k = $parts[0].Trim()
                $v = $parts[1].Trim().Trim('"').Trim("'")
                if ($k -eq "E2E_USERNAME" -and -not $e2eUsername) { $e2eUsername = $v }
                if ($k -eq "E2E_PASSWORD" -and -not $e2ePassword) { $e2ePassword = $v }
            }
        }
    }
}

if (-not $e2eUsername -or -not $e2ePassword) {
    throw "Security requirement: E2E_USERNAME and E2E_PASSWORD must be configured. Aborting verification."
}

function Execute-LocalScalar([string]$database, [string]$query) {
    $connStr = "Server=localhost;Database=$database;Integrated Security=True;TrustServerCertificate=True;"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $query
        return $cmd.ExecuteScalar()
    } finally {
        $conn.Close()
        $conn.Dispose()
    }
}

function Execute-LocalNonQuery([string]$database, [string]$query) {
    $connStr = "Server=localhost;Database=$database;Integrated Security=True;TrustServerCertificate=True;"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $query
        return $cmd.ExecuteNonQuery()
    } finally {
        $conn.Close()
        $conn.Dispose()
    }
}

function Execute-LocalRow([string]$database, [string]$query) {
    $connStr = "Server=localhost;Database=$database;Integrated Security=True;TrustServerCertificate=True;"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)
    $conn.Open()
    try {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = $query
        $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
        $dt = New-Object System.Data.DataTable
        $null = $adapter.Fill($dt)
        if ($dt.Rows.Count -gt 0) {
            return $dt.Rows[0]
        }
        return $null
    } finally {
        $conn.Close()
        $conn.Dispose()
    }
}

function Verify-OutboxRecord([string]$database, [string]$commandName, [string]$syncId, [string]$expectedYear, [string]$expectedOp, [string]$expectedDeviceId, [long]$expectedBaseServerVersion) {
    $row = Execute-LocalRow $database "SELECT TOP (1) Status, RetryCount, DatabaseId, EntitySyncId, PayloadJson FROM [sync].[LocalOutbox] WHERE CommandName = '$commandName' AND EntitySyncId = '$syncId' ORDER BY CreatedAtUtc DESC;"
    if (-not $row) {
        throw "Verification failure: No outbox record in $database for CommandName '$commandName' and EntitySyncId '$syncId'!"
    }
    if ($row["Status"] -ne "PENDING") {
        throw "Outbox Status expected 'PENDING', got '$($row["Status"])'"
    }
    if ([int]$row["RetryCount"] -ne 0) {
        throw "Outbox RetryCount expected 0, got $($row["RetryCount"])"
    }
    if ($row["DatabaseId"] -ne $expectedYear) {
        throw "Outbox DatabaseId expected '$expectedYear', got '$($row["DatabaseId"])'"
    }
    if ($row["EntitySyncId"].ToString().ToLowerInvariant() -ne $syncId.ToLowerInvariant()) {
        throw "Outbox EntitySyncId mismatch: expected '$syncId', got '$($row["EntitySyncId"])'"
    }

    $env = $row["PayloadJson"] | ConvertFrom-Json
    if ([int]$env.schemaVersion -ne 1) {
        throw "PayloadJson schemaVersion expected 1, got $($env.schemaVersion)"
    }
    if ($env.operationType -ne $expectedOp) {
        throw "PayloadJson operationType expected '$expectedOp', got '$($env.operationType)'"
    }
    if ($env.databaseId -ne $expectedYear) {
        throw "PayloadJson databaseId expected '$expectedYear', got '$($env.databaseId)'"
    }
    if ($env.deviceId -ne $expectedDeviceId) {
        throw "PayloadJson deviceId expected '$expectedDeviceId', got '$($env.deviceId)'"
    }
    if ([long]$env.baseServerVersion -ne $expectedBaseServerVersion) {
        throw "PayloadJson baseServerVersion expected $expectedBaseServerVersion, got $($env.baseServerVersion)"
    }
    if ($env.entityType -ne "Daily") {
        throw "PayloadJson entityType expected 'Daily', got '$($env.entityType)'"
    }
    if ($env.entitySyncId.ToString().ToLowerInvariant() -ne $syncId.ToLowerInvariant()) {
        throw "PayloadJson entitySyncId expected '$syncId', got '$($env.entitySyncId)'"
    }
    return $env
}

$allPassed = $true
$report = [ordered]@{
    Slice = "4.3B"
    Title = "Offline Write + Transactional Outbox Pilot Smoke Verification"
    TimestampUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    Engine_Compatibility = [ordered]@{}
    Atomic_Rollback_Proof = [ordered]@{}
    Positive_Integration_Matrix = [ordered]@{}
    FailClosed_Scope_Rejection = [ordered]@{}
    Zero_Azure_Connections_Proof = [ordered]@{}
    Overall_Status = "PASS"
}

# --- [TIER 1] SQL Engine Compatibility & Isolated Database Setup ---
Write-Host "`n--- [TIER 1] SQL Engine Compatibility & Isolated Database Setup ---" -ForegroundColor Yellow

try {
    Write-Host "Test 1.1: Compatibility Level Verification (Target: 120 / SQL Server 2014)..." -NoNewline
    $compat2026 = [int](Execute-LocalScalar "master" "SELECT compatibility_level FROM sys.databases WHERE name = 'IProgramLocalDb2026';")
    $compat2027 = [int](Execute-LocalScalar "master" "SELECT compatibility_level FROM sys.databases WHERE name = 'IProgramLocalDb2027';")

    if ($compat2026 -ne 120 -or $compat2027 -ne 120) {
        throw "Compatibility level mismatch: 2026=$compat2026, 2027=$compat2027. Expected 120."
    }
    $report.Engine_Compatibility["2026_CompatLevel"] = $compat2026
    $report.Engine_Compatibility["2027_CompatLevel"] = $compat2027
    Write-Host " PASS (2026: 120, 2027: 120)" -ForegroundColor Green
} catch {
    $allPassed = $false
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
    throw
}

# Setup Isolated Test Databases to ensure zero mutation touches operational databases
Write-Host "Setting up isolated test databases for write pilot testing..." -NoNewline
$tempDir = "C:\temp"
if (-not (Test-Path $tempDir)) {
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
}
$bak2026 = Join-Path $tempDir "db2026_smoke_write.bak"
$mdf2026 = Join-Path $tempDir "db2026_smoke_write.mdf"
$ldf2026 = Join-Path $tempDir "db2026_smoke_write.ldf"

$bak2027 = Join-Path $tempDir "db2027_smoke_write.bak"
$mdf2027 = Join-Path $tempDir "db2027_smoke_write.mdf"
$ldf2027 = Join-Path $tempDir "db2027_smoke_write.ldf"

try {
    Execute-LocalNonQuery "master" "BACKUP DATABASE IProgramLocalDb2026 TO DISK = '$bak2026' WITH INIT;"
    Execute-LocalNonQuery "master" "IF DB_ID('IProgramLocalDb2026_SmokeTest') IS NOT NULL BEGIN ALTER DATABASE IProgramLocalDb2026_SmokeTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IProgramLocalDb2026_SmokeTest; END; RESTORE DATABASE IProgramLocalDb2026_SmokeTest FROM DISK = '$bak2026' WITH MOVE 'IProgramLocalDb2026_Bootstrap_20260919_214709' TO '$mdf2026', MOVE 'IProgramLocalDb2026_Bootstrap_20260919_214709_log' TO '$ldf2026';"

    Execute-LocalNonQuery "master" "BACKUP DATABASE IProgramLocalDb2027 TO DISK = '$bak2027' WITH INIT;"
    Execute-LocalNonQuery "master" "IF DB_ID('IProgramLocalDb2027_SmokeTest') IS NOT NULL BEGIN ALTER DATABASE IProgramLocalDb2027_SmokeTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IProgramLocalDb2027_SmokeTest; END; RESTORE DATABASE IProgramLocalDb2027_SmokeTest FROM DISK = '$bak2027' WITH MOVE 'IProgramLocalDb2027' TO '$mdf2027', MOVE 'IProgramLocalDb2027_log' TO '$ldf2027';"
    Write-Host " PASS (IProgramLocalDb2026_SmokeTest & IProgramLocalDb2027_SmokeTest created)" -ForegroundColor Green
} catch {
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
    throw "Failed to create isolated test databases: $($_.Exception.Message)"
}

# --- Launch Test API Server (Port 5097) in OfflineReadWritePilot mode ---
$testPort = 5097
$testBaseUrl = "http://127.0.0.1:$testPort"
$apiProcess = $null

$origEnv = @{
    ASPNETCORE_ENVIRONMENT = $env:ASPNETCORE_ENVIRONMENT
    LocalFirst__ReadOnlyMode = $env:LocalFirst__ReadOnlyMode
    LocalFirst__Enabled = $env:LocalFirst__Enabled
    E2E__DiagnosticsEnabled = $env:E2E__DiagnosticsEnabled
    ConnectionStrings__DefaultConnection = $env:ConnectionStrings__DefaultConnection
    ConnectionStrings__CON2027 = $env:ConnectionStrings__CON2027
    ConnectionStrings__LocalConnection2026 = $env:ConnectionStrings__LocalConnection2026
    ConnectionStrings__LocalConnection2027 = $env:ConnectionStrings__LocalConnection2027
    ASPNETCORE_URLS = $env:ASPNETCORE_URLS
}

try {
    $apiDir = Join-Path $repoRoot "src\Api"
    $apiDll = Join-Path $apiDir "bin\Debug\net10.0\Auth.Api.dll"

    $logFile = Join-Path $auditDir "test_api_server.log"
    $errFile = Join-Path $auditDir "test_api_server.err.log"

    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:LocalFirst__ReadOnlyMode = "false"
    $env:LocalFirst__Enabled = "true"
    $env:E2E__DiagnosticsEnabled = "true"
    $env:ConnectionStrings__LocalConnection2026 = "Server=localhost;Database=IProgramLocalDb2026_SmokeTest;Trusted_Connection=True;TrustServerCertificate=True;"
    $env:ConnectionStrings__LocalConnection2027 = "Server=localhost;Database=IProgramLocalDb2027_SmokeTest;Trusted_Connection=True;TrustServerCertificate=True;"
    # Blackhole remote Azure endpoints
    $env:ConnectionStrings__DefaultConnection = "Server=127.0.0.1,9999;Database=IProgramDb2026;Connection Timeout=1;"
    $env:ConnectionStrings__CON2027 = "Server=127.0.0.1,9999;Database=IProgramDb2027;Connection Timeout=1;"
    $env:ASPNETCORE_URLS = $testBaseUrl

    Write-Host "Starting isolated Auth.Api test server on $testBaseUrl..." -NoNewline
    $apiProcess = Start-Process -FilePath "dotnet" -ArgumentList $apiDll -WorkingDirectory $apiDir -PassThru -NoNewWindow -RedirectStandardOutput $logFile -RedirectStandardError $errFile

    $serverReady = $false
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt 30000 -and -not $serverReady) {
        Start-Sleep -Milliseconds 500
        try {
            $st = Invoke-RestMethod -Uri "$testBaseUrl/api/account/runtime-status" -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
            if ($st -and $st.runtimeMode -eq "OfflineReadWritePilot") {
                $serverReady = $true
            }
        } catch {}
    }

    if (-not $serverReady) {
        throw "Isolated Auth.Api process failed to become ready at $testBaseUrl within 30 seconds."
    }
    Write-Host " PASS (Online at $testBaseUrl, runtimeMode: OfflineReadWritePilot)" -ForegroundColor Green

    # Test-safe initialization: clear connection audit records before test run
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/diagnostics/connection-audit/clear" -Method Post -TimeoutSec 5

    # Authenticate for Year 2026 & Year 2027
    $loginBody2026 = @{ username = $e2eUsername; password = $e2ePassword } | ConvertTo-Json
    $loginResp2026 = Invoke-RestMethod -Uri "$testBaseUrl/api/account/login" -Method Post -Body $loginBody2026 -ContentType "application/json" -Headers @{ "X-Db-Selection" = "2026" } -TimeoutSec 5
    $token2026 = $loginResp2026.token
    if (-not $token2026) { throw "Year 2026 login failed to return token." }
    $headers2026 = @{ "Authorization" = "Bearer $token2026"; "X-Db-Selection" = "2026" }

    $loginBody2027 = @{ username = $e2eUsername; password = $e2ePassword } | ConvertTo-Json
    $loginResp2027 = Invoke-RestMethod -Uri "$testBaseUrl/api/account/login" -Method Post -Body $loginBody2027 -ContentType "application/json" -Headers @{ "X-Db-Selection" = "2027" } -TimeoutSec 5
    $token2027 = $loginResp2027.token
    if (-not $token2027) { throw "Year 2027 login failed to return token." }
    $headers2027 = @{ "Authorization" = "Bearer $token2027"; "X-Db-Selection" = "2027" }

    # --- [TIER 2] Mandatory Atomic Rollback Proof via Fault Injection ---
    Write-Host "`n--- [TIER 2] Mandatory Atomic Rollback Proof via Fault Injection ---" -ForegroundColor Yellow

    Write-Host "Test 2.1: Fault Injection Atomic Rollback Proof..." -NoNewline
    $dailyCountBefore = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [dbo].[Daily];")
    $outboxCountBefore = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [sync].[LocalOutbox];")

    # Install test trigger causing failure on sync.LocalOutbox insertion
    Execute-LocalNonQuery "IProgramLocalDb2026_SmokeTest" @"
CREATE TRIGGER [sync].[TR_FaultInjection_FailInsert]
ON [sync].[LocalOutbox]
INSTEAD OF INSERT
AS
BEGIN
    RAISERROR('Simulated Outbox Failure for Rollback Test', 16, 1);
END;
"@

    $faultOpFailed = $false
    $faultStatusCode = 0
    try {
        $faultBody = @{
            name = "Fault Injection Test Daily"
            dailyDate = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
        } | ConvertTo-Json

        $resp = Invoke-WebRequest -Uri "$testBaseUrl/api/Daily" -Method Post -Body $faultBody -ContentType "application/json" -Headers $headers2026 -TimeoutSec 10 -UseBasicParsing
        throw "Expected API call to fail due to outbox fault trigger, but received HTTP $($resp.StatusCode)"
    } catch {
        $faultOpFailed = $true
        if ($_.Exception.Response) {
            $faultStatusCode = [int]$_.Exception.Response.StatusCode
        }
    } finally {
        # Drop fault trigger unconditionally
        Execute-LocalNonQuery "IProgramLocalDb2026_SmokeTest" "IF OBJECT_ID('[sync].[TR_FaultInjection_FailInsert]', 'TR') IS NOT NULL DROP TRIGGER [sync].[TR_FaultInjection_FailInsert];"
    }

    $dailyCountAfter = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [dbo].[Daily];")
    $outboxCountAfter = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [sync].[LocalOutbox];")
    $faultDailyExists = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [Name] = 'Fault Injection Test Daily';")

    if (-not $faultOpFailed -or $faultStatusCode -ne 500) {
        throw "Atomic rollback verification failed: API call must fail specifically with HTTP 500 (failed: $faultOpFailed, status: $faultStatusCode)."
    }
    if ($dailyCountAfter -ne $dailyCountBefore -or $faultDailyExists -ne 0) {
        throw "Atomic rollback verification failed: Business write was committed despite outbox insertion failure! (Count before: $dailyCountBefore, after: $dailyCountAfter)"
    }
    if ($outboxCountAfter -ne $outboxCountBefore) {
        throw "Atomic rollback verification failed: Outbox count changed despite insertion failure!"
    }

    $report.Atomic_Rollback_Proof = [ordered]@{
        Status = "PASS"
        ApiCallFailedAsExpected = $true
        HttpStatusReturned = $faultStatusCode
        BusinessRowCountUnchanged = ($dailyCountAfter -eq $dailyCountBefore)
        OutboxRowCountUnchanged = ($outboxCountAfter -eq $outboxCountBefore)
        RollbackConfirmed = $true
    }
    Write-Host " PASS (API Failed 500 as expected, Daily rows before: $dailyCountBefore == after: $dailyCountAfter, Outbox rows: $outboxCountBefore == $outboxCountAfter. Full rollback confirmed!)" -ForegroundColor Green

    # --- [TIER 3] Positive Integration Matrix (2026 & 2027) ---
    Write-Host "`n--- [TIER 3] Positive Integration Matrix (2026 & 2027) ---" -ForegroundColor Yellow

    # Retrieve LocalState metadata for Year 2026 & Year 2027
    $localState2026 = Execute-LocalRow "IProgramLocalDb2026_SmokeTest" "SELECT DeviceId, LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';"
    if (-not $localState2026) { throw "Missing [sync].[LocalState] record for DatabaseId 2026" }
    $devId2026 = $localState2026["DeviceId"].ToString()
    $baseVer2026 = [long]$localState2026["LastServerVersion"]

    $localState2027 = Execute-LocalRow "IProgramLocalDb2027_SmokeTest" "SELECT DeviceId, LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2027';"
    if (-not $localState2027) { throw "Missing [sync].[LocalState] record for DatabaseId 2027" }
    $devId2027 = $localState2027["DeviceId"].ToString()
    $baseVer2027 = [long]$localState2027["LastServerVersion"]

    # Test 3.1: Year 2026 Operations
    Write-Host "Test 3.1: Year 2026 Full Operation Lifecycle (Add, Edit, Close, Unclose, SoftDelete)..." -NoNewline
    $addBody2026 = @{
        name = "Daily_2026_Smoke_Test_Slice43B"
        dailyDate = "2026-09-20T00:00:00"
    } | ConvertTo-Json

    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily" -Method Post -Body $addBody2026 -ContentType "application/json" -Headers $headers2026 -TimeoutSec 5
    $createdDailyId = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT TOP (1) Id FROM [dbo].[Daily] WHERE Name = 'Daily_2026_Smoke_Test_Slice43B' ORDER BY Id DESC;")
    if ($createdDailyId -le 0) { throw "Year 2026 Add Daily: Failed to retrieve created Daily ID from database." }

    $dailyRow2026 = (Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT SyncId FROM [dbo].[Daily] WHERE Id = $createdDailyId;").ToString()
    $addEnv2026 = Verify-OutboxRecord "IProgramLocalDb2026_SmokeTest" "Daily.Insert" $dailyRow2026 "2026" "INSERT" $devId2026 $baseVer2026

    # Edit Daily
    $editBody2026 = @{
        id = $createdDailyId
        name = "Daily_2026_Smoke_Test_Updated"
        dailyDate = "2026-09-20T00:00:00"
    } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily" -Method Put -Body $editBody2026 -ContentType "application/json" -Headers $headers2026 -TimeoutSec 5

    $editEnv2026 = Verify-OutboxRecord "IProgramLocalDb2026_SmokeTest" "Daily.Update" $dailyRow2026 "2026" "UPDATE" $devId2026 $baseVer2026

    # Close Daily
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily/CloseDaily/$createdDailyId" -Method Put -Headers $headers2026 -TimeoutSec 5
    $closedStatus = [bool](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT Closed FROM [dbo].[Daily] WHERE Id = $createdDailyId;")
    if (-not $closedStatus) { throw "Year 2026 CloseDaily failed to set Closed = 1 in SQL." }

    $updateCountAfterClose = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE EntitySyncId = '$dailyRow2026' AND CommandName = 'Daily.Update';")
    if ($updateCountAfterClose -lt 2) {
        throw "Year 2026 CloseDaily failed to create distinct outbox record (expected >= 2, got $updateCountAfterClose)."
    }
    $closeEnv2026 = Verify-OutboxRecord "IProgramLocalDb2026_SmokeTest" "Daily.Update" $dailyRow2026 "2026" "UPDATE" $devId2026 $baseVer2026
    if (-not $closeEnv2026.entityData.Closed) {
        throw "Year 2026 CloseDaily outbox payload does not reflect Closed = true."
    }

    # Unclose Daily
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily/UncloseDaily/$createdDailyId" -Method Put -Headers $headers2026 -TimeoutSec 5
    $unclosedStatus = [bool](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT Closed FROM [dbo].[Daily] WHERE Id = $createdDailyId;")
    if ($unclosedStatus) { throw "Year 2026 UncloseDaily failed to set Closed = 0 in SQL." }

    $updateCountAfterUnclose = [int](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE EntitySyncId = '$dailyRow2026' AND CommandName = 'Daily.Update';")
    if ($updateCountAfterUnclose -lt 3) {
        throw "Year 2026 UncloseDaily failed to create distinct outbox record (expected >= 3, got $updateCountAfterUnclose)."
    }
    $uncloseEnv2026 = Verify-OutboxRecord "IProgramLocalDb2026_SmokeTest" "Daily.Update" $dailyRow2026 "2026" "UPDATE" $devId2026 $baseVer2026
    if ($uncloseEnv2026.entityData.Closed) {
        throw "Year 2026 UncloseDaily outbox payload does not reflect Closed = false."
    }

    # Soft Delete Daily
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily/softdelete/$createdDailyId" -Method Delete -Headers $headers2026 -TimeoutSec 5
    $isActiveStatus = [bool](Execute-LocalScalar "IProgramLocalDb2026_SmokeTest" "SELECT IsActive FROM [dbo].[Daily] WHERE Id = $createdDailyId;")
    if ($isActiveStatus) { throw "Year 2026 SoftDelete failed to set IsActive = 0 in SQL." }

    $deleteEnv2026 = Verify-OutboxRecord "IProgramLocalDb2026_SmokeTest" "Daily.SoftDelete" $dailyRow2026 "2026" "SOFT_DELETE" $devId2026 $baseVer2026

    $report.Positive_Integration_Matrix["Year_2026"] = [ordered]@{
        Status = "PASS"
        DailyId = $createdDailyId
        EntitySyncId = $dailyRow2026
        DeviceId = $devId2026
        BaseServerVersion = $baseVer2026
        AddOutboxCreated = $true
        EditOutboxCreated = $true
        CloseOutboxCreated = $true
        UncloseOutboxCreated = $true
        SoftDeleteOutboxCreated = $true
        AllOutboxStatusesPending = $true
        AllRetryCountsZero = $true
    }
    Write-Host " PASS (Add -> INSERT, Edit -> UPDATE, Close -> UPDATE [Closed=1], Unclose -> UPDATE [Closed=0], SoftDelete -> SOFT_DELETE)" -ForegroundColor Green

    # Test 3.2: Year 2027 Operations (Add, Edit, SoftDelete)
    Write-Host "Test 3.2: Year 2027 Lifecycle (Add, Edit, SoftDelete)..." -NoNewline
    $addBody2027 = @{
        name = "Daily_2027_Smoke_Test_Slice43B"
        dailyDate = "2027-01-15T00:00:00"
    } | ConvertTo-Json

    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily" -Method Post -Body $addBody2027 -ContentType "application/json" -Headers $headers2027 -TimeoutSec 5
    $createdDailyId2027 = [int](Execute-LocalScalar "IProgramLocalDb2027_SmokeTest" "SELECT TOP (1) Id FROM [dbo].[Daily] WHERE Name = 'Daily_2027_Smoke_Test_Slice43B' ORDER BY Id DESC;")
    if ($createdDailyId2027 -le 0) { throw "Year 2027 Add Daily: Failed to retrieve created Daily ID from database." }

    $dailyRow2027 = (Execute-LocalScalar "IProgramLocalDb2027_SmokeTest" "SELECT SyncId FROM [dbo].[Daily] WHERE Id = $createdDailyId2027;").ToString()
    $addEnv2027 = Verify-OutboxRecord "IProgramLocalDb2027_SmokeTest" "Daily.Insert" $dailyRow2027 "2027" "INSERT" $devId2027 $baseVer2027

    # Edit Daily for Year 2027
    $editBody2027 = @{
        id = $createdDailyId2027
        name = "Daily_2027_Smoke_Test_Updated"
        dailyDate = "2027-01-15T00:00:00"
    } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily" -Method Put -Body $editBody2027 -ContentType "application/json" -Headers $headers2027 -TimeoutSec 5

    $nameInDb2027 = Execute-LocalScalar "IProgramLocalDb2027_SmokeTest" "SELECT Name FROM [dbo].[Daily] WHERE Id = $createdDailyId2027;"
    if ($nameInDb2027 -ne "Daily_2027_Smoke_Test_Updated") {
        throw "Year 2027 Edit Daily failed to update Name in SQL (got '$nameInDb2027')."
    }

    $editEnv2027 = Verify-OutboxRecord "IProgramLocalDb2027_SmokeTest" "Daily.Update" $dailyRow2027 "2027" "UPDATE" $devId2027 $baseVer2027

    # Soft Delete Daily via DELETE /api/Daily/{id} route
    $null = Invoke-RestMethod -Uri "$testBaseUrl/api/Daily/$createdDailyId2027" -Method Delete -Headers $headers2027 -TimeoutSec 5
    $isActiveStatus2027 = [bool](Execute-LocalScalar "IProgramLocalDb2027_SmokeTest" "SELECT IsActive FROM [dbo].[Daily] WHERE Id = $createdDailyId2027;")
    if ($isActiveStatus2027) { throw "Year 2027 DELETE /api/Daily/{id} failed to perform soft delete in SQL." }

    $deleteEnv2027 = Verify-OutboxRecord "IProgramLocalDb2027_SmokeTest" "Daily.SoftDelete" $dailyRow2027 "2027" "SOFT_DELETE" $devId2027 $baseVer2027

    $report.Positive_Integration_Matrix["Year_2027"] = [ordered]@{
        Status = "PASS"
        DailyId = $createdDailyId2027
        EntitySyncId = $dailyRow2027
        DatabaseId = "2027"
        DeviceId = $devId2027
        BaseServerVersion = $baseVer2027
        AddOutboxCreated = $true
        EditOutboxCreated = $true
        SoftDeleteOutboxCreated = $true
        AllOutboxStatusesPending = $true
        AllRetryCountsZero = $true
    }
    Write-Host " PASS (Add -> INSERT, Edit -> UPDATE, DELETE -> SOFT_DELETE - All with DatabaseId=2027)" -ForegroundColor Green

    # --- [TIER 4] Fail-Closed Scope Rejection Matrix ---
    Write-Host "`n--- [TIER 4] Fail-Closed Scope Rejection Matrix ---" -ForegroundColor Yellow

    $rejectionCases = @(
        @{ Name = "Form_POST"; Method = "POST"; Path = "/api/Form"; Body = @{ name = "Blocked Form"; dailyId = $createdDailyId } },
        @{ Name = "Employee_POST"; Method = "POST"; Path = "/api/Employee"; Body = @{ name = "Blocked Employee" } },
        @{ Name = "Account_ChangePassword_PUT"; Method = "PUT"; Path = "/api/account/ChangePassword"; Body = @{ oldPassword = "x"; newPassword = "y" } },
        @{ Name = "Daily_Copy_POST"; Method = "POST"; Path = "/api/Daily/copy/$createdDailyId"; Body = @{} },
        @{ Name = "Beneficiary_Comment_PUT"; Method = "PUT"; Path = "/api/Daily/$createdDailyId/beneficiary-comment"; Body = @{ comment = "Blocked" } },
        @{ Name = "Beneficiary_NetPay_PUT"; Method = "PUT"; Path = "/api/Daily/$createdDailyId/beneficiary-netpay"; Body = @{ netPay = 100 } },
        @{ Name = "VerifyPdf_POST"; Method = "POST"; Path = "/api/Daily/$createdDailyId/verify-pdf"; Body = @{} },
        @{ Name = "ResetReviews_POST"; Method = "POST"; Path = "/api/Daily/$createdDailyId/reset-reviews"; Body = @{} }
    )

    foreach ($case in $rejectionCases) {
        Write-Host "Testing rejection of $($case.Name) ($($case.Method) $($case.Path))..." -NoNewline
        $rejected = $false
        $statusCode = 0
        $code = ""

        try {
            $bJson = $case.Body | ConvertTo-Json
            $resp = Invoke-WebRequest -Uri "$testBaseUrl$($case.Path)" -Method $case.Method -Body $bJson -ContentType "application/json" -Headers $headers2026 -TimeoutSec 5 -UseBasicParsing
            throw "Expected mutation to be blocked with 403, but received HTTP $($resp.StatusCode)"
        } catch {
            $ex = $_.Exception
            if ($ex.Response) {
                $statusCode = [int]$ex.Response.StatusCode
                if ($statusCode -eq 403) {
                    $rejected = $true
                    $rdr = New-Object System.IO.StreamReader($ex.Response.GetResponseStream())
                    try {
                        $rawBody = $rdr.ReadToEnd()
                        if ($rawBody.Contains("OFFLINE_WRITE_SCOPE_BLOCKED")) {
                            $code = "OFFLINE_WRITE_SCOPE_BLOCKED"
                        }
                    } finally {
                        $rdr.Dispose()
                    }
                }
            }
        }

        if (-not $rejected -or $code -ne "OFFLINE_WRITE_SCOPE_BLOCKED") {
            throw "Expected 403 Forbidden with OFFLINE_WRITE_SCOPE_BLOCKED for $($case.Name), got status $statusCode, code $code"
        }

        $report.FailClosed_Scope_Rejection[$case.Name] = [ordered]@{
            Status = "PASS"
            HttpMethod = $case.Method
            Path = $case.Path
            ResponseStatusCode = $statusCode
            ErrorCode = $code
            RejectedFailClosed = $true
        }
        Write-Host " PASS (403 Forbidden, code: OFFLINE_WRITE_SCOPE_BLOCKED)" -ForegroundColor Green
    }

    # --- [TIER 5] Connection Audit Verification (Zero Azure / Remote Connections) ---
    Write-Host "`n--- [TIER 5] Connection Audit Verification (Zero Azure / Remote Connections) ---" -ForegroundColor Yellow
    Write-Host "Test 5.1: Connection Audit Tracker Endpoint (/api/diagnostics/connection-audit)..." -NoNewline

    $connAudit = Invoke-RestMethod -Uri "$testBaseUrl/api/diagnostics/connection-audit" -Method Get -TimeoutSec 5
    if ($connAudit.fallbackAttempts -ne 0) {
        throw "Security violation: Recorded $($connAudit.fallbackAttempts) fallback connection attempts!"
    }
    if ($connAudit.disallowedRemoteConnections -ne 0) {
        throw "Security violation: Recorded $($connAudit.disallowedRemoteConnections) disallowed remote connections!"
    }
    if ($connAudit.allowedLocalConnections -le 0) {
        throw "Verification failure: No allowed local connections recorded ($($connAudit.allowedLocalConnections))."
    }

    $nonLocalRecords = $connAudit.records | Where-Object { -not $_.isLocal -or $_.isFallbackEndpoint -or -not $_.allowed }
    if ($nonLocalRecords -and $nonLocalRecords.Count -gt 0) {
        throw "Security violation: Detected non-local connection records in audit: $($nonLocalRecords | ConvertTo-Json)"
    }

    $report.Zero_Azure_Connections_Proof = [ordered]@{
        Status = "PASS"
        FallbackAttempts = [int]$connAudit.fallbackAttempts
        DisallowedRemoteConnections = [int]$connAudit.disallowedRemoteConnections
        AllowedLocalConnections = [int]$connAudit.allowedLocalConnections
        TotalConnectionsAudited = [int]$connAudit.totalConnections
        AllConnectionsLocalOnly = $true
        ZeroAzureWritesVerified = $true
    }
    Write-Host " PASS (Allowed Local: $($connAudit.allowedLocalConnections), Fallback: 0, Disallowed Remote: 0)" -ForegroundColor Green

} catch {
    $allPassed = $false
    $report.Overall_Status = "FAIL"
    $report["Error"] = $_.Exception.Message
    Write-Host "`nCRITICAL VERIFICATION FAILURE: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
    if ($origEnv) {
        foreach ($k in $origEnv.Keys) {
            if ($origEnv[$k] -eq $null) { Remove-Item "Env:\$k" -ErrorAction SilentlyContinue } else { Set-Item "Env:\$k" $origEnv[$k] }
        }
    }

    # Clean up isolated test databases and disk backup artifacts
    Write-Host "`nCleaning up isolated test databases and backup artifacts..." -NoNewline
    try {
        Execute-LocalNonQuery "master" "IF DB_ID('IProgramLocalDb2026_SmokeTest') IS NOT NULL BEGIN ALTER DATABASE IProgramLocalDb2026_SmokeTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IProgramLocalDb2026_SmokeTest; END;"
        Execute-LocalNonQuery "master" "IF DB_ID('IProgramLocalDb2027_SmokeTest') IS NOT NULL BEGIN ALTER DATABASE IProgramLocalDb2027_SmokeTest SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE IProgramLocalDb2027_SmokeTest; END;"
        Remove-Item $bak2026, $mdf2026, $ldf2026, $bak2027, $mdf2027, $ldf2027 -Force -ErrorAction SilentlyContinue
        Write-Host " PASS (Isolated databases and temp files dropped)" -ForegroundColor Green
    } catch {
        Write-Host " WARNING: Failed to cleanly drop test databases: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# Export Audit JSON Report
$reportJson = $report | ConvertTo-Json -Depth 6
$reportJson | Set-Content -Path $OutputJsonPath -Encoding UTF8
Write-Host "`nSmoke verification report saved to: $OutputJsonPath" -ForegroundColor Cyan

if (-not $allPassed) {
    exit 1
}
Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  SLICE 4.3B VERIFICATION COMPLETE: ALL GATES & PROOFS PASSED (100%)       " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
