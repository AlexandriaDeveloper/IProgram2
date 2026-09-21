# ==============================================================================
# SLICE 4.5B-A: CONTROLLED PRODUCTION CATCH-UP OPERATOR & DRY-RUN
# Controlled, fail-closed, observable operator tool for Daily Pull catch-up
#
# Modes:
#   -DryRun  : Pure read-only preflight verification across Azure & Local.
#              Zero API calls, Zero state mutations, Zero business DML.
#   -Execute : Dedicated transient API execution calling POST /api/sync/pull.
#              (Locked against Azure Production in Slice 4.5B-A).
# ==============================================================================

using namespace System.Security.Cryptography
using namespace System.Text
using namespace System.Globalization
using namespace System.Data.SqlClient
using namespace System.Collections.Generic

[CmdletBinding()]
param (
    [switch]$DryRun,
    [switch]$Execute,
    [int]$Port = 5099,
    [string]$Username = $env:IPROGRAM_OPERATOR_USERNAME,
    [string]$Password = $env:IPROGRAM_OPERATOR_PASSWORD,
    [string]$Azure2026ConnectionString,
    [string]$Azure2027ConnectionString,
    [string]$Local2026ConnectionString,
    [string]$Local2027ConnectionString,
    [switch]$AllowIsolatedExecutionOnly # Permitted only for isolated test harness execution
)

$ErrorActionPreference = "Stop"

# --- 1. Mode Validation (Mutually Exclusive) ---
if (($DryRun -and $Execute) -or (-not $DryRun -and -not $Execute)) {
    throw "OPERATOR_MODE_ERROR: You must specify exactly one of -DryRun or -Execute."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
Add-Type -AssemblyName "System.Data"

# --- 2. Slice 4.5B-A Production Safety Guard ---
if ($Execute -and -not $AllowIsolatedExecutionOnly) {
    throw "SLICE_4_5B_A_PRODUCTION_GUARD: Executing actual catch-up against Azure Production is locked in Slice 4.5B-A. Run -DryRun on production, or run isolated tests with -AllowIsolatedExecutionOnly."
}

# --- 3. Committed Configuration Guard ---
function Assert-CommittedConfigurationGuard {
    param([string]$Root)
    
    $files = @(
        (Join-Path $Root "src\Api\appsettings.json"),
        (Join-Path $Root "src\Api\appsettings.Development.json")
    )
    
    foreach ($f in $files) {
        if (-not (Test-Path $f)) { continue }
        $json = Get-Content $f -Raw | ConvertFrom-Json
        
        $pull = if ($json.Sync -and $json.Sync.PullEnabled) { [bool]$json.Sync.PullEnabled } else { $false }
        $push = if ($json.Sync -and $json.Sync.PushEnabled) { [bool]$json.Sync.PushEnabled } else { $false }
        $track = if ($json.Sync -and $json.Sync.AuthoritativeTrackingEnabled) { [bool]$json.Sync.AuthoritativeTrackingEnabled } else { $false }
        $migration = if ($json.LegacyMigration -and $json.LegacyMigration.Enabled) { [bool]$json.LegacyMigration.Enabled } else { $false }
        $localFirst = if ($json.LocalFirst -and $json.LocalFirst.Enabled) { [bool]$json.LocalFirst.Enabled } else { $false }
        $isolatedTesting = if ($json.Sync -and $json.Sync.AllowIsolatedLocalRemoteForTesting) { [bool]$json.Sync.AllowIsolatedLocalRemoteForTesting } else { $false }
        
        if ($pull -or $push -or $track -or $migration -or $localFirst -or $isolatedTesting) {
            throw "COMMITTED_CONFIG_GUARD_VIOLATION: Committed configuration in '$f' has active flags (Pull=$pull, Push=$push, Track=$track, Migration=$migration, LocalFirst=$localFirst, IsolatedTesting=$isolatedTesting). All must be false."
        }
    }
}

Write-Host "Verifying committed configuration invariants..." -NoNewline
Assert-CommittedConfigurationGuard -Root $repoRoot
Write-Host " PASS (All committed flags are false)" -ForegroundColor Green

# --- 4. Resolve Connection Strings ---
if ([string]::IsNullOrWhiteSpace($Azure2026ConnectionString) -or [string]::IsNullOrWhiteSpace($Azure2027ConnectionString)) {
    $apiProj = Join-Path $repoRoot "src\Api\Auth.Api.csproj"
    if (Test-Path $apiProj) {
        $secrets = dotnet user-secrets list --project $apiProj 2>$null
        foreach ($line in $secrets) {
            if ($line.StartsWith("ConnectionStrings:DefaultConnection = ")) {
                if ([string]::IsNullOrWhiteSpace($Azure2026ConnectionString)) {
                    $Azure2026ConnectionString = $line.Substring("ConnectionStrings:DefaultConnection = ".Length).Trim()
                }
            }
            if ($line.StartsWith("ConnectionStrings:CON2027 = ")) {
                if ([string]::IsNullOrWhiteSpace($Azure2027ConnectionString)) {
                    $Azure2027ConnectionString = $line.Substring("ConnectionStrings:CON2027 = ".Length).Trim()
                }
            }
        }
    }
}

$appsettingsPath = Join-Path $repoRoot "src\Api\appsettings.json"
$appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json

if ([string]::IsNullOrWhiteSpace($Local2026ConnectionString)) {
    $Local2026ConnectionString = $appsettings.ConnectionStrings.LocalConnection2026
}
if ([string]::IsNullOrWhiteSpace($Local2027ConnectionString)) {
    $Local2027ConnectionString = $appsettings.ConnectionStrings.LocalConnection2027
}

# --- 5. Physical Database Binding Guard ---
function Assert-DatabaseBinding {
    param(
        [string]$ConnectionString,
        [string]$ExpectedTarget, # 'Azure' or 'Local'
        [string]$ExpectedYear,   # '2026' or '2027'
        [bool]$IsIsolatedMode = $false
    )
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "BINDING_ERROR: Connection string for $ExpectedTarget $ExpectedYear is null or empty."
    }

    $builder = New-Object SqlConnectionStringBuilder($ConnectionString)
    $dataSource = if ($builder.DataSource) { $builder.DataSource.Trim() } else { "" }
    $initialCatalog = if ($builder.InitialCatalog) { $builder.InitialCatalog.Trim() } else { "" }
    $serverHost = $dataSource -replace '^(?i)tcp:', '' -replace ',\s*[0-9]+$', ''

    if ($ExpectedTarget -eq "Azure") {
        if (-not $IsIsolatedMode) {
            $expectedCatalog = if ($ExpectedYear -eq "2026") { "IProgramDb2026" } else { "IProgramDb2027" }
            if ($initialCatalog -ne $expectedCatalog) {
                throw "BINDING_ERROR: Azure $ExpectedYear InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
            }
            $isAzure = $serverHost.ToLowerInvariant().EndsWith(".database.windows.net")
            if (-not $isAzure) {
                throw "BINDING_ERROR: Azure $ExpectedYear DataSource is not a trusted Azure SQL endpoint (*.database.windows.net)."
            }
        }
    } elseif ($ExpectedTarget -eq "Local") {
        if (-not $IsIsolatedMode) {
            $expectedCatalog = if ($ExpectedYear -eq "2026") { "IProgramLocalDb2026" } else { "IProgramLocalDb2027" }
            if ($initialCatalog -ne $expectedCatalog) {
                throw "BINDING_ERROR: Local $ExpectedYear InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
            }
        }
        if ($serverHost.ToLowerInvariant().Contains(".database.windows.net")) {
            throw "BINDING_ERROR: Local $ExpectedYear DataSource cannot point to an Azure endpoint."
        }
    }

    return @{
        Target = $ExpectedTarget
        Year = $ExpectedYear
        InitialCatalog = $initialCatalog
        Status = "PASS"
    }
}

# --- 6. Deterministic Daily Hash Calculation ---
function Calculate-DailyHashAndCounts($conn) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT 
    [SyncId],
    [Name],
    [DailyDate],
    [Closed],
    [CreatedAt],
    [CreatedBy],
    [UpdatedAt],
    [UpdatedBy],
    [DeactivatedAt],
    [DeactivatedBy],
    [IsActive]
FROM [dbo].[Daily]
ORDER BY [SyncId] ASC;
"@
    
    $sha = [SHA256]::Create()
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms, [Encoding]::UTF8)

    $reader = $cmd.ExecuteReader()
    $count = 0
    $activeCount = 0
    $inactiveCount = 0

    while ($reader.Read()) {
        $count++
        $syncId = $reader.GetGuid(0)
        $name = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
        $dailyDate = if ($reader.IsDBNull(2)) { $null } else { $reader.GetDateTime(2) }
        $closed = $reader.GetBoolean(3)
        $createdAt = if ($reader.IsDBNull(4)) { $null } else { $reader.GetDateTime(4) }
        $createdBy = if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) }
        $updatedAt = if ($reader.IsDBNull(6)) { $null } else { $reader.GetDateTime(6) }
        $updatedBy = if ($reader.IsDBNull(7)) { $null } else { $reader.GetString(7) }
        $deactivatedAt = if ($reader.IsDBNull(8)) { $null } else { $reader.GetDateTime(8) }
        $deactivatedBy = if ($reader.IsDBNull(9)) { $null } else { $reader.GetString(9) }
        $isActive = $reader.GetBoolean(10)

        if ($isActive) { $activeCount++ } else { $inactiveCount++ }

        $bw.Write([byte]0xFF)
        $bw.Write($syncId.ToByteArray())

        if ($name -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($name) }
        if ($dailyDate -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($dailyDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [CultureInfo]::InvariantCulture)) }
        $bw.Write($closed)
        if ($createdAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [CultureInfo]::InvariantCulture)) }
        if ($createdBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($createdBy) }
        if ($updatedAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($updatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [CultureInfo]::InvariantCulture)) }
        if ($updatedBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($updatedBy) }
        if ($deactivatedAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($deactivatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [CultureInfo]::InvariantCulture)) }
        if ($deactivatedBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($deactivatedBy) }
        $bw.Write($isActive)
    }
    $reader.Close()

    $bw.Flush()
    $bytes = $ms.ToArray()
    $hashBytes = $sha.ComputeHash($bytes)
    $hashStr = [BitConverter]::ToString($hashBytes).Replace("-", "")

    $bw.Dispose()
    $ms.Dispose()
    $sha.Dispose()

    return @{
        TotalRows = $count
        ActiveRows = $activeCount
        InactiveRows = $inactiveCount
        DeterministicSha256 = $hashStr
    }
}

# --- 7. Preflight Verification Function ---
function Invoke-PreflightVerification {
    param(
        [string]$Year,
        [string]$AzureConnStr,
        [string]$LocalConnStr,
        [bool]$IsIsolated = $false
    )

    Write-Host "`n--- Running Preflight Verification for Year $Year ---" -ForegroundColor Cyan
    Assert-DatabaseBinding -ConnectionString $AzureConnStr -ExpectedTarget "Azure" -ExpectedYear $Year -IsIsolatedMode $IsIsolated | Out-Null
    Assert-DatabaseBinding -ConnectionString $LocalConnStr -ExpectedTarget "Local" -ExpectedYear $Year -IsIsolatedMode $IsIsolated | Out-Null

    $azureConn = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            $azureConn = New-Object SqlConnection($AzureConnStr)
            $azureConn.Open()
            break
        } catch {
            if ($attempt -eq 3) { throw }
            Start-Sleep -Seconds 2
        }
    }
    $localConn = New-Object SqlConnection($LocalConnStr)
    $localConn.Open()

    try {
        # A. Remote Invariants
        $cmdAz = $azureConn.CreateCommand()
        $cmdAz.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;"
        $p = $cmdAz.CreateParameter(); $p.ParameterName = "@DatabaseId"; $p.Value = $Year; $cmdAz.Parameters.Add($p) | Out-Null
        $azVer = [int64]$cmdAz.ExecuteScalar()
        if ($azVer -ne 2) {
            throw "PREFLIGHT_FAIL: Azure ServerState.CurrentVersion for $Year is $azVer (Expected: 2)."
        }

        # Feed entries
        $cmdFeed = $azureConn.CreateCommand()
        $cmdFeed.CommandText = "SELECT ServerVersion, OperationType, EntityType, EntitySyncId, OriginDeviceId FROM [sync].[ServerChangeFeed] WHERE DatabaseId = @DatabaseId ORDER BY ServerVersion ASC;"
        $p2 = $cmdFeed.CreateParameter(); $p2.ParameterName = "@DatabaseId"; $p2.Value = $Year; $cmdFeed.Parameters.Add($p2) | Out-Null
        $rFeed = $cmdFeed.ExecuteReader()
        $feedList = New-Object List[psobject]
        while ($rFeed.Read()) {
            $feedList.Add([PSCustomObject]@{
                ServerVersion = [int64]$rFeed["ServerVersion"]
                OperationType = [string]$rFeed["OperationType"]
                EntityType = [string]$rFeed["EntityType"]
                EntitySyncId = [guid]$rFeed["EntitySyncId"]
                OriginDeviceId = [guid]$rFeed["OriginDeviceId"]
            })
        }
        $rFeed.Close()

        if ($feedList.Count -ne 2) {
            throw "PREFLIGHT_FAIL: Azure ChangeFeed count for $Year is $($feedList.Count) (Expected: 2)."
        }
        if ($feedList[0].ServerVersion -ne 1 -or $feedList[0].OperationType -ne "INSERT" -or $feedList[0].OriginDeviceId -ne [Guid]::Empty) {
            throw "PREFLIGHT_FAIL: Azure ChangeFeed v1 must be INSERT with OriginDeviceId=Guid.Empty."
        }
        if ($feedList[1].ServerVersion -ne 2 -or $feedList[1].OperationType -ne "HARD_DELETE" -or $feedList[1].OriginDeviceId -ne [Guid]::Empty) {
            throw "PREFLIGHT_FAIL: Azure ChangeFeed v2 must be HARD_DELETE with OriginDeviceId=Guid.Empty."
        }
        if ($feedList[0].EntitySyncId -ne $feedList[1].EntitySyncId) {
            throw "PREFLIGHT_FAIL: Azure ChangeFeed v1 and v2 EntitySyncId mismatch."
        }
        $canarySyncId = $feedList[0].EntitySyncId

        # Tombstone
        $cmdTomb = $azureConn.CreateCommand()
        $cmdTomb.CommandText = "SELECT ServerVersion, EntityType, EntitySyncId FROM [sync].[Tombstones] WHERE DatabaseId = @DatabaseId;"
        $p3 = $cmdTomb.CreateParameter(); $p3.ParameterName = "@DatabaseId"; $p3.Value = $Year; $cmdTomb.Parameters.Add($p3) | Out-Null
        $rTomb = $cmdTomb.ExecuteReader()
        $tombCount = 0
        $tombVer = 0
        $tombSyncId = [Guid]::Empty
        while ($rTomb.Read()) {
            $tombCount++
            $tombVer = [int64]$rTomb["ServerVersion"]
            $tombSyncId = [guid]$rTomb["EntitySyncId"]
        }
        $rTomb.Close()

        if ($tombCount -ne 1 -or $tombVer -ne 2 -or $tombSyncId -ne $canarySyncId) {
            throw "PREFLIGHT_FAIL: Azure Tombstones for $Year must have exactly 1 record with ServerVersion=2 matching canary SyncId."
        }

        # ProcessedOperations
        $cmdProc = $azureConn.CreateCommand()
        $cmdProc.CommandText = "SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE DatabaseId = @DatabaseId;"
        $p4 = $cmdProc.CreateParameter(); $p4.ParameterName = "@DatabaseId"; $p4.Value = $Year; $cmdProc.Parameters.Add($p4) | Out-Null
        $procCount = [int]$cmdProc.ExecuteScalar()
        if ($procCount -ne 0) {
            throw "PREFLIGHT_FAIL: Azure ProcessedOperations count for $Year is $procCount (Expected: 0)."
        }

        # Canary absent from Azure Daily
        $cmdCanaryCheck = $azureConn.CreateCommand()
        $cmdCanaryCheck.CommandText = "SELECT COUNT(*) FROM [dbo].[Daily] WHERE [SyncId] = @SyncId;"
        $p5 = $cmdCanaryCheck.CreateParameter(); $p5.ParameterName = "@SyncId"; $p5.Value = $canarySyncId; $cmdCanaryCheck.Parameters.Add($p5) | Out-Null
        $canaryDailyCount = [int]$cmdCanaryCheck.ExecuteScalar()
        if ($canaryDailyCount -ne 0) {
            throw "PREFLIGHT_FAIL: Canary row still exists in Azure Daily table ($canaryDailyCount rows found)."
        }

        # B. Local Invariants
        $cmdLocState = $localConn.CreateCommand()
        $cmdLocState.CommandText = "SELECT LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;"
        $p6 = $cmdLocState.CreateParameter(); $p6.ParameterName = "@DatabaseId"; $p6.Value = $Year; $cmdLocState.Parameters.Add($p6) | Out-Null
        $rLoc = $cmdLocState.ExecuteReader()
        if (-not $rLoc.Read()) {
            throw "PREFLIGHT_FAIL: LocalState row missing for DatabaseId '$Year'."
        }
        $lastVer = [int64]$rLoc["LastServerVersion"]
        $leaseToken = if ($rLoc.IsDBNull(1)) { $null } else { [string]$rLoc["ActiveLeaseToken"] }
        $leaseExpiry = if ($rLoc.IsDBNull(2)) { $null } else { [datetime]$rLoc["LeaseExpiresAtUtc"] }
        $rLoc.Close()

        if ($lastVer -ne 0) {
            throw "PREFLIGHT_FAIL: Local LastServerVersion for $Year is $lastVer (Expected: 0)."
        }
        if (-not [string]::IsNullOrWhiteSpace($leaseToken) -and $leaseExpiry -and $leaseExpiry -ge [DateTime]::UtcNow) {
            throw "PREFLIGHT_FAIL: LocalState for $Year has an active sync lease token."
        }

        # LocalOutbox blockers
        $cmdOutbox = $localConn.CreateCommand()
        $cmdOutbox.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE DatabaseId = @DatabaseId AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');"
        $p7 = $cmdOutbox.CreateParameter(); $p7.ParameterName = "@DatabaseId"; $p7.Value = $Year; $cmdOutbox.Parameters.Add($p7) | Out-Null
        $outboxBlockers = [int]$cmdOutbox.ExecuteScalar()
        if ($outboxBlockers -ne 0) {
            throw "PREFLIGHT_FAIL: LocalOutbox contains $outboxBlockers operational blockers (Expected: 0)."
        }

        # C. Business Data Parity
        $azDaily = Calculate-DailyHashAndCounts $azureConn
        $locDaily = Calculate-DailyHashAndCounts $localConn

        $expectedDailyRows = if ($Year -eq "2026") { 30 } else { 14 }
        if (-not $IsIsolated) {
            if ($azDaily.TotalRows -ne $expectedDailyRows) {
                throw "PREFLIGHT_FAIL: Azure Daily row count for $Year is $($azDaily.TotalRows) (Expected: $expectedDailyRows)."
            }
            if ($locDaily.TotalRows -ne $expectedDailyRows) {
                throw "PREFLIGHT_FAIL: Local Daily row count for $Year is $($locDaily.TotalRows) (Expected: $expectedDailyRows)."
            }
        }

        if ($azDaily.DeterministicSha256 -ne $locDaily.DeterministicSha256) {
            throw "PREFLIGHT_FAIL: Daily deterministic hash mismatch between Azure and Local for $Year."
        }

        Write-Host "  * Azure CurrentVersion == 2:                  [OK]" -ForegroundColor Green
        Write-Host "  * Feed v1 INSERT + v2 HARD_DELETE:            [OK]" -ForegroundColor Green
        Write-Host "  * Tombstone Count == 1 (matching canary):     [OK]" -ForegroundColor Green
        Write-Host "  * ProcessedOperations == 0:                   [OK]" -ForegroundColor Green
        Write-Host "  * Local LastServerVersion == 0:               [OK]" -ForegroundColor Green
        Write-Host "  * LocalOutbox Blockers == 0:                  [OK]" -ForegroundColor Green
        Write-Host "  * Daily Exact Parity ($($locDaily.TotalRows) rows, hash match): [OK]" -ForegroundColor Green

        return @{
            Year = $Year
            Status = "READY_FOR_CATCHUP"
            AzureVersion = $azVer
            LocalVersion = $lastVer
            DailyRows = $locDaily.TotalRows
            DailyHash = $locDaily.DeterministicSha256
            OutboxBlockers = $outboxBlockers
            CanarySyncId = $canarySyncId
        }
    } finally {
        $azureConn.Close()
        $localConn.Close()
    }
}

# --- 8. JWT Helper (Local Decode without Secret/Token Leakage) ---
function Decode-JwtPayloadSafe {
    param([string]$Jwt)
    if ([string]::IsNullOrWhiteSpace($Jwt)) { return $null }
    $parts = $Jwt.Split('.')
    if ($parts.Length -lt 2) { return $null }
    $payloadBase64 = $parts[1].Replace('-', '+').Replace('_', '/')
    switch ($payloadBase64.Length % 4) {
        2 { $payloadBase64 += '==' }
        3 { $payloadBase64 += '=' }
    }
    $bytes = [Convert]::FromBase64String($payloadBase64)
    $jsonStr = [Encoding]::UTF8.GetString($bytes)
    return ($jsonStr | ConvertFrom-Json)
}

# ==============================================================================
# EXECUTION LOGIC
# ==============================================================================

if ($DryRun) {
    Write-Host "`n==========================================================================" -ForegroundColor Cyan
    Write-Host "  SLICE 4.5B-A: OPERATOR CATCH-UP DRY-RUN MODE (PURE READ-ONLY)          " -ForegroundColor Cyan
    Write-Host "==========================================================================" -ForegroundColor Cyan

    $res2026 = Invoke-PreflightVerification -Year "2026" -AzureConnStr $Azure2026ConnectionString -LocalConnStr $Local2026ConnectionString -IsIsolated $AllowIsolatedExecutionOnly
    $res2027 = Invoke-PreflightVerification -Year "2027" -AzureConnStr $Azure2027ConnectionString -LocalConnStr $Local2027ConnectionString -IsIsolated $AllowIsolatedExecutionOnly

    Write-Host "`n==========================================================================" -ForegroundColor Cyan
    Write-Host "  OPERATOR CATCH-UP DRY-RUN PREFLIGHT SUMMARY                              " -ForegroundColor Cyan
    Write-Host "==========================================================================" -ForegroundColor Cyan
    Write-Host "Year 2026: $($res2026.Status) (Azure=$($res2026.AzureVersion), Local=$($res2026.LocalVersion), Parity=EXACT, Blockers=$($res2026.OutboxBlockers))" -ForegroundColor Green
    Write-Host "Year 2027: $($res2027.Status) (Azure=$($res2027.AzureVersion), Local=$($res2027.LocalVersion), Parity=EXACT, Blockers=$($res2027.OutboxBlockers))" -ForegroundColor Green
    Write-Host "Status: DRY-RUN SUCCESS (Zero API calls, Zero mutations, Pure Read-Only)" -ForegroundColor Green
    Write-Host "==========================================================================" -ForegroundColor Cyan

    return [PSCustomObject]@{
        Mode = "DryRun"
        Year2026 = $res2026
        Year2027 = $res2027
        OverallStatus = "DRY_RUN_PASSED"
    }
}

# --- Execute Mode ---
if ($Execute) {
    Write-Host "`n==========================================================================" -ForegroundColor Yellow
    Write-Host "  SLICE 4.5B-A: OPERATOR CATCH-UP EXECUTE MODE (CONTROLLED)               " -ForegroundColor Yellow
    Write-Host "==========================================================================" -ForegroundColor Yellow

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        $e2eEnv = Join-Path $repoRoot "tests\e2e\.env"
        if (Test-Path $e2eEnv) {
            foreach ($line in Get-Content $e2eEnv) {
                if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)\s*$') {
                    $k = $matches[1].Trim()
                    $v = $matches[2].Trim().Trim('"').Trim("'")
                    if (($k -eq "IPROGRAM_OPERATOR_USERNAME" -or $k -eq "E2E_USERNAME") -and [string]::IsNullOrWhiteSpace($Username)) { $Username = $v }
                    if (($k -eq "IPROGRAM_OPERATOR_PASSWORD" -or $k -eq "E2E_PASSWORD") -and [string]::IsNullOrWhiteSpace($Password)) { $Password = $v }
                }
            }
        }
    }

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        throw "OPERATOR_CREDENTIALS_MISSING: IPROGRAM_OPERATOR_USERNAME and IPROGRAM_OPERATOR_PASSWORD must be provided for execution."
    }

    $apiDir = Join-Path $repoRoot "src\Api"
    $apiDll = Join-Path $apiDir "bin\Release\net10.0\Auth.Api.dll"
    if (-not (Test-Path $apiDll)) {
        $apiDll = Join-Path $apiDir "bin\Debug\net10.0\Auth.Api.dll"
    }
    if (-not (Test-Path $apiDll)) {
        throw "API_BIN_NOT_FOUND: Could not find Auth.Api.dll in Release or Debug."
    }

    $tempLog = [System.IO.Path]::GetTempFileName()
    $tempErr = [System.IO.Path]::GetTempFileName()
    $baseUrl = "http://127.0.0.1:$Port"
    $apiProcess = $null

    try {
        # 1. Run Preflights before starting API
        $pre2026 = Invoke-PreflightVerification -Year "2026" -AzureConnStr $Azure2026ConnectionString -LocalConnStr $Local2026ConnectionString -IsIsolated $AllowIsolatedExecutionOnly
        $pre2027 = Invoke-PreflightVerification -Year "2027" -AzureConnStr $Azure2027ConnectionString -LocalConnStr $Local2027ConnectionString -IsIsolated $AllowIsolatedExecutionOnly

        # 2. Spawn dedicated temporary API process
        Write-Host "`nStarting dedicated operator API process on $baseUrl..." -ForegroundColor Cyan
        
        $env:ASPNETCORE_URLS = $baseUrl
        $env:ASPNETCORE_ENVIRONMENT = "Development"
        $env:Sync__PullEnabled = "true"
        $env:Sync__PushEnabled = "false"
        $env:Sync__AuthoritativeTrackingEnabled = "false"
        $env:LocalFirst__Enabled = "false"
        $env:LocalFirst__ReadOnlyMode = "false"
        
        if ($AllowIsolatedExecutionOnly) {
            $env:Sync__AllowIsolatedLocalRemoteForTesting = "true"
            $env:ConnectionStrings__DefaultConnection = $Azure2026ConnectionString
            $env:ConnectionStrings__CON2027 = $Azure2027ConnectionString
            $env:ConnectionStrings__LocalConnection2026 = $Local2026ConnectionString
            $env:ConnectionStrings__LocalConnection2027 = $Local2027ConnectionString
        }

        $apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "`"$apiDll`"" -WorkingDirectory $apiDir -PassThru -NoNewWindow -RedirectStandardOutput $tempLog -RedirectStandardError $tempErr

        # Wait for API server readiness
        $ready = $false
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 30000 -and -not $ready) {
            Start-Sleep -Milliseconds 500
            try {
                $st = Invoke-RestMethod -Uri "$baseUrl/api/account/runtime-status" -Method Get -TimeoutSec 2 -ErrorAction SilentlyContinue
                if ($st -and $st.runtimeMode -eq "Online") {
                    $ready = $true
                }
            } catch {}
        }

        if (-not $ready) {
            $errContent = if (Test-Path $tempErr) { Get-Content $tempErr -Raw } else { "" }
            $outContent = if (Test-Path $tempLog) { Get-Content $tempLog -Raw } else { "" }
            throw "API_START_TIMEOUT: Dedicated API process failed to become ready on $baseUrl within 30s. $errContent $outContent"
        }
        Write-Host "Dedicated API process is online at $baseUrl." -ForegroundColor Green

        # --- Phase 1: Catch-Up Year 2026 ---
        Write-Host "`n==========================================================================" -ForegroundColor Cyan
        Write-Host "  PHASE 1: CATCH-UP YEAR 2026                                             " -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # A. Obtain Admin JWT for 2026
        Write-Host "Obtaining Admin JWT for 2026..." -NoNewline
        $loginBody = @{
            username = $Username
            password = $Password
        } | ConvertTo-Json

        $loginHeaders = @{
            "Content-Type" = "application/json"
            "X-Db-Selection" = "2026"
        }

        $loginRes2026 = Invoke-RestMethod -Uri "$baseUrl/api/account/login" -Method Post -Body $loginBody -Headers $loginHeaders -TimeoutSec 10
        $token2026 = $loginRes2026.token
        if ([string]::IsNullOrWhiteSpace($token2026)) {
            throw "AUTH_ERROR: Failed to obtain token for 2026."
        }

        # Validate JWT claims
        $jwtClaims2026 = Decode-JwtPayloadSafe -Jwt $token2026
        $roles2026 = if ($jwtClaims2026.role) { $jwtClaims2026.role } else { $jwtClaims2026.'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' }
        $isAdmin2026 = ($roles2026 -is [array] -and $roles2026 -contains "Admin") -or ($roles2026 -eq "Admin")
        if (-not $isAdmin2026) {
            throw "AUTH_ERROR: User is not in role Admin."
        }
        if ($jwtClaims2026.db -ne "2026") {
            throw "AUTH_ERROR: Token db claim mismatch. Expected '2026', got '$($jwtClaims2026.db)'."
        }
        Write-Host " PASS (Admin authenticated with db=2026)" -ForegroundColor Green

        # B. Call POST /api/sync/pull for 2026
        Write-Host "Executing POST /api/sync/pull for 2026..." -NoNewline
        $pullHeaders2026 = @{
            "Authorization" = "Bearer $token2026"
        }
        $pullRes2026 = $null
        try {
            $pullRes2026 = Invoke-RestMethod -Uri "$baseUrl/api/sync/pull" -Method Post -Headers $pullHeaders2026 -TimeoutSec 60
        } catch {
            Write-Host " FAILED" -ForegroundColor Red
            Write-Host "PULL_ERROR_MSG: $($_.Exception.Message)" -ForegroundColor Red
            if ($_.Exception.Response) {
                try {
                    $respStream = $_.Exception.Response.GetResponseStream()
                    $sr = New-Object System.IO.StreamReader($respStream)
                    Write-Host "PULL_ERROR_BODY: $($sr.ReadToEnd())" -ForegroundColor Red
                } catch {}
            }
            if (Test-Path $tempLog) {
                Write-Host "API_LOG_DUMP:`n$(Get-Content $tempLog -Raw)" -ForegroundColor Yellow
            }
            if (Test-Path $tempErr) {
                Write-Host "API_ERR_DUMP:`n$(Get-Content $tempErr -Raw)" -ForegroundColor Yellow
            }
            throw
        }
        Write-Host " PASS (Result: PrevWatermark=$($pullRes2026.previousWatermark), FinalServerVer=$($pullRes2026.finalServerVersion), IsNoOp=$($pullRes2026.isNoOp))" -ForegroundColor Green

        if ($pullRes2026.previousWatermark -ne 0 -or $pullRes2026.finalServerVersion -ne 2) {
            throw "PULL_RESULT_ERROR: 2026 Pull watermark anomaly. Expected 0 -> 2, got $($pullRes2026.previousWatermark) -> $($pullRes2026.finalServerVersion)."
        }

        # C. Post-Pull Audit 2026
        Write-Host "Auditing 2026 post-pull state..." -NoNewline
        $locConn2026 = New-Object SqlConnection($Local2026ConnectionString)
        $locConn2026.Open()
        try {
            $cmdV = $locConn2026.CreateCommand()
            $cmdV.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';"
            $postVer2026 = [int64]$cmdV.ExecuteScalar()
            if ($postVer2026 -ne 2) {
                throw "POST_AUDIT_ERROR: 2026 Local LastServerVersion is $postVer2026 (Expected: 2)."
            }

            $postDaily2026 = Calculate-DailyHashAndCounts $locConn2026
            if ($postDaily2026.DeterministicSha256 -ne $pre2026.DailyHash) {
                throw "POST_AUDIT_ERROR: 2026 Daily deterministic hash drift detected after pull."
            }
            if ($postDaily2026.TotalRows -ne $pre2026.DailyRows) {
                throw "POST_AUDIT_ERROR: 2026 Daily total rows changed from $($pre2026.DailyRows) to $($postDaily2026.TotalRows)."
            }
        } finally {
            $locConn2026.Close()
        }
        Write-Host " PASS (LastServerVersion=2, Daily parity 100% exact)" -ForegroundColor Green

        # --- Phase 2: Catch-Up Year 2027 ---
        Write-Host "`n==========================================================================" -ForegroundColor Cyan
        Write-Host "  PHASE 2: CATCH-UP YEAR 2027                                             " -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # A. Obtain Admin JWT for 2027
        Write-Host "Obtaining Admin JWT for 2027..." -NoNewline
        $loginHeaders2027 = @{
            "Content-Type" = "application/json"
            "X-Db-Selection" = "2027"
        }
        $loginRes2027 = Invoke-RestMethod -Uri "$baseUrl/api/account/login" -Method Post -Body $loginBody -Headers $loginHeaders2027 -TimeoutSec 10
        $token2027 = $loginRes2027.token
        if ([string]::IsNullOrWhiteSpace($token2027)) {
            throw "AUTH_ERROR: Failed to obtain token for 2027."
        }

        # Validate JWT claims
        $jwtClaims2027 = Decode-JwtPayloadSafe -Jwt $token2027
        $roles2027 = if ($jwtClaims2027.role) { $jwtClaims2027.role } else { $jwtClaims2027.'http://schemas.microsoft.com/ws/2008/06/identity/claims/role' }
        $isAdmin2027 = ($roles2027 -is [array] -and $roles2027 -contains "Admin") -or ($roles2027 -eq "Admin")
        if (-not $isAdmin2027) {
            throw "AUTH_ERROR: User is not in role Admin."
        }
        if ($jwtClaims2027.db -ne "2027") {
            throw "AUTH_ERROR: Token db claim mismatch. Expected '2027', got '$($jwtClaims2027.db)'."
        }
        Write-Host " PASS (Admin authenticated with db=2027)" -ForegroundColor Green

        # B. Call POST /api/sync/pull for 2027
        Write-Host "Executing POST /api/sync/pull for 2027..." -NoNewline
        $pullHeaders2027 = @{
            "Authorization" = "Bearer $token2027"
        }
        $pullRes2027 = $null
        try {
            $pullRes2027 = Invoke-RestMethod -Uri "$baseUrl/api/sync/pull" -Method Post -Headers $pullHeaders2027 -TimeoutSec 60
        } catch {
            Write-Host " FAILED" -ForegroundColor Red
            Write-Host "PULL_ERROR_MSG: $($_.Exception.Message)" -ForegroundColor Red
            if ($_.Exception.Response) {
                try {
                    $respStream = $_.Exception.Response.GetResponseStream()
                    $sr = New-Object System.IO.StreamReader($respStream)
                    Write-Host "PULL_ERROR_BODY: $($sr.ReadToEnd())" -ForegroundColor Red
                } catch {}
            }
            if (Test-Path $tempLog) {
                Write-Host "API_LOG_DUMP:`n$(Get-Content $tempLog -Raw)" -ForegroundColor Yellow
            }
            if (Test-Path $tempErr) {
                Write-Host "API_ERR_DUMP:`n$(Get-Content $tempErr -Raw)" -ForegroundColor Yellow
            }
            throw
        }
        Write-Host " PASS (Result: PrevWatermark=$($pullRes2027.previousWatermark), FinalServerVer=$($pullRes2027.finalServerVersion), IsNoOp=$($pullRes2027.isNoOp))" -ForegroundColor Green

        if ($pullRes2027.previousWatermark -ne 0 -or $pullRes2027.finalServerVersion -ne 2) {
            throw "PULL_RESULT_ERROR: 2027 Pull watermark anomaly. Expected 0 -> 2, got $($pullRes2027.previousWatermark) -> $($pullRes2027.finalServerVersion)."
        }

        # C. Post-Pull Audit 2027
        Write-Host "Auditing 2027 post-pull state..." -NoNewline
        $locConn2027 = New-Object SqlConnection($Local2027ConnectionString)
        $locConn2027.Open()
        try {
            $cmdV27 = $locConn2027.CreateCommand()
            $cmdV27.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2027';"
            $postVer2027 = [int64]$cmdV27.ExecuteScalar()
            if ($postVer2027 -ne 2) {
                throw "POST_AUDIT_ERROR: 2027 Local LastServerVersion is $postVer2027 (Expected: 2)."
            }

            $postDaily2027 = Calculate-DailyHashAndCounts $locConn2027
            if ($postDaily2027.DeterministicSha256 -ne $pre2027.DailyHash) {
                throw "POST_AUDIT_ERROR: 2027 Daily deterministic hash drift detected after pull."
            }
            if ($postDaily2027.TotalRows -ne $pre2027.DailyRows) {
                throw "POST_AUDIT_ERROR: 2027 Daily total rows changed from $($pre2027.DailyRows) to $($postDaily2027.TotalRows)."
            }
        } finally {
            $locConn2027.Close()
        }
        Write-Host " PASS (LastServerVersion=2, Daily parity 100% exact)" -ForegroundColor Green

        Write-Host "`n==========================================================================" -ForegroundColor Green
        Write-Host "  CONTROLLED CATCH-UP EXECUTION COMPLETED SUCCESSFULLY                    " -ForegroundColor Green
        Write-Host "==========================================================================" -ForegroundColor Green

        return [PSCustomObject]@{
            Mode = "Execute"
            Year2026 = @{
                PreviousWatermark = $pullRes2026.previousWatermark
                FinalServerVersion = $pullRes2026.finalServerVersion
                IsNoOp = $pullRes2026.isNoOp
                DailyParity = "EXACT_MATCH"
            }
            Year2027 = @{
                PreviousWatermark = $pullRes2027.previousWatermark
                FinalServerVersion = $pullRes2027.finalServerVersion
                IsNoOp = $pullRes2027.isNoOp
                DailyParity = "EXACT_MATCH"
            }
            OverallStatus = "CATCHUP_SUCCESS"
        }
    } finally {
        # Cleanup dedicated process and transient environment
        if ($apiProcess -and -not $apiProcess.HasExited) {
            Write-Host "Stopping dedicated operator API process..." -NoNewline
            Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $apiProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
            Write-Host " Stopped." -ForegroundColor Green
        }
        
        Remove-Item Env:\ASPNETCORE_URLS -ErrorAction SilentlyContinue
        Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__PullEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__PushEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\Sync__AuthoritativeTrackingEnabled -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__Enabled -ErrorAction SilentlyContinue
        Remove-Item Env:\LocalFirst__ReadOnlyMode -ErrorAction SilentlyContinue
        
        if ($AllowIsolatedExecutionOnly) {
            Remove-Item Env:\Sync__AllowIsolatedLocalRemoteForTesting -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__CON2027 -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__LocalConnection2026 -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__LocalConnection2027 -ErrorAction SilentlyContinue
        }

        if (Test-Path $tempLog) { Remove-Item $tempLog -Force -ErrorAction SilentlyContinue }
        if (Test-Path $tempErr) { Remove-Item $tempErr -Force -ErrorAction SilentlyContinue }

        # Zero memory variables
        $Password = $null
        $token2026 = $null
        $token2027 = $null
    }
}
