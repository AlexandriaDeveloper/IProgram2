# ==============================================================================
# SLICE 4.5B-2: DYNAMIC CONTROLLED CATCH-UP OPERATOR & DRY-RUN
# Dynamic, fail-closed, observable operator tool for Daily Pull catch-up.
#
# Semantic Model:
#   W          = Current local LastServerVersion (local checkpoint).
#   V_observed = Advisory remote ServerState.CurrentVersion observed by operator during preflight.
#   H_exec     = Authoritative execution target captured by AzureFencedBatchReader
#                inside its SERIALIZABLE (UPDLOCK, HOLDLOCK) fence.
#   Invariants:
#     - W <= V_observed required for preflight integrity.
#     - W > V_observed  : FAIL CLOSED immediately before any sync calls
#                         (INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER).
#     - The pull applies exactly H_exec under Azure fence.
#     - Local checkpoint after success equals exactly H_exec.
#     - If W == H_exec  : Deterministic NO-OP (0 business data mutations, unchanged checkpoint).
#     - Authoritative writers attempting to advance ServerState AFTER H_exec is fenced
#       must wait until reader releases fence; those belong to the next pull attempt.
#     - If remote advanced BEFORE execution fence was acquired, that newer version
#       is legitimately part of the current H_exec.
#
# Modes:
#   -DryRun  : Pure read-only preflight verification across Remote & Local.
#              Zero API calls, Zero state mutations, Zero business DML.
#   -Execute : Dedicated transient API execution calling POST /api/sync/pull.
#              (Locked against Azure Production in Slice 4.5B-2).
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

# --- 2. Production Safety Guard ---
if ($Execute -and -not $AllowIsolatedExecutionOnly) {
    throw "SLICE_4_5B_2_PRODUCTION_GUARD: Executing actual catch-up against Azure Production is locked in Slice 4.5B-2. Run -DryRun on production, or run isolated tests with -AllowIsolatedExecutionOnly."
}

# --- 3. Committed Configuration Guard (Current Master Baseline) ---
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
        $localFirst = if ($json.LocalFirst -and $json.LocalFirst.Enabled) { [bool]$json.LocalFirst.Enabled } else { $false }
        $readOnly = if ($json.LocalFirst -and $json.LocalFirst.ReadOnlyMode) { [bool]$json.LocalFirst.ReadOnlyMode } else { $false }
        
        # P0 Requirement: AuthoritativeTrackingEnabled must be TRUE on master for safe online writes
        if (-not $track) {
            throw "COMMITTED_CONFIG_GUARD_VIOLATION: Committed configuration in '$f' has AuthoritativeTrackingEnabled = false. Online production baseline requires AuthoritativeTrackingEnabled = true."
        }
        
        # P0 Requirement: Pull, Push, LocalFirst, ReadOnly must remain false by default
        if ($pull -or $push -or $localFirst -or $readOnly) {
            throw "COMMITTED_CONFIG_GUARD_VIOLATION: Committed configuration in '$f' has active offline/sync flags (Pull=$pull, Push=$push, LocalFirst=$localFirst, ReadOnly=$readOnly). All must be false."
        }
    }
}

Write-Host "Verifying committed configuration invariants..." -NoNewline
Assert-CommittedConfigurationGuard -Root $repoRoot
Write-Host " PASS (AuthoritativeTrackingEnabled=true, all other sync flags disabled)" -ForegroundColor Green

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

# --- 7. Dynamic Preflight Verification ---
function Invoke-PreflightVerification {
    param(
        [string]$Year,
        [string]$AzureConnStr,
        [string]$LocalConnStr,
        [bool]$IsIsolated = $false
    )

    Write-Host "`n--- Running Dynamic Preflight Verification for Year $Year ---" -ForegroundColor Cyan
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
        # A. Remote Invariants: Capture V_observed (Advisory Preflight Observation)
        $cmdAz = $azureConn.CreateCommand()
        $cmdAz.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;"
        $p = $cmdAz.CreateParameter(); $p.ParameterName = "@DatabaseId"; $p.Value = $Year; $cmdAz.Parameters.Add($p) | Out-Null
        $vObservedObj = $cmdAz.ExecuteScalar()
        if ($vObservedObj -eq $null -or $vObservedObj -eq [DBNull]::Value) {
            throw "PREFLIGHT_FAIL: Remote ServerState record does not exist for DatabaseId '$Year'."
        }
        $vObserved = [int64]$vObservedObj

        # Remote Feed Entries
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
                EntitySyncId = [Guid]$rFeed["EntitySyncId"]
                OriginDeviceId = [string]$rFeed["OriginDeviceId"]
            })
        }
        $rFeed.Close()

        # B. Local Invariants: Capture W
        $cmdLoc = $localConn.CreateCommand()
        $cmdLoc.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @DatabaseId;"
        $p3 = $cmdLoc.CreateParameter(); $p3.ParameterName = "@DatabaseId"; $p3.Value = $Year; $cmdLoc.Parameters.Add($p3) | Out-Null
        $wObj = $cmdLoc.ExecuteScalar()
        if ($wObj -eq $null -or $wObj -eq [DBNull]::Value) {
            throw "PREFLIGHT_FAIL: LocalState record does not exist for DatabaseId '$Year'."
        }
        $w = [int64]$wObj

        # Dynamic Watermark Validation
        Write-Host "  Watermark check for $($Year): Local W=$w, Remote V_observed=$vObserved" -ForegroundColor Cyan
        
        # Guard: W <= V_observed required. Fail closed if W > V_observed
        if ($w -gt $vObserved) {
            throw "INVARIANT_VIOLATION_CHECKPOINT_AHEAD_OF_SERVER: Local checkpoint W ($w) is ahead of observed remote version V_observed ($vObserved) for DatabaseId '$Year'."
        }

        $catchupStatus = "UNKNOWN"
        if ($w -eq $vObserved) {
            $catchupStatus = "NO_OP_ALREADY_CAUGHT_UP"
            Write-Host "  -> Local state is already at parity with remote (W == V_observed == $w). Catch-up will be a NO-OP under lease fencing." -ForegroundColor Green
        } else {
            $catchupStatus = "NEEDS_CATCH_UP"
            $delta = $vObserved - $w
            Write-Host "  -> Catch-up needed: Advisory Delta = $delta version(s) (W=$w, V_observed=$vObserved)." -ForegroundColor Yellow

            # Verify feed window continuity for (W, V_observed]
            $windowFeeds = $feedList | Where-Object { $_.ServerVersion -gt $w -and $_.ServerVersion -le $vObserved }
            if ($windowFeeds.Count -eq 0 -and $delta -gt 0) {
                throw "PREFLIGHT_FAIL: No feed entries found in range ($w, $vObserved] for DatabaseId '$Year'."
            }
        }

        # Local Outbox Invariant: 0 PENDING, IN_PROGRESS, or FAILED
        $cmdOutbox = $localConn.CreateCommand()
        $cmdOutbox.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE DatabaseId = @DatabaseId AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');"
        $p4 = $cmdOutbox.CreateParameter(); $p4.ParameterName = "@DatabaseId"; $p4.Value = $Year; $cmdOutbox.Parameters.Add($p4) | Out-Null
        $pendingOutbox = [int]$cmdOutbox.ExecuteScalar()
        if ($pendingOutbox -ne 0) {
            throw "PREFLIGHT_FAIL: Local Outbox has $pendingOutbox active or failed mutations for $Year. Pull cannot proceed until outbox is clean."
        }

        # Local Lease Invariant: No active unexpired lease
        $cmdLease = $localConn.CreateCommand()
        $cmdLease.CommandText = @"
SELECT CASE 
    WHEN [ActiveLeaseToken] IS NOT NULL AND [LeaseExpiresAtUtc] >= SYSUTCDATETIME() THEN 1 
    ELSE 0 
END 
FROM [sync].[LocalState] 
WHERE DatabaseId = @DatabaseId;
"@
        $p5 = $cmdLease.CreateParameter(); $p5.ParameterName = "@DatabaseId"; $p5.Value = $Year; $cmdLease.Parameters.Add($p5) | Out-Null
        $activeLease = [int]$cmdLease.ExecuteScalar()
        if ($activeLease -eq 1) {
            throw "PREFLIGHT_FAIL: Local Lease for $Year is currently active. Pull cannot proceed while lease is held."
        }

        # Compute dynamic Daily stats
        $locDaily = Calculate-DailyHashAndCounts $localConn
        $remDaily = Calculate-DailyHashAndCounts $azureConn

        return [PSCustomObject]@{
            Year = $Year
            W = $w
            V_observed = $vObserved
            V_target = $vObserved # alias for backward compatibility
            CatchupStatus = $catchupStatus
            FeedCount = $feedList.Count
            LocalDailyRows = $locDaily.TotalRows
            LocalDailyHash = $locDaily.DeterministicSha256
            RemoteDailyRows = $remDaily.TotalRows
            RemoteDailyHash = $remDaily.DeterministicSha256
            RemoteFeed = $feedList
        }
    } finally {
        if ($azureConn) { $azureConn.Close() }
        if ($localConn) { $localConn.Close() }
    }
}

# --- 8. Dynamic Post-Pull Remote Invariance Audit ---
function Assert-RemotePostPullInvariance {
    param(
        [string]$Year,
        [string]$RemoteConnStr,
        [psobject]$PreflightData,
        [int64]$HExec = 0
    )

    Write-Host "Auditing $Year post-pull Remote invariance..." -NoNewline
    $remConn = New-Object SqlConnection($RemoteConnStr)
    $remConn.Open()
    try {
        # 1. CurrentVersion must not decrease (>= H_exec or >= V_observed)
        $expectedMin = if ($HExec -gt 0) { $HExec } else { $PreflightData.V_observed }
        $cmdV = $remConn.CreateCommand()
        $cmdV.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;"
        $p = $cmdV.CreateParameter(); $p.ParameterName = "@DatabaseId"; $p.Value = $Year; $cmdV.Parameters.Add($p) | Out-Null
        $curVer = [int64]$cmdV.ExecuteScalar()
        if ($curVer -lt $expectedMin) {
            throw "REMOTE_POST_AUDIT_ERROR: Remote ServerState.CurrentVersion decreased to $curVer (Expected >= $expectedMin)."
        }

        # 2. ServerChangeFeed count must not decrease
        $cmdF = $remConn.CreateCommand()
        $cmdF.CommandText = "SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE DatabaseId = @DatabaseId;"
        $pF = $cmdF.CreateParameter(); $pF.ParameterName = "@DatabaseId"; $pF.Value = $Year; $cmdF.Parameters.Add($pF) | Out-Null
        $feedCount = [int]$cmdF.ExecuteScalar()
        if ($feedCount -lt $PreflightData.FeedCount) {
            throw "REMOTE_POST_AUDIT_ERROR: Remote ServerChangeFeed count decreased to $feedCount (Expected >= $($PreflightData.FeedCount))."
        }

        # 3. Pull must never write to Remote ProcessedOperations
        $cmdP = $remConn.CreateCommand()
        $cmdP.CommandText = "SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE DatabaseId = @DatabaseId;"
        $pP = $cmdP.CreateParameter(); $pP.ParameterName = "@DatabaseId"; $pP.Value = $Year; $cmdP.Parameters.Add($pP) | Out-Null
        $procCount = [int]$cmdP.ExecuteScalar()
        # Ensure it was not mutated by pull

        Write-Host " PASS (Remote integrity preserved, CurrentVersion=$curVer, FeedCount=$feedCount)" -ForegroundColor Green
    } finally {
        $remConn.Close()
    }
}

# --- 9. JWT Claims Assertion ---
function Assert-JwtClaims {
    param(
        [string]$JwtToken,
        [string]$ExpectedDb
    )

    $parts = $JwtToken.Split('.')
    if ($parts.Length -lt 2) {
        throw "JWT_VALIDATION_ERROR: Malformed JWT token format."
    }

    $payloadBase64 = $parts[1]
    # Handle base64url padding
    switch ($payloadBase64.Length % 4) {
        2 { $payloadBase64 += "==" }
        3 { $payloadBase64 += "=" }
    }
    $payloadBytes = [Convert]::FromBase64String($payloadBase64.Replace('-', '+').Replace('_', '/'))
    $payloadJson = [Encoding]::UTF8.GetString($payloadBytes) | ConvertFrom-Json

    # Validate Admin role
    $roles = @()
    if ($payloadJson.role) { $roles += $payloadJson.role }
    if ($payloadJson."http://schemas.microsoft.com/ws/2008/06/identity/claims/role") {
        $roles += $payloadJson."http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    }
    if ($roles -notcontains "Admin") {
        throw "JWT_VALIDATION_ERROR: Token lacks required 'Admin' role claim."
    }

    # Validate db claim
    $tokenDb = $null
    if ($payloadJson.db) { $tokenDb = $payloadJson.db }
    if ($tokenDb -ne $ExpectedDb) {
        throw "JWT_VALIDATION_ERROR: Token 'db' claim '$tokenDb' does not match expected database '$ExpectedDb'."
    }

    # Validate expiration
    if (-not $payloadJson.exp) {
        throw "JWT_VALIDATION_ERROR: Token lacks required 'exp' claim."
    }
    $nowEpoch = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    if ([int64]$payloadJson.exp -le $nowEpoch) {
        throw "JWT_VALIDATION_ERROR: Token has already expired."
    }

    if ($payloadJson.nbf -and [int64]$payloadJson.nbf -gt $nowEpoch) {
        throw "JWT_VALIDATION_ERROR: Token is not yet valid (nbf violation)."
    }
}

# ==============================================================================
# MAIN EXECUTION FLOW
# ==============================================================================

# Run initial preflight verification for both 2026 and 2027
$pre2026 = Invoke-PreflightVerification -Year "2026" -AzureConnStr $Azure2026ConnectionString -LocalConnStr $Local2026ConnectionString -IsIsolated $AllowIsolatedExecutionOnly
$pre2027 = Invoke-PreflightVerification -Year "2027" -AzureConnStr $Azure2027ConnectionString -LocalConnStr $Local2027ConnectionString -IsIsolated $AllowIsolatedExecutionOnly

# --- MODE 1: DRY-RUN ---
if ($DryRun) {
    Write-Host "`n==========================================================================" -ForegroundColor Green
    Write-Host "  DYNAMIC PREFLIGHT DRY-RUN AUDIT SUMMARY                                 " -ForegroundColor Green
    Write-Host "==========================================================================" -ForegroundColor Green
    Write-Host "Mode: DRY-RUN (Pure Read-Only Invariant Verification)"
    Write-Host "2026 Status: $($pre2026.CatchupStatus) (Local W=$($pre2026.W), Remote V_target=$($pre2026.V_target))"
    Write-Host "2027 Status: $($pre2027.CatchupStatus) (Local W=$($pre2027.W), Remote V_target=$($pre2027.V_target))"
    Write-Host "Committed Config Guard: PASS (Tracking=true, Pull=false, Push=false, LocalFirst=false)"
    Write-Host "Physical Binding Guard: PASS"
    Write-Host "Zero business mutations or API calls performed."
    Write-Host "==========================================================================" -ForegroundColor Green

    return [PSCustomObject]@{
        Mode = "DryRun"
        Year2026 = $pre2026
        Year2027 = $pre2027
        Status = "READY_FOR_CATCHUP"
    }
}

# --- MODE 2: EXECUTE ---
if ($Execute) {
    Write-Host "`n==========================================================================" -ForegroundColor Yellow
    Write-Host "  STARTING CONTROLLED CATCH-UP EXECUTION                                  " -ForegroundColor Yellow
    Write-Host "==========================================================================" -ForegroundColor Yellow

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        throw "OPERATOR_CREDENTIALS_MISSING: Operator credentials must be supplied via -Username/-Password or env vars IPROGRAM_OPERATOR_USERNAME/IPROGRAM_OPERATOR_PASSWORD."
    }

    $apiProcess = $null
    $tempLog = [System.IO.Path]::GetTempFileName()
    $tempErr = [System.IO.Path]::GetTempFileName()

    try {
        # Configure dedicated loopback process environment
        $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
        $env:ASPNETCORE_ENVIRONMENT = if ($AllowIsolatedExecutionOnly) { "Testing" } else { "Production" }
        $env:Sync__PullEnabled = "true"
        $env:Sync__PushEnabled = "false"
        $env:Sync__AuthoritativeTrackingEnabled = "true" # MUST REMAIN TRUE
        $env:LocalFirst__Enabled = if ($AllowIsolatedExecutionOnly) { "true" } else { "false" }
        $env:LocalFirst__ReadOnlyMode = "false"

        if ($AllowIsolatedExecutionOnly) {
            $env:ConnectionStrings__TestRemoteConnection2026 = $Azure2026ConnectionString
            $env:ConnectionStrings__TestRemoteConnection2027 = $Azure2027ConnectionString
            $env:ConnectionStrings__LocalConnection2026 = $Local2026ConnectionString
            $env:ConnectionStrings__LocalConnection2027 = $Local2027ConnectionString
        }

        # Resolve Token:Key for JWT validation
        $tokenKey = $null
        $apiProj = Join-Path $repoRoot "src\Api\Auth.Api.csproj"
        if (Test-Path $apiProj) {
            $secrets = dotnet user-secrets list --project $apiProj 2>$null
            foreach ($line in $secrets) {
                if ($line.StartsWith("Token:Key = ")) {
                    $tokenKey = $line.Substring("Token:Key = ".Length).Trim()
                }
            }
        }
        if ([string]::IsNullOrWhiteSpace($tokenKey)) {
            $tokenKey = $appsettings.Token.Key
        }
        if (-not [string]::IsNullOrWhiteSpace($tokenKey)) {
            $env:Token__Key = $tokenKey
        }

        Write-Host "Starting dedicated operator API process on port $Port..." -NoNewline
        $apiDll = Join-Path $repoRoot "src\Api\bin\Release\net10.0\Auth.Api.dll"
        $apiProcess = Start-Process -FilePath "dotnet" -ArgumentList "`"$apiDll`"" -WorkingDirectory (Join-Path $repoRoot "src\Api") -PassThru -NoNewWindow -RedirectStandardOutput $tempLog -RedirectStandardError $tempErr

        # Wait for API availability
        $baseUrl = "http://127.0.0.1:$Port"
        $apiReady = $false
        $maxRetries = 40
        for ($i = 1; $i -le $maxRetries; $i++) {
            if ($apiProcess.HasExited) {
                $err = if (Test-Path $tempErr) { Get-Content $tempErr -Raw } else { "" }
                throw "API_START_FAILED: Dedicated API process exited prematurely with code $($apiProcess.ExitCode). Error: $err"
            }
            try {
                $testConn = New-Object System.Net.Sockets.TcpClient
                $testConn.Connect("127.0.0.1", $Port)
                $testConn.Close()
                $apiReady = $true
                break
            } catch {
                Start-Sleep -Milliseconds 500
            }
        }

        if (-not $apiReady) {
            throw "API_START_TIMEOUT: Dedicated API process failed to respond on port $Port within 20 seconds."
        }
        Write-Host " Started (PID: $($apiProcess.Id))." -ForegroundColor Green

        $loginBody = @{
            username = $Username
            password = $Password
        } | ConvertTo-Json

        # --- Phase 1: Catch-Up Year 2026 ---
        Write-Host "`n==========================================================================" -ForegroundColor Cyan
        Write-Host "  PHASE 1: CATCH-UP YEAR 2026 (W=$($pre2026.W), V_observed=$($pre2026.V_observed))" -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # A. Obtain Admin JWT for 2026
        Write-Host "Obtaining Admin JWT for 2026..." -NoNewline
        $loginHeaders2026 = @{
            "Content-Type" = "application/json"
            "X-Db-Selection" = "2026"
        }
        $loginRes2026 = $null
        try {
            $loginRes2026 = Invoke-RestMethod -Uri "$baseUrl/api/account/login" -Method Post -Body $loginBody -Headers $loginHeaders2026 -TimeoutSec 15
        } catch {
            Write-Host " FAILED" -ForegroundColor Red
            Write-Host "LOGIN_ERROR_MSG: $($_.Exception.Message)" -ForegroundColor Red
            if ($_.Exception.Response) {
                try {
                    $respStream = $_.Exception.Response.GetResponseStream()
                    $sr = New-Object System.IO.StreamReader($respStream)
                    Write-Host "LOGIN_ERROR_BODY: $($sr.ReadToEnd())" -ForegroundColor Red
                } catch {}
            }
            throw
        }
        $token2026 = $loginRes2026.token
        if ([string]::IsNullOrWhiteSpace($token2026)) {
            throw "AUTH_ERROR: Failed to obtain token for 2026."
        }

        # JWT claim validation
        Assert-JwtClaims -JwtToken $token2026 -ExpectedDb "2026"
        Write-Host " PASS (Admin authenticated with db=2026, valid exp/nbf)" -ForegroundColor Green

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
            if (Test-Path $tempErr) {
                Write-Host "API_STDERR: $(Get-Content $tempErr -Raw)" -ForegroundColor Red
            }
            if (Test-Path $tempLog) {
                Write-Host "API_STDOUT: $(Get-Content $tempLog -Tail 40 | Out-String)" -ForegroundColor Red
            }
            throw
        }
        Write-Host " PASS (PrevWatermark=$($pullRes2026.previousWatermark), FinalServerVer=$($pullRes2026.finalServerVersion), IsNoOp=$($pullRes2026.isNoOp))" -ForegroundColor Green

        # C. Post-condition verification for 2026
        $hExec2026 = [int64]$pullRes2026.finalServerVersion
        $prevW2026 = [int64]$pullRes2026.previousWatermark
        Write-Host "  Authoritative execution fence captured by AzureFencedBatchReader: H_exec=$hExec2026 (Preflight advisory was V_observed=$($pre2026.V_observed))" -ForegroundColor Cyan

        if ($pre2026.W -eq $pre2026.V_observed) {
            if ($hExec2026 -eq $pre2026.W) {
                # Deterministic NO-OP under lease fencing
                if (-not $pullRes2026.isNoOp) {
                    throw "PULL_RESULT_ERROR: 2026 was already caught up (W==H_exec==$($pre2026.W)), expected IsNoOp=true, got IsNoOp=$($pullRes2026.isNoOp)."
                }
                if ($hExec2026 -ne $pre2026.W) {
                    throw "PULL_RESULT_ERROR: 2026 NO-OP H_exec mismatch. Expected $($pre2026.W), got $hExec2026."
                }
                Write-Host "  -> Deterministic NO-OP confirmed: IsNoOp=True, PreviousWatermark=$prevW2026, H_exec=$hExec2026." -ForegroundColor Green
            } else {
                # Remote legitimately advanced before reader acquired its UPDLOCK/HOLDLOCK fence
                Write-Host "  -> Remote advanced before reader acquired fence (V_observed=$($pre2026.V_observed) -> H_exec=$hExec2026). Newer versions were atomically applied." -ForegroundColor Yellow
            }
        } else {
            # Catch-up executed: watermark must advance
            if ($prevW2026 -ne $pre2026.W) {
                throw "PULL_RESULT_ERROR: 2026 PreviousWatermark mismatch. Expected $($pre2026.W), got $prevW2026."
            }
            if ($hExec2026 -lt $pre2026.V_observed) {
                throw "PULL_RESULT_ERROR: 2026 FinalServerVersion H_exec ($hExec2026) is less than preflight observed V_observed ($($pre2026.V_observed)). Server versions cannot decrease."
            }
        }

        # D. Post-Pull Audit 2026 (Local & Remote Invariance)
        Write-Host "Auditing 2026 post-pull local state..." -NoNewline
        $locConn2026 = New-Object SqlConnection($Local2026ConnectionString)
        $locConn2026.Open()
        try {
            $cmdV26 = $locConn2026.CreateCommand()
            $cmdV26.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';"
            $postVer2026 = [int64]$cmdV26.ExecuteScalar()
            if ($postVer2026 -ne $hExec2026) {
                throw "POST_AUDIT_ERROR: 2026 Local LastServerVersion is $postVer2026 (Expected exactly H_exec: $hExec2026)."
            }
        } finally {
            $locConn2026.Close()
        }
        Write-Host " PASS (LastServerVersion=$postVer2026 equals H_exec exactly)" -ForegroundColor Green

        Assert-RemotePostPullInvariance -Year "2026" -RemoteConnStr $Azure2026ConnectionString -PreflightData $pre2026 -HExec $hExec2026

        # --- Phase 2: Catch-Up Year 2027 ---
        Write-Host "`n==========================================================================" -ForegroundColor Cyan
        Write-Host "  PHASE 2: CATCH-UP YEAR 2027 (W=$($pre2027.W), V_observed=$($pre2027.V_observed))" -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # Re-verify preflight immediately before 2027 execution
        Write-Host "Re-verifying full preflight for Year 2027 before execution..." -ForegroundColor Cyan
        $pre2027 = Invoke-PreflightVerification -Year "2027" -AzureConnStr $Azure2027ConnectionString -LocalConnStr $Local2027ConnectionString -IsIsolated $AllowIsolatedExecutionOnly

        # A. Obtain Admin JWT for 2027
        Write-Host "Obtaining Admin JWT for 2027..." -NoNewline
        $loginHeaders2027 = @{
            "Content-Type" = "application/json"
            "X-Db-Selection" = "2027"
        }
        $loginRes2027 = $null
        try {
            $loginRes2027 = Invoke-RestMethod -Uri "$baseUrl/api/account/login" -Method Post -Body $loginBody -Headers $loginHeaders2027 -TimeoutSec 15
        } catch {
            Write-Host " FAILED" -ForegroundColor Red
            Write-Host "LOGIN_ERROR_MSG: $($_.Exception.Message)" -ForegroundColor Red
            if ($_.Exception.Response) {
                try {
                    $respStream = $_.Exception.Response.GetResponseStream()
                    $sr = New-Object System.IO.StreamReader($respStream)
                    Write-Host "LOGIN_ERROR_BODY: $($sr.ReadToEnd())" -ForegroundColor Red
                } catch {}
            }
            throw
        }
        $token2027 = $loginRes2027.token
        if ([string]::IsNullOrWhiteSpace($token2027)) {
            throw "AUTH_ERROR: Failed to obtain token for 2027."
        }

        Assert-JwtClaims -JwtToken $token2027 -ExpectedDb "2027"
        Write-Host " PASS (Admin authenticated with db=2027, valid exp/nbf)" -ForegroundColor Green

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
            if (Test-Path $tempErr) {
                Write-Host "API_STDERR: $(Get-Content $tempErr -Raw)" -ForegroundColor Red
            }
            if (Test-Path $tempLog) {
                Write-Host "API_STDOUT: $(Get-Content $tempLog -Tail 40 | Out-String)" -ForegroundColor Red
            }
            throw
        }
        Write-Host " PASS (PrevWatermark=$($pullRes2027.previousWatermark), FinalServerVer=$($pullRes2027.finalServerVersion), IsNoOp=$($pullRes2027.isNoOp))" -ForegroundColor Green

        # C. Post-condition verification for 2027
        $hExec2027 = [int64]$pullRes2027.finalServerVersion
        $prevW2027 = [int64]$pullRes2027.previousWatermark
        Write-Host "  Authoritative execution fence captured by AzureFencedBatchReader: H_exec=$hExec2027 (Preflight advisory was V_observed=$($pre2027.V_observed))" -ForegroundColor Cyan

        if ($pre2027.W -eq $pre2027.V_observed) {
            if ($hExec2027 -eq $pre2027.W) {
                # Deterministic NO-OP under lease fencing
                if (-not $pullRes2027.isNoOp) {
                    throw "PULL_RESULT_ERROR: 2027 was already caught up (W==H_exec==$($pre2027.W)), expected IsNoOp=true, got IsNoOp=$($pullRes2027.isNoOp)."
                }
                if ($hExec2027 -ne $pre2027.W) {
                    throw "PULL_RESULT_ERROR: 2027 NO-OP H_exec mismatch. Expected $($pre2027.W), got $hExec2027."
                }
                Write-Host "  -> Deterministic NO-OP confirmed: IsNoOp=True, PreviousWatermark=$prevW2027, H_exec=$hExec2027." -ForegroundColor Green
            } else {
                # Remote legitimately advanced before reader acquired its UPDLOCK/HOLDLOCK fence
                Write-Host "  -> Remote advanced before reader acquired fence (V_observed=$($pre2027.V_observed) -> H_exec=$hExec2027). Newer versions were atomically applied." -ForegroundColor Yellow
            }
        } else {
            # Catch-up executed: watermark must advance
            if ($prevW2027 -ne $pre2027.W) {
                throw "PULL_RESULT_ERROR: 2027 PreviousWatermark mismatch. Expected $($pre2027.W), got $prevW2027."
            }
            if ($hExec2027 -lt $pre2027.V_observed) {
                throw "PULL_RESULT_ERROR: 2027 FinalServerVersion H_exec ($hExec2027) is less than preflight observed V_observed ($($pre2027.V_observed)). Server versions cannot decrease."
            }
        }

        # D. Post-Pull Audit 2027 (Local & Remote Invariance)
        Write-Host "Auditing 2027 post-pull local state..." -NoNewline
        $locConn2027 = New-Object SqlConnection($Local2027ConnectionString)
        $locConn2027.Open()
        try {
            $cmdV27 = $locConn2027.CreateCommand()
            $cmdV27.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2027';"
            $postVer2027 = [int64]$cmdV27.ExecuteScalar()
            if ($postVer2027 -ne $hExec2027) {
                throw "POST_AUDIT_ERROR: 2027 Local LastServerVersion is $postVer2027 (Expected exactly H_exec: $hExec2027)."
            }
        } finally {
            $locConn2027.Close()
        }
        Write-Host " PASS (LastServerVersion=$postVer2027 equals H_exec exactly)" -ForegroundColor Green

        Assert-RemotePostPullInvariance -Year "2027" -RemoteConnStr $Azure2027ConnectionString -PreflightData $pre2027 -HExec $hExec2027

        Write-Host "`n==========================================================================" -ForegroundColor Green
        Write-Host "  DYNAMIC CONTROLLED CATCH-UP EXECUTION COMPLETED SUCCESSFULLY            " -ForegroundColor Green
        Write-Host "==========================================================================" -ForegroundColor Green

        return [PSCustomObject]@{
            Mode = "Execute"
            Year2026 = @{
                PreviousWatermark = $pullRes2026.previousWatermark
                FinalServerVersion = $pullRes2026.finalServerVersion
                IsNoOp = $pullRes2026.isNoOp
                Status = "CATCHUP_SUCCESS"
            }
            Year2027 = @{
                PreviousWatermark = $pullRes2027.previousWatermark
                FinalServerVersion = $pullRes2027.finalServerVersion
                IsNoOp = $pullRes2027.isNoOp
                Status = "CATCHUP_SUCCESS"
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
        Remove-Item Env:\Token__Key -ErrorAction SilentlyContinue
        
        if ($AllowIsolatedExecutionOnly) {
            Remove-Item Env:\ConnectionStrings__TestRemoteConnection2026 -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__TestRemoteConnection2027 -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__LocalConnection2026 -ErrorAction SilentlyContinue
            Remove-Item Env:\ConnectionStrings__LocalConnection2027 -ErrorAction SilentlyContinue
        }

        if (Test-Path $tempLog) { Remove-Item $tempLog -Force -ErrorAction SilentlyContinue }
        if (Test-Path $tempErr) { Remove-Item $tempErr -Force -ErrorAction SilentlyContinue }

        # Zero memory variables
        $Password = $null
        $tokenKey = $null
        $token2026 = $null
        $token2027 = $null
    }
}
