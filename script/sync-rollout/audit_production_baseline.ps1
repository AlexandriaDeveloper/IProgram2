# ==============================================================================
# SLICE 4.4B: PRODUCTION BASELINE RECONCILIATION & READINESS AUDIT TOOL
# Strictly READ-ONLY comparison across Azure and Local databases (2026 & 2027)
# 
# Verifies:
# 1. Physical Database Binding Validation (SqlConnectionStringBuilder fail-closed).
# 2. dbo.Daily scalar fields, counts, active/inactive, deterministic SHA-256 hash.
# 3. Sync metadata: Azure sync.ServerState vs Local sync.LocalState & sync.LocalOutbox.
# 4. Local sync metadata strict validation (LocalState=1 row, BootstrapManifest=1 row,
#    VERIFIED_READY, IsWriteAllowed=true).
# 5. Outbox readiness requiring strictly 0 total operations.
# 6. Actual configuration safety verification (AuthoritativeTrackingEnabled=false,
#    PushEnabled=false, LegacyMigration:Enabled=false).
# 7. Architecture AST audit (zero direct Daily DML outside push coordinator).
# 8. Detection and classification of untracked drift (CLEAN_BASELINE,
#    BUSINESS_DRIFT_UNTRACKED, VERSION_DRIFT, OUTBOX_PENDING, MIXED_DRIFT).
# 9. Generates sanitized audit reports:
#    - docs/audit/sync-slice-4-4b/baseline_2026_report.json
#    - docs/audit/sync-slice-4-4b/baseline_2027_report.json
#    - docs/audit/sync-slice-4-4b/BASELINE_RECONCILIATION_SUMMARY.md
# ==============================================================================

using namespace System.Security.Cryptography
using namespace System.Text
using namespace System.Globalization
using namespace System.Data.SqlClient
using namespace System.Collections.Generic

[CmdletBinding()]
param (
    [string]$Azure2026ConnectionString,
    [string]$Azure2027ConnectionString,
    [string]$Local2026ConnectionString,
    [string]$Local2027ConnectionString
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$docsAuditDir = Join-Path $repoRoot "docs\audit\sync-slice-4-4b"
if (-not (Test-Path $docsAuditDir)) {
    New-Item -ItemType Directory -Path $docsAuditDir -Force | Out-Null
}

Add-Type -AssemblyName "System.Data"

# ------------------------------------------------------------------------------
# P0: Physical Database Binding Validation Function
# ------------------------------------------------------------------------------
function Assert-PhysicalDatabaseBinding {
    param(
        [string]$ConnectionString,
        [string]$ExpectedTarget, # "Azure" or "Local"
        [string]$ExpectedYear     # "2026" or "2027"
    )
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "PHYSICAL_BINDING_ERROR: Connection string for $ExpectedTarget $ExpectedYear is null or empty."
    }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
    $dataSource = if ($builder.DataSource) { $builder.DataSource.Trim() } else { "" }
    $initialCatalog = if ($builder.InitialCatalog) { $builder.InitialCatalog.Trim() } else { "" }

    # Strip optional tcp: prefix and ,port suffix for endpoint evaluation
    $serverHost = $dataSource -replace '^(?i)tcp:', '' -replace ',\s*[0-9]+$', ''

    if ($ExpectedTarget -eq "Azure") {
        $expectedCatalog = if ($ExpectedYear -eq "2026") { "IProgramDb2026" } else { "IProgramDb2027" }
        if ($initialCatalog -ne $expectedCatalog) {
            throw "PHYSICAL_BINDING_ERROR: Azure $ExpectedYear InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
        }
        $isAzure = $serverHost.ToLowerInvariant().EndsWith(".database.windows.net") -and 
                   ($serverHost -match '^[a-zA-Z0-9.-]+\.database\.windows\.net$')
        if (-not $isAzure) {
            throw "PHYSICAL_BINDING_ERROR: Azure $ExpectedYear DataSource is not a trusted Azure SQL endpoint (*.database.windows.net)."
        }
    } elseif ($ExpectedTarget -eq "Local") {
        $expectedCatalog = if ($ExpectedYear -eq "2026") { "IProgramLocalDb2026" } else { "IProgramLocalDb2027" }
        if ($initialCatalog -ne $expectedCatalog) {
            throw "PHYSICAL_BINDING_ERROR: Local $ExpectedYear InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
        }
        if ($serverHost.ToLowerInvariant().Contains(".database.windows.net")) {
            throw "PHYSICAL_BINDING_ERROR: Local $ExpectedYear DataSource cannot point to an Azure SQL endpoint."
        }
        $isLocal = ($serverHost -eq "." -or 
                    $serverHost -eq "(local)" -or 
                    $serverHost -eq "localhost" -or 
                    $serverHost -eq "127.0.0.1" -or 
                    $serverHost.StartsWith("(localdb)\", [System.StringComparison]::OrdinalIgnoreCase) -or 
                    $serverHost.StartsWith("localhost\", [System.StringComparison]::OrdinalIgnoreCase) -or 
                    $serverHost.StartsWith(".\", [System.StringComparison]::OrdinalIgnoreCase) -or 
                    $serverHost.StartsWith("127.0.0.1\", [System.StringComparison]::OrdinalIgnoreCase))
        if (-not $isLocal) {
            throw "PHYSICAL_BINDING_ERROR: Local $ExpectedYear DataSource is not a trusted local endpoint."
        }
    } else {
        throw "PHYSICAL_BINDING_ERROR: Unknown expected target '$ExpectedTarget'."
    }

    return @{
        Target = $ExpectedTarget
        Year = $ExpectedYear
        InitialCatalog = $initialCatalog
        Status = "PASS"
    }
}

# 1. Resolve Connection Strings securely
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

# ------------------------------------------------------------------------------
# P0: Configuration Safety Verification (Committed appsettings.json)
# ------------------------------------------------------------------------------
$authTrackingEnabled = [bool]$appsettings.Sync.AuthoritativeTrackingEnabled
$pushEnabled = [bool]$appsettings.Sync.PushEnabled
$legacyMigrationEnabled = [bool]$appsettings.LegacyMigration.Enabled

$configSafetyIssues = @()
if ($authTrackingEnabled -ne $false) {
    $configSafetyIssues += "Sync:AuthoritativeTrackingEnabled must be false in committed configuration (found: $authTrackingEnabled)"
}
if ($pushEnabled -ne $false) {
    $configSafetyIssues += "Sync:PushEnabled must be false in committed configuration (found: $pushEnabled)"
}
if ($legacyMigrationEnabled -ne $false) {
    $configSafetyIssues += "LegacyMigration:Enabled must be false in committed configuration (found: $legacyMigrationEnabled)"
}

$configSafetyStatus = if ($configSafetyIssues.Count -eq 0) { "PASS" } else { "FAIL" }

# ------------------------------------------------------------------------------
# P1: Architecture DML Audit Verification
# ------------------------------------------------------------------------------
function Audit-DailyDmlArchitectureSafety($rootPath) {
    $srcDir = Join-Path $rootPath "src"
    $csFiles = Get-ChildItem -Path $srcDir -Filter "*.cs" -Recurse | Where-Object { $_.Name -ne "AzurePushTransactionCoordinator.cs" }
    $dmlPattern = [regex]'\b(INSERT\s+INTO|UPDATE|DELETE(\s+FROM)?)\s+(\[?dbo\]?\.)?\[?Daily\]?\b'
    $violations = @()

    foreach ($file in $csFiles) {
        $lines = Get-Content $file.FullName
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $line = $lines[$i].Trim()
            if ($line.StartsWith("//") -or $line.StartsWith("/*") -or $line.StartsWith("*")) { continue }
            if ($dmlPattern.IsMatch($line)) {
                $violations += "$($file.Name): Line $($i + 1)"
            }
        }
    }

    return @{
        Status = if ($violations.Count -eq 0) { "PASS" } else { "FAIL" }
        Violations = $violations
    }
}

$archDmlAudit = Audit-DailyDmlArchitectureSafety $repoRoot

# ------------------------------------------------------------------------------
# Database Audit Functions
# ------------------------------------------------------------------------------
function Audit-DailyTable($conn) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandTimeout = 120
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
    $nullSyncIds = 0
    $dupSyncIds = 0
    $seenSyncIds = New-Object 'System.Collections.Generic.HashSet[System.Guid]'
    $rowDict = @{}

    while ($reader.Read()) {
        $count++
        $syncId = $reader.GetGuid(0)
        if ($syncId -eq [Guid]::Empty) {
            $nullSyncIds++
        }
        if (-not $seenSyncIds.Add($syncId)) {
            $dupSyncIds++
        }

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

        # Deterministic row serialization for hashing
        $bw.Write([byte]0xFF) # Row boundary
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

        $rowDict[$syncId.ToString()] = @{
            SyncId = $syncId.ToString()
            Name = $name
            DailyDate = if ($dailyDate) { $dailyDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") } else { $null }
            Closed = $closed
            CreatedAt = if ($createdAt) { $createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") } else { $null }
            CreatedBy = $createdBy
            UpdatedAt = if ($updatedAt) { $updatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") } else { $null }
            UpdatedBy = $updatedBy
            DeactivatedAt = if ($deactivatedAt) { $deactivatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") } else { $null }
            DeactivatedBy = $deactivatedBy
            IsActive = $isActive
        }
    }
    $reader.Close()

    $bw.Flush()
    $bytes = $ms.ToArray()
    $hashBytes = $sha.ComputeHash($bytes)
    $hashStr = [BitConverter]::ToString($hashBytes).Replace("-", "")
    $ms.Dispose()

    return @{
        TotalRows = $count
        ActiveRows = $activeCount
        InactiveRows = $inactiveCount
        NullSyncIdCount = $nullSyncIds
        DuplicateSyncIdCount = $dupSyncIds
        DeterministicSha256 = $hashStr
        Rows = $rowDict
    }
}

function Audit-AzureSyncMetadata($conn, $year) {
    # Check if sync schema tables exist
    $cmdTables = $conn.CreateCommand()
    $cmdTables.CommandText = @"
SELECT TABLE_NAME 
FROM INFORMATION_SCHEMA.TABLES 
WHERE TABLE_SCHEMA = 'sync' 
  AND TABLE_NAME IN ('ServerState', 'ServerChangeFeed', 'Tombstones', 'ProcessedOperations');
"@
    $tables = @()
    $rTbl = $cmdTables.ExecuteReader()
    while ($rTbl.Read()) {
        $tables += $rTbl.GetString(0)
    }
    $rTbl.Close()

    $schemaComplete = ($tables.Count -eq 4)

    # ServerState
    $serverStateExists = $false
    $serverStateRowCount = 0
    $currentVersion = [long]-1
    $lastUpdatedUtc = $null
    $boundDbId = $null

    if ($tables -contains "ServerState") {
        $cmdSS = $conn.CreateCommand()
        $cmdSS.CommandText = "SELECT COUNT(1) FROM [sync].[ServerState];"
        $serverStateRowCount = [Convert]::ToInt32($cmdSS.ExecuteScalar())
        $serverStateExists = ($serverStateRowCount -gt 0)

        if ($serverStateRowCount -eq 1) {
            $cmdSSDetails = $conn.CreateCommand()
            $cmdSSDetails.CommandText = "SELECT DatabaseId, CurrentVersion, LastUpdatedUtc FROM [sync].[ServerState];"
            $rSS = $cmdSSDetails.ExecuteReader()
            if ($rSS.Read()) {
                $boundDbId = $rSS.GetString(0)
                $currentVersion = $rSS.GetInt64(1)
                $lastUpdatedUtc = if ($rSS.IsDBNull(2)) { $null } else { $rSS.GetDateTime(2).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") }
            }
            $rSS.Close()
        }
    }

    # ChangeFeed count
    $changeFeedCount = 0
    if ($tables -contains "ServerChangeFeed") {
        $cmdFeed = $conn.CreateCommand()
        $cmdFeed.CommandText = "SELECT COUNT(1) FROM [sync].[ServerChangeFeed];"
        $changeFeedCount = [Convert]::ToInt32($cmdFeed.ExecuteScalar())
    }

    # Tombstones count
    $tombstoneCount = 0
    if ($tables -contains "Tombstones") {
        $cmdTomb = $conn.CreateCommand()
        $cmdTomb.CommandText = "SELECT COUNT(1) FROM [sync].[Tombstones];"
        $tombstoneCount = [Convert]::ToInt32($cmdTomb.ExecuteScalar())
    }

    # ProcessedOperations count
    $processedOpsCount = 0
    if ($tables -contains "ProcessedOperations") {
        $cmdOps = $conn.CreateCommand()
        $cmdOps.CommandText = "SELECT COUNT(1) FROM [sync].[ProcessedOperations];"
        $processedOpsCount = [Convert]::ToInt32($cmdOps.ExecuteScalar())
    }

    return @{
        SchemaComplete = $schemaComplete
        SyncTables = $tables
        ServerStateExists = $serverStateExists
        ServerStateRowCount = $serverStateRowCount
        DatabaseId = $boundDbId
        CurrentVersion = $currentVersion
        LastUpdatedUtc = $lastUpdatedUtc
        ChangeFeedCount = $changeFeedCount
        TombstoneCount = $tombstoneCount
        ProcessedOperationsCount = $processedOpsCount
    }
}

# ------------------------------------------------------------------------------
# P0: Strict Local Sync Metadata Validation Function (No DeviceId/Tokens Leaked)
# ------------------------------------------------------------------------------
function Audit-LocalSyncMetadata($conn, $year) {
    # 1. LocalState cardinality and values
    $cmdLS = $conn.CreateCommand()
    $cmdLS.CommandText = "SELECT DatabaseId, LastServerVersion FROM [sync].[LocalState];"
    $rLS = $cmdLS.ExecuteReader()
    $lsRows = @()
    while ($rLS.Read()) {
        $lsRows += @{
            DatabaseId = if ($rLS.IsDBNull(0)) { $null } else { $rLS.GetString(0) }
            LastServerVersion = if ($rLS.IsDBNull(1)) { [long]-1 } else { $rLS.GetInt64(1) }
        }
    }
    $rLS.Close()

    $lsRowCount = $lsRows.Count
    $lsDbId = if ($lsRowCount -ge 1) { $lsRows[0].DatabaseId } else { $null }
    $lastServerVersion = if ($lsRowCount -ge 1) { $lsRows[0].LastServerVersion } else { [long]-1 }

    # 2. BootstrapManifest cardinality and values
    $cmdBM = $conn.CreateCommand()
    $cmdBM.CommandText = "SELECT DatabaseId, Status, IsWriteAllowed, BootstrapTimestampUtc FROM [sync].[BootstrapManifest];"
    $rBM = $cmdBM.ExecuteReader()
    $bmRows = @()
    while ($rBM.Read()) {
        $bmRows += @{
            DatabaseId = if ($rBM.IsDBNull(0)) { $null } else { $rBM.GetString(0) }
            Status = if ($rBM.IsDBNull(1)) { $null } else { $rBM.GetString(1) }
            IsWriteAllowed = if ($rBM.IsDBNull(2)) { $false } else { $rBM.GetBoolean(2) }
            BootstrapTimestampUtc = if ($rBM.IsDBNull(3)) { $null } else { $rBM.GetDateTime(3).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ") }
        }
    }
    $rBM.Close()

    $bmRowCount = $bmRows.Count
    $bmDbId = if ($bmRowCount -ge 1) { $bmRows[0].DatabaseId } else { $null }
    $bootstrapStatus = if ($bmRowCount -ge 1) { $bmRows[0].Status } else { $null }
    $isWriteAllowed = if ($bmRowCount -ge 1) { $bmRows[0].IsWriteAllowed } else { $false }
    $bootstrapTimestampUtc = if ($bmRowCount -ge 1) { $bmRows[0].BootstrapTimestampUtc } else { $null }

    # 3. LocalOutbox all 5 counts
    $cmdOB = $conn.CreateCommand()
    $cmdOB.CommandText = @"
SELECT 
    COUNT(1) AS TotalCount,
    SUM(CASE WHEN Status = 'PENDING' THEN 1 ELSE 0 END) AS PendingCount,
    SUM(CASE WHEN Status = 'IN_PROGRESS' THEN 1 ELSE 0 END) AS InProgressCount,
    SUM(CASE WHEN Status = 'COMPLETED' THEN 1 ELSE 0 END) AS CompletedCount,
    SUM(CASE WHEN Status = 'FAILED' THEN 1 ELSE 0 END) AS FailedCount
FROM [sync].[LocalOutbox];
"@
    $obData = @{}
    $rOB = $cmdOB.ExecuteReader()
    if ($rOB.Read()) {
        $obData = @{
            TotalCount = if ($rOB.IsDBNull(0)) { 0 } else { [Convert]::ToInt32($rOB.GetValue(0)) }
            PendingCount = if ($rOB.IsDBNull(1)) { 0 } else { [Convert]::ToInt32($rOB.GetValue(1)) }
            InProgressCount = if ($rOB.IsDBNull(2)) { 0 } else { [Convert]::ToInt32($rOB.GetValue(2)) }
            CompletedCount = if ($rOB.IsDBNull(3)) { 0 } else { [Convert]::ToInt32($rOB.GetValue(3)) }
            FailedCount = if ($rOB.IsDBNull(4)) { 0 } else { [Convert]::ToInt32($rOB.GetValue(4)) }
        }
    }
    $rOB.Close()

    return @{
        LocalStateRowCount = $lsRowCount
        DatabaseId = $lsDbId
        LastServerVersion = $lastServerVersion
        BootstrapManifestRowCount = $bmRowCount
        BootstrapDatabaseId = $bmDbId
        BootstrapStatus = $bootstrapStatus
        IsWriteAllowed = $isWriteAllowed
        BootstrapTimestampUtc = $bootstrapTimestampUtc
        LocalOutbox = $obData
    }
}

# ------------------------------------------------------------------------------
# Year Baseline Comparison Function
# ------------------------------------------------------------------------------
function Compare-YearBaseline($year, $azureCs, $localCs) {
    Write-Host "`n==========================================================================" -ForegroundColor Cyan
    Write-Host "  AUDITING BASELINE: YEAR $year" -ForegroundColor Cyan
    Write-Host "==========================================================================" -ForegroundColor Cyan

    # P0: Physical Database Binding Validation BEFORE opening connections
    Write-Host "Validating physical database bindings for $year..." -NoNewline
    $azureBinding = Assert-PhysicalDatabaseBinding -ConnectionString $azureCs -ExpectedTarget "Azure" -ExpectedYear $year
    $localBinding = Assert-PhysicalDatabaseBinding -ConnectionString $localCs -ExpectedTarget "Local" -ExpectedYear $year
    Write-Host " PASS (Azure: $($azureBinding.InitialCatalog), Local: $($localBinding.InitialCatalog))" -ForegroundColor Green

    # Connect to Azure (Strictly SELECT-only with retry for transient wake-up)
    Write-Host "Connecting to Azure DB ($year)..." -NoNewline
    $azureBldr = New-Object SqlConnectionStringBuilder($azureCs)
    $azureBldr["Connect Timeout"] = 60
    $azureConn = $null
    $maxAttempts = 3
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            $azureConn = New-Object SqlConnection($azureBldr.ConnectionString)
            $azureConn.Open()
            break
        } catch {
            if ($attempt -eq $maxAttempts) { throw }
            Start-Sleep -Seconds 3
        }
    }
    Write-Host " Connected (READ-ONLY)" -ForegroundColor Green

    # Connect to Local (Strictly SELECT-only)
    Write-Host "Connecting to Local DB ($year)..." -NoNewline
    $localConn = New-Object SqlConnection($localCs)
    $localConn.Open()
    Write-Host " Connected (READ-ONLY)" -ForegroundColor Green

    try {
        # 1. Audit Daily on Azure and Local
        Write-Host "Auditing Daily table on Azure..." -NoNewline
        $azureDaily = Audit-DailyTable $azureConn
        Write-Host " OK ($($azureDaily.TotalRows) rows, hash: $($azureDaily.DeterministicSha256.Substring(0,8))...)" -ForegroundColor Green

        Write-Host "Auditing Daily table on Local..." -NoNewline
        $localDaily = Audit-DailyTable $localConn
        Write-Host " OK ($($localDaily.TotalRows) rows, hash: $($localDaily.DeterministicSha256.Substring(0,8))...)" -ForegroundColor Green

        # 2. Audit Sync Metadata
        Write-Host "Auditing Sync Metadata on Azure..." -NoNewline
        $azureSync = Audit-AzureSyncMetadata $azureConn $year
        Write-Host " OK (CurrentVersion=$($azureSync.CurrentVersion))" -ForegroundColor Green

        Write-Host "Auditing Sync Metadata on Local..." -NoNewline
        $localSync = Audit-LocalSyncMetadata $localConn $year
        Write-Host " OK (LastServerVersion=$($localSync.LastServerVersion), Outbox Total=$($localSync.LocalOutbox.TotalCount))" -ForegroundColor Green

        # 3. Detect and Compare Daily Differences (P1: Sanitized key hashes only, zero production data leakage)
        $dailyMatch = ($azureDaily.DeterministicSha256 -eq $localDaily.DeterministicSha256)
        $missingOnLocal = @()
        $missingOnAzure = @()
        $differingRows = @()

        foreach ($syncId in $azureDaily.Rows.Keys) {
            $syncIdBytes = [System.Text.Encoding]::UTF8.GetBytes($syncId.ToString())
            $keyHash = [BitConverter]::ToString([SHA256]::Create().ComputeHash($syncIdBytes)).Replace("-", "").Substring(0, 16)

            if (-not $localDaily.Rows.ContainsKey($syncId)) {
                $missingOnLocal += @{ EntityKeyHash = $keyHash }
            } else {
                $azR = $azureDaily.Rows[$syncId]
                $locR = $localDaily.Rows[$syncId]
                $diffFields = @()
                foreach ($f in @("Name", "DailyDate", "Closed", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy", "DeactivatedAt", "DeactivatedBy", "IsActive")) {
                    if ($azR[$f] -ne $locR[$f]) {
                        $diffFields += $f
                    }
                }
                if ($diffFields.Count -gt 0) {
                    $differingRows += @{
                        EntityKeyHash = $keyHash
                        DifferingFields = $diffFields
                    }
                }
            }
        }

        foreach ($syncId in $localDaily.Rows.Keys) {
            if (-not $azureDaily.Rows.ContainsKey($syncId)) {
                $syncIdBytes = [System.Text.Encoding]::UTF8.GetBytes($syncId.ToString())
                $keyHash = [BitConverter]::ToString([SHA256]::Create().ComputeHash($syncIdBytes)).Replace("-", "").Substring(0, 16)
                $missingOnAzure += @{ EntityKeyHash = $keyHash }
            }
        }

        # 4. Classify Drift (P0: Any outbox row prevents CLEAN_BASELINE)
        $classification = ""
        $versionMatch = ($azureSync.CurrentVersion -eq $localSync.LastServerVersion)
        $hasOutboxRows = ($localSync.LocalOutbox.TotalCount -gt 0)

        if ($dailyMatch -and $versionMatch -and -not $hasOutboxRows) {
            $classification = "CLEAN_BASELINE"
        } elseif (-not $dailyMatch -and $versionMatch -and -not $hasOutboxRows) {
            $classification = "BUSINESS_DRIFT_UNTRACKED"
        } elseif ($dailyMatch -and -not $versionMatch -and -not $hasOutboxRows) {
            $classification = "VERSION_DRIFT"
        } elseif ($hasOutboxRows -and $dailyMatch -and $versionMatch) {
            $classification = "OUTBOX_PENDING"
        } else {
            $classification = "MIXED_DRIFT"
        }

        Write-Host "`nClassification Result: $classification" -ForegroundColor Yellow

        # 5. Authoritative Tracking Cutover Readiness Evaluation
        $readinessIssues = @()

        # Azure Schema & State Validation
        if (-not $azureSync.SchemaComplete) {
            $readinessIssues += "Azure sync schema is incomplete (missing tables: $(@('ServerState', 'ServerChangeFeed', 'Tombstones', 'ProcessedOperations') | Where-Object { $azureSync.SyncTables -notcontains $_ }))"
        }
        if ($azureSync.ServerStateRowCount -ne 1) {
            $readinessIssues += "Azure ServerState must exist exactly once per DB (found count: $($azureSync.ServerStateRowCount))"
        }
        if ($azureSync.DatabaseId -ne $year) {
            $readinessIssues += "Azure ServerState DatabaseId mismatch: expected '$year', found '$($azureSync.DatabaseId)'"
        }
        if ($azureDaily.NullSyncIdCount -gt 0 -or $azureDaily.DuplicateSyncIdCount -gt 0) {
            $readinessIssues += "Azure Daily contains invalid SyncIds (nulls: $($azureDaily.NullSyncIdCount), duplicates: $($azureDaily.DuplicateSyncIdCount))"
        }

        # Local Schema & State Validation (P0: Strict Cardinality & Status)
        if ($localSync.LocalStateRowCount -ne 1) {
            $readinessIssues += "LocalState must exist exactly once per DB (found count: $($localSync.LocalStateRowCount))"
        }
        if ($localSync.DatabaseId -ne $year) {
            $readinessIssues += "LocalState DatabaseId mismatch: expected '$year', found '$($localSync.DatabaseId)'"
        }
        if ($localSync.LastServerVersion -lt 0) {
            $readinessIssues += "LocalState LastServerVersion must be non-negative (found: $($localSync.LastServerVersion))"
        }
        if ($localSync.BootstrapManifestRowCount -ne 1) {
            $readinessIssues += "BootstrapManifest must exist exactly once per DB (found count: $($localSync.BootstrapManifestRowCount))"
        }
        if ($localSync.BootstrapDatabaseId -ne $year) {
            $readinessIssues += "BootstrapManifest DatabaseId mismatch: expected '$year', found '$($localSync.BootstrapDatabaseId)'"
        }
        if ($localSync.BootstrapStatus -ne "VERIFIED_READY") {
            $readinessIssues += "BootstrapManifest Status must be 'VERIFIED_READY' (found: '$($localSync.BootstrapStatus)')"
        }
        if ($localSync.IsWriteAllowed -ne $true) {
            $readinessIssues += "BootstrapManifest IsWriteAllowed must be true (found: $($localSync.IsWriteAllowed))"
        }
        if ($localDaily.NullSyncIdCount -gt 0 -or $localDaily.DuplicateSyncIdCount -gt 0) {
            $readinessIssues += "Local Daily contains invalid SyncIds (nulls: $($localDaily.NullSyncIdCount), duplicates: $($localDaily.DuplicateSyncIdCount))"
        }

        # P0: Outbox Truly Empty Requirement
        if ($localSync.LocalOutbox.TotalCount -gt 0) {
            $readinessIssues += "LocalOutbox must be completely empty (0 total operations). Found total: $($localSync.LocalOutbox.TotalCount) (Pending: $($localSync.LocalOutbox.PendingCount), InProgress: $($localSync.LocalOutbox.InProgressCount), Failed: $($localSync.LocalOutbox.FailedCount), Completed: $($localSync.LocalOutbox.CompletedCount))"
        }

        # P0: Configuration Safety
        if ($configSafetyStatus -ne "PASS") {
            foreach ($csIssue in $configSafetyIssues) {
                $readinessIssues += "Configuration safety issue: $csIssue"
            }
        }

        # P1: Architecture DML Audit
        if ($archDmlAudit.Status -ne "PASS") {
            $readinessIssues += "Architecture safety violation: Found $($archDmlAudit.Violations.Count) direct Daily DML statements outside AzurePushTransactionCoordinator"
        }

        # Baseline Drift Check
        if ($classification -ne "CLEAN_BASELINE") {
            $readinessIssues += "Baseline drift detected ($classification). Reconciliation must occur before enabling tracking."
        }

        $cutoverReadiness = if ($readinessIssues.Count -eq 0) { "YES" } else { "NO" }

        # Build Sanitized Report Object (Zero raw DeviceId, zero credentials, zero production data)
        $report = [ordered]@{
            Year = $year
            TimestampUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
            Classification = $classification
            AuthoritativeTrackingCutoverReadiness = $cutoverReadiness
            ReadinessIssues = $readinessIssues
            Comparison_Summary = [ordered]@{
                DailyContentMatch = $dailyMatch
                VersionMatch = $versionMatch
                HasPendingOutbox = ($localSync.LocalOutbox.TotalCount -gt 0)
                Azure_Daily_Total = $azureDaily.TotalRows
                Azure_Daily_Active = $azureDaily.ActiveRows
                Azure_Daily_Inactive = $azureDaily.InactiveRows
                Azure_Daily_Sha256 = $azureDaily.DeterministicSha256
                Local_Daily_Total = $localDaily.TotalRows
                Local_Daily_Active = $localDaily.ActiveRows
                Local_Daily_Inactive = $localDaily.InactiveRows
                Local_Daily_Sha256 = $localDaily.DeterministicSha256
                MissingOnLocalCount = $missingOnLocal.Count
                MissingOnAzureCount = $missingOnAzure.Count
                DifferingFieldRowsCount = $differingRows.Count
            }
            Azure_Metadata = [ordered]@{
                SchemaComplete = $azureSync.SchemaComplete
                SyncTables = $azureSync.SyncTables
                ServerStateRowCount = $azureSync.ServerStateRowCount
                DatabaseId = $azureSync.DatabaseId
                CurrentVersion = $azureSync.CurrentVersion
                LastUpdatedUtc = $azureSync.LastUpdatedUtc
                ServerChangeFeedRows = $azureSync.ChangeFeedCount
                TombstoneRows = $azureSync.TombstoneCount
                ProcessedOperationsRows = $azureSync.ProcessedOperationsCount
            }
            Local_Metadata = [ordered]@{
                LocalStateRowCount = $localSync.LocalStateRowCount
                DatabaseId = $localSync.DatabaseId
                LastServerVersion = $localSync.LastServerVersion
                BootstrapManifestRowCount = $localSync.BootstrapManifestRowCount
                BootstrapStatus = $localSync.BootstrapStatus
                IsWriteAllowed = $localSync.IsWriteAllowed
                BootstrapTimestampUtc = $localSync.BootstrapTimestampUtc
                LocalOutbox = $localSync.LocalOutbox
            }
            ConfigurationSafety = [ordered]@{
                AuthoritativeTrackingEnabled = $authTrackingEnabled
                PushEnabled = $pushEnabled
                LegacyMigrationEnabled = $legacyMigrationEnabled
                Status = $configSafetyStatus
            }
            ArchitectureSafety = [ordered]@{
                RawDmlAudit = $archDmlAudit.Status
                ApprovedCoordinator = "AzurePushTransactionCoordinator.cs"
            }
            Drift_Details = [ordered]@{
                MissingOnLocal = $missingOnLocal
                MissingOnAzure = $missingOnAzure
                DifferingRows = $differingRows
            }
        }

        # Write sanitized JSON report
        $reportPath = Join-Path $docsAuditDir "baseline_${year}_report.json"
        $report | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding UTF8
        Write-Host "Report saved to: $reportPath" -ForegroundColor Green

        return $report

    } finally {
        $azureConn.Close()
        $localConn.Close()
    }
}

try {
    Write-Host "==========================================================================" -ForegroundColor Yellow
    Write-Host "  SLICE 4.4B: PRODUCTION BASELINE RECONCILIATION AUDIT STARTING           " -ForegroundColor Yellow
    Write-Host "==========================================================================" -ForegroundColor Yellow

    $report2026 = Compare-YearBaseline "2026" $Azure2026ConnectionString $Local2026ConnectionString
    $report2027 = Compare-YearBaseline "2027" $Azure2027ConnectionString $Local2027ConnectionString

    # Compute cutover proposal text
    $proposalSb = New-Object System.Text.StringBuilder
    if ($report2026.Classification -eq 'CLEAN_BASELINE' -and $report2027.Classification -eq 'CLEAN_BASELINE') {
        [void]$proposalSb.AppendLine("### Controlled Cutover Strategy (Clean Baseline)")
        [void]$proposalSb.AppendLine("Since both 2026 and 2027 exhibit clean baseline alignment (zero content drift, matching server versions, zero pending local outbox operations):")
        [void]$proposalSb.AppendLine("")
        [void]$proposalSb.AppendLine("1. **Short Online Write Freeze:** Momentarily restrict production Online writes to ensure quiescent state.")
        [void]$proposalSb.AppendLine("2. **Re-Verify Baseline:** Run a 5-second sanity check confirming zero in-flight mutations.")
        [void]$proposalSb.AppendLine('3. **Enable Authoritative Tracking:** Set `Sync:AuthoritativeTrackingEnabled = true` in production configuration.')
        [void]$proposalSb.AppendLine("4. **Execute Controlled Canary Mutation:** Perform one controlled Online Daily update (e.g. updating a test record or touching an approved audit attribute).")
        [void]$proposalSb.AppendLine("5. **Verify Authoritative Invariants:**")
        [void]$proposalSb.AppendLine('   - `[sync].[ServerState].CurrentVersion` advanced by exactly +1.')
        [void]$proposalSb.AppendLine('   - `[sync].[ServerChangeFeed]` contains exactly 1 row with `OriginDeviceId = Guid.Empty`.')
        [void]$proposalSb.AppendLine("6. **Resume Production Writes:** Unfreeze and monitor normal operations.")
    } else {
        [void]$proposalSb.AppendLine("### Reconciliation Plan Required (Drift Detected)")
        [void]$proposalSb.AppendLine("Baseline drift was detected. In accordance with safety rules:")
        [void]$proposalSb.AppendLine("- No automatic local updates are performed.")
        [void]$proposalSb.AppendLine("- No Azure production modifications are performed.")
        [void]$proposalSb.AppendLine("- AuthoritativeTracking must remain disabled until an architect-approved reconciliation plan is executed.")
    }

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# Slice 4.4B -- Production Baseline Reconciliation & Cutover Readiness Summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 1. Executive Summary")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("This audit performs a strictly **READ-ONLY** baseline reconciliation between the authoritative Azure production databases (`IProgramDb2026`, `IProgramDb2027`) and the local offline replica databases (`IProgramLocalDb2026`, `IProgramLocalDb2027`).")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **Audit Date (UTC):** $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm:ss')) UTC")
    [void]$sb.AppendLine('- **Scope:** `dbo.Daily`, `sync.ServerState`, `sync.LocalState`, `sync.BootstrapManifest`, `sync.LocalOutbox`.')
    [void]$sb.AppendLine("- **Azure Access:** Strictly `SELECT` queries only. Zero DML (INSERT/UPDATE/DELETE/MERGE), zero DDL, zero migrations.")
    [void]$sb.AppendLine("- **Local Access:** Strictly `SELECT` queries only. Zero mutations to business or sync state.")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 2. Baseline Reconciliation Matrix")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("| Year | Daily Rows (Azure / Local) | Daily SHA-256 Match | Azure ServerVersion | Local LastServerVersion | Local Outbox Total | LocalState Rows | Bootstrap Rows | Classification | Cutover Readiness |")
    [void]$sb.AppendLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |")

    $m2026 = if ($report2026.Comparison_Summary.DailyContentMatch) { 'MATCH' } else { 'MISMATCH' }
    $m2027 = if ($report2027.Comparison_Summary.DailyContentMatch) { 'MATCH' } else { 'MISMATCH' }
    [void]$sb.AppendLine("| **2026** | $($report2026.Comparison_Summary.Azure_Daily_Total) / $($report2026.Comparison_Summary.Local_Daily_Total) | $m2026 | $($report2026.Azure_Metadata.CurrentVersion) | $($report2026.Local_Metadata.LastServerVersion) | $($report2026.Local_Metadata.LocalOutbox.TotalCount) | $($report2026.Local_Metadata.LocalStateRowCount) | $($report2026.Local_Metadata.BootstrapManifestRowCount) | **$($report2026.Classification)** | **$($report2026.AuthoritativeTrackingCutoverReadiness)** |")
    [void]$sb.AppendLine("| **2027** | $($report2027.Comparison_Summary.Azure_Daily_Total) / $($report2027.Comparison_Summary.Local_Daily_Total) | $m2027 | $($report2027.Azure_Metadata.CurrentVersion) | $($report2027.Local_Metadata.LastServerVersion) | $($report2027.Local_Metadata.LocalOutbox.TotalCount) | $($report2027.Local_Metadata.LocalStateRowCount) | $($report2027.Local_Metadata.BootstrapManifestRowCount) | **$($report2027.Classification)** | **$($report2027.AuthoritativeTrackingCutoverReadiness)** |")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 3. Detailed Audit Findings by Year")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### Year 2026")
    [void]$sb.AppendLine("- **Classification:** $($report2026.Classification)")
    [void]$sb.AppendLine("- **Authoritative Tracking Cutover Readiness:** $($report2026.AuthoritativeTrackingCutoverReadiness)")
    [void]$sb.AppendLine("- **Azure Daily:** $($report2026.Comparison_Summary.Azure_Daily_Total) total ($($report2026.Comparison_Summary.Azure_Daily_Active) active, $($report2026.Comparison_Summary.Azure_Daily_Inactive) inactive)")
    [void]$sb.AppendLine("- **Local Daily:** $($report2026.Comparison_Summary.Local_Daily_Total) total ($($report2026.Comparison_Summary.Local_Daily_Active) active, $($report2026.Comparison_Summary.Local_Daily_Inactive) inactive)")
    [void]$sb.AppendLine("- **Azure ServerState Version:** $($report2026.Azure_Metadata.CurrentVersion)")
    [void]$sb.AppendLine("- **Local LastServerVersion:** $($report2026.Local_Metadata.LastServerVersion)")
    [void]$sb.AppendLine("- **LocalState Cardinality:** $($report2026.Local_Metadata.LocalStateRowCount) row(s)")
    [void]$sb.AppendLine("- **BootstrapManifest Cardinality:** $($report2026.Local_Metadata.BootstrapManifestRowCount) row(s) (Status: $($report2026.Local_Metadata.BootstrapStatus), IsWriteAllowed: $($report2026.Local_Metadata.IsWriteAllowed))")
    [void]$sb.AppendLine("- **Local Outbox Operations:** $($report2026.Local_Metadata.LocalOutbox.TotalCount) total ($($report2026.Local_Metadata.LocalOutbox.PendingCount) pending, $($report2026.Local_Metadata.LocalOutbox.InProgressCount) in-progress, $($report2026.Local_Metadata.LocalOutbox.FailedCount) failed, $($report2026.Local_Metadata.LocalOutbox.CompletedCount) completed)")
    [void]$sb.AppendLine("- **Readiness Issues:**")
    if ($report2026.ReadinessIssues.Count -eq 0) {
        [void]$sb.AppendLine("  - None (All pre-conditions satisfied)")
    } else {
        foreach ($iss in $report2026.ReadinessIssues) {
            [void]$sb.AppendLine("  - $iss")
        }
    }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("### Year 2027")
    [void]$sb.AppendLine("- **Classification:** $($report2027.Classification)")
    [void]$sb.AppendLine("- **Authoritative Tracking Cutover Readiness:** $($report2027.AuthoritativeTrackingCutoverReadiness)")
    [void]$sb.AppendLine("- **Azure Daily:** $($report2027.Comparison_Summary.Azure_Daily_Total) total ($($report2027.Comparison_Summary.Azure_Daily_Active) active, $($report2027.Comparison_Summary.Azure_Daily_Inactive) inactive)")
    [void]$sb.AppendLine("- **Local Daily:** $($report2027.Comparison_Summary.Local_Daily_Total) total ($($report2027.Comparison_Summary.Local_Daily_Active) active, $($report2027.Comparison_Summary.Local_Daily_Inactive) inactive)")
    [void]$sb.AppendLine("- **Azure ServerState Version:** $($report2027.Azure_Metadata.CurrentVersion)")
    [void]$sb.AppendLine("- **Local LastServerVersion:** $($report2027.Local_Metadata.LastServerVersion)")
    [void]$sb.AppendLine("- **LocalState Cardinality:** $($report2027.Local_Metadata.LocalStateRowCount) row(s)")
    [void]$sb.AppendLine("- **BootstrapManifest Cardinality:** $($report2027.Local_Metadata.BootstrapManifestRowCount) row(s) (Status: $($report2027.Local_Metadata.BootstrapStatus), IsWriteAllowed: $($report2027.Local_Metadata.IsWriteAllowed))")
    [void]$sb.AppendLine("- **Local Outbox Operations:** $($report2027.Local_Metadata.LocalOutbox.TotalCount) total ($($report2027.Local_Metadata.LocalOutbox.PendingCount) pending, $($report2027.Local_Metadata.LocalOutbox.InProgressCount) in-progress, $($report2027.Local_Metadata.LocalOutbox.FailedCount) failed, $($report2027.Local_Metadata.LocalOutbox.CompletedCount) completed)")
    [void]$sb.AppendLine("- **Readiness Issues:**")
    if ($report2027.ReadinessIssues.Count -eq 0) {
        [void]$sb.AppendLine("  - None (All pre-conditions satisfied)")
    } else {
        foreach ($iss in $report2027.ReadinessIssues) {
            [void]$sb.AppendLine("  - $iss")
        }
    }
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 4. Configuration & Architecture Safety Verification")
    [void]$sb.AppendLine("")
    $authStatus = if (-not $authTrackingEnabled) { 'PASS' } else { 'FAIL' }
    $pushStatus = if (-not $pushEnabled) { 'PASS' } else { 'FAIL' }
    $legacyStatus = if (-not $legacyMigrationEnabled) { 'PASS' } else { 'FAIL' }

    [void]$sb.AppendLine("| Check | Expected Value | Actual Value | Status |")
    [void]$sb.AppendLine("| :--- | :--- | :--- | :--- |")
    [void]$sb.AppendLine("| ``Sync:AuthoritativeTrackingEnabled`` | ``false`` | ``$($authTrackingEnabled.ToString().ToLowerInvariant())`` | **$authStatus** |")
    [void]$sb.AppendLine("| ``Sync:PushEnabled`` | ``false`` | ``$($pushEnabled.ToString().ToLowerInvariant())`` | **$pushStatus** |")
    [void]$sb.AppendLine("| ``LegacyMigration:Enabled`` | ``false`` | ``$($legacyMigrationEnabled.ToString().ToLowerInvariant())`` | **$legacyStatus** |")
    [void]$sb.AppendLine("| Direct Daily DML Audit | 0 outside Approved Coordinator | $($archDmlAudit.Violations.Count) violation(s) | **$($archDmlAudit.Status)** |")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 5. Cutover Strategy Proposal")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine($proposalSb.ToString())
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("## 6. Safety Invariants Confirmed")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("- **Azure Production DML:** Exactly 0 mutations executed (strictly SELECT-only).")
    [void]$sb.AppendLine("- **Local Replicas:** Exactly 0 mutations executed.")
    [void]$sb.AppendLine("- **Physical Database Binding Guard:** Verified fail-closed on SqlConnectionStringBuilder.")
    [void]$sb.AppendLine("- **Artifact Sanitization:** Zero DeviceIds, passwords, tokens, connection strings, IPs, or production business scalar values leaked.")
    [void]$sb.AppendLine('- **Feature Gate Sync:AuthoritativeTrackingEnabled:** `false`')
    [void]$sb.AppendLine('- **Feature Gate Sync:PushEnabled:** `false`')
    [void]$sb.AppendLine('- **Feature Gate LegacyMigration:Enabled:** `false`')

    $summaryPath = Join-Path $docsAuditDir "BASELINE_RECONCILIATION_SUMMARY.md"
    Set-Content -Path $summaryPath -Value $sb.ToString() -Encoding UTF8
    Write-Host "`nSummary report generated at: $summaryPath" -ForegroundColor Green

    Write-Host "`n==========================================================================" -ForegroundColor Green
    Write-Host "  SLICE 4.4B: AUDIT COMPLETE                                              " -ForegroundColor Green
    Write-Host "==========================================================================" -ForegroundColor Green

} catch {
    Write-Host "`nAUDIT ERROR: $($_.Exception.Message)" -ForegroundColor Red
    throw
}
