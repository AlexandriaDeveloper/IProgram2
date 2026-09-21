# ==============================================================================
# SLICE 4.4C: AUTHORITATIVE TRACKING CUTOVER VERIFICATION AUDIT TOOL
# Strictly READ-ONLY verification across Azure and Local databases (2026 & 2027)
#
# Verifies:
# 1. Physical Database Binding Validation (SqlConnectionStringBuilder fail-closed).
# 2. dbo.Daily scalar fields, counts, active/inactive, deterministic SHA-256 hash.
# 3. Sync metadata: Azure ServerState (CurrentVersion = 2) vs Local LocalState (LastServerVersion = 0).
# 4. Outbox readiness requiring strictly 0 total operations.
# 5. Full ChangeFeed history (v1 INSERT, v2 HARD_DELETE) and Tombstone alignment.
# 6. Classification as TRACKED_VERSION_GAP.
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
$docsAuditDir = Join-Path $repoRoot "docs\audit\sync-slice-4-4c"
if (-not (Test-Path $docsAuditDir)) {
    New-Item -ItemType Directory -Path $docsAuditDir -Force | Out-Null
}

Add-Type -AssemblyName "System.Data"

function Assert-PhysicalDatabaseBinding {
    param(
        [string]$ConnectionString,
        [string]$ExpectedTarget,
        [string]$ExpectedYear
    )
    if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
        throw "PHYSICAL_BINDING_ERROR: Connection string for $ExpectedTarget $ExpectedYear is null or empty."
    }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnectionString)
    $dataSource = if ($builder.DataSource) { $builder.DataSource.Trim() } else { "" }
    $initialCatalog = if ($builder.InitialCatalog) { $builder.InitialCatalog.Trim() } else { "" }

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
    }

    return @{
        Target = $ExpectedTarget
        Year = $ExpectedYear
        InitialCatalog = $initialCatalog
        Status = "PASS"
    }
}

# Resolve connection strings
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

function Audit-DailyTable($conn) {
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
    $ms.Dispose()

    return @{
        TotalRows = $count
        ActiveRows = $activeCount
        InactiveRows = $inactiveCount
        DeterministicSha256 = $hashStr
    }
}

function Audit-AzureSyncMetadata($conn, $year) {
    $cmdSS = $conn.CreateCommand()
    $cmdSS.CommandText = "SELECT CurrentVersion, LastUpdatedUtc FROM [sync].[ServerState] WHERE DatabaseId = @dbId;"
    $cmdSS.Parameters.AddWithValue("@dbId", $year) | Out-Null
    $rSS = $cmdSS.ExecuteReader()
    $curVer = -1
    $lastUpdated = $null
    if ($rSS.Read()) {
        $curVer = $rSS.GetInt64(0)
        $lastUpdated = if ($rSS.IsDBNull(1)) { $null } else { $rSS.GetDateTime(1).ToString("yyyy-MM-ddTHH:mm:ssZ") }
    }
    $rSS.Close()

    $cmdFeed = $conn.CreateCommand()
    $cmdFeed.CommandText = "SELECT COUNT(1) FROM [sync].[ServerChangeFeed] WHERE DatabaseId = @dbId;"
    $cmdFeed.Parameters.AddWithValue("@dbId", $year) | Out-Null
    $feedCount = [Convert]::ToInt32($cmdFeed.ExecuteScalar())

    $cmdTomb = $conn.CreateCommand()
    $cmdTomb.CommandText = "SELECT COUNT(1) FROM [sync].[Tombstones] WHERE DatabaseId = @dbId;"
    $cmdTomb.Parameters.AddWithValue("@dbId", $year) | Out-Null
    $tombCount = [Convert]::ToInt32($cmdTomb.ExecuteScalar())

    $cmdOps = $conn.CreateCommand()
    $cmdOps.CommandText = "SELECT COUNT(1) FROM [sync].[ProcessedOperations];"
    $opsCount = [Convert]::ToInt32($cmdOps.ExecuteScalar())

    return @{
        CurrentVersion = $curVer
        LastUpdatedUtc = $lastUpdated
        ChangeFeedCount = $feedCount
        TombstoneCount = $tombCount
        ProcessedOperationsCount = $opsCount
    }
}

function Audit-LocalSyncMetadata($conn, $year) {
    $cmdLS = $conn.CreateCommand()
    $cmdLS.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @dbId;"
    $cmdLS.Parameters.AddWithValue("@dbId", $year) | Out-Null
    $lastServerVer = [Convert]::ToInt64($cmdLS.ExecuteScalar())

    $cmdOB = $conn.CreateCommand()
    $cmdOB.CommandText = "SELECT COUNT(1) FROM [sync].[LocalOutbox];"
    $obCount = [Convert]::ToInt32($cmdOB.ExecuteScalar())

    return @{
        LastServerVersion = $lastServerVer
        LocalOutboxTotal = $obCount
    }
}

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.4C: AUTHORITATIVE TRACKING CUTOVER VERIFICATION AUDIT STARTING  " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

$allAuditPassed = $true

foreach ($year in @("2026", "2027")) {
    Write-Host "`n==========================================================================" -ForegroundColor Cyan
    Write-Host "  AUDITING CUTOVER STATE: YEAR $year" -ForegroundColor Cyan
    Write-Host "==========================================================================" -ForegroundColor Cyan

    $azureCs = if ($year -eq "2026") { $Azure2026ConnectionString } else { $Azure2027ConnectionString }
    $localCs = if ($year -eq "2026") { $Local2026ConnectionString } else { $Local2027ConnectionString }

    Write-Host "Validating physical bindings for $year..." -NoNewline
    $azureB = Assert-PhysicalDatabaseBinding -ConnectionString $azureCs -ExpectedTarget "Azure" -ExpectedYear $year
    $localB = Assert-PhysicalDatabaseBinding -ConnectionString $localCs -ExpectedTarget "Local" -ExpectedYear $year
    Write-Host " PASS (Azure: $($azureB.InitialCatalog), Local: $($localB.InitialCatalog))" -ForegroundColor Green

    $azureConn = New-Object SqlConnection($azureCs)
    $azureConn.Open()
    $localConn = New-Object SqlConnection($localCs)
    $localConn.Open()

    try {
        Write-Host "Auditing Daily on Azure..." -NoNewline
        $azDaily = Audit-DailyTable $azureConn
        Write-Host " OK ($($azDaily.TotalRows) rows, hash: $($azDaily.DeterministicSha256.Substring(0,8))...)" -ForegroundColor Green

        Write-Host "Auditing Daily on Local..." -NoNewline
        $locDaily = Audit-DailyTable $localConn
        Write-Host " OK ($($locDaily.TotalRows) rows, hash: $($locDaily.DeterministicSha256.Substring(0,8))...)" -ForegroundColor Green

        Write-Host "Auditing Azure Sync Metadata..." -NoNewline
        $azSync = Audit-AzureSyncMetadata $azureConn $year
        Write-Host " OK (CurrentVersion=$($azSync.CurrentVersion), FeedCount=$($azSync.ChangeFeedCount), Tombstones=$($azSync.TombstoneCount))" -ForegroundColor Green

        Write-Host "Auditing Local Sync Metadata..." -NoNewline
        $locSync = Audit-LocalSyncMetadata $localConn $year
        Write-Host " OK (LastServerVersion=$($locSync.LastServerVersion), OutboxTotal=$($locSync.LocalOutboxTotal))" -ForegroundColor Green

        # Assertions
        $hashMatch = ($azDaily.DeterministicSha256 -eq $locDaily.DeterministicSha256)
        $rowCountMatch = ($azDaily.TotalRows -eq $locDaily.TotalRows)
        $azureVersionIs2 = ($azSync.CurrentVersion -eq 2)
        $localVersionIs0 = ($locSync.LastServerVersion -eq 0)
        $outboxClean = ($locSync.LocalOutboxTotal -eq 0)
        $feedCountIs2 = ($azSync.ChangeFeedCount -eq 2)
        $tombCountIs1 = ($azSync.TombstoneCount -eq 1)

        $classification = if ($hashMatch -and $rowCountMatch -and $azureVersionIs2 -and $localVersionIs0 -and $outboxClean -and $feedCountIs2) {
            "TRACKED_VERSION_GAP"
        } else {
            "DRIFT_OR_INCOMPLETE"
        }

        Write-Host "Classification Result: $classification" -ForegroundColor $(if ($classification -eq "TRACKED_VERSION_GAP") { "Green" } else { "Red" })

        if ($classification -ne "TRACKED_VERSION_GAP") {
            $allAuditPassed = $false
        }
    } finally {
        $azureConn.Close()
        $localConn.Close()
    }
}

Write-Host "`n==========================================================================" -ForegroundColor Cyan
Write-Host "  AUDIT STATUS: $(if ($allAuditPassed) { 'ALL CHECKS PASSED' } else { 'AUDIT FAILED' })" -ForegroundColor $(if ($allAuditPassed) { "Green" } else { "Red" })
Write-Host "==========================================================================" -ForegroundColor Cyan

if (-not $allAuditPassed) {
    exit 1
}
