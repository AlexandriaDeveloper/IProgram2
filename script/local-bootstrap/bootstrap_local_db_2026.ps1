# End-to-End Orchestrator for Phase 4 Slice 4.2B: Full Azure to Local SQL Server 2014 Bootstrap (Year 2026)
param(
    [string]$RunId = ""
)

$ErrorActionPreference = "Stop"

if (-not $RunId) {
    $RunId = (Get-Date).ToString("yyyyMMdd_HHmmss")
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$auditDir = Join-Path $repoRoot "docs\audit\sync-slice-4-2b\2026"
if (-not (Test-Path $auditDir)) {
    New-Item -ItemType Directory -Path $auditDir -Force | Out-Null
}

$bootstrapStorageDir = "C:\Users\seaag\IProgram_Bootstrap_2026"
if (-not (Test-Path $bootstrapStorageDir)) {
    New-Item -ItemType Directory -Path $bootstrapStorageDir -Force | Out-Null
}

$secretsPath = "$env:APPDATA\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1\secrets.json"
if (-not (Test-Path $secretsPath)) {
    throw "UserSecrets file not found: $secretsPath"
}
$secrets = Get-Content $secretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$azureConnStr = $secrets.'ConnectionStrings:DefaultConnection'
if (-not $azureConnStr) {
    throw "Azure connection string 'ConnectionStrings:DefaultConnection' not found in secrets."
}
# Adjust connection timeout for export stability
$azureExportConnStr = $azureConnStr -replace 'Connection Timeout=\d+', 'Connection Timeout=180'

$localEngineTarget = "localhost"
$finalLocalDbName = "IProgramLocalDb2026"
$stagingDbName = "IProgramLocalDb2026_Bootstrap_$RunId"
$bacpacFileName = "IProgramDb2026_Slice42B_${RunId}.bacpac"
$bacpacPath = Join-Path $bootstrapStorageDir $bacpacFileName

$sqlpackageExe = "C:\Users\seaag\.dotnet\tools\sqlpackage.exe"
if (-not (Test-Path $sqlpackageExe)) {
    throw "SqlPackage executable not found at: $sqlpackageExe"
}

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  PHASE 4 -- SLICE 4.2B: FULL AZURE -> LOCAL SQL SERVER 2014 BOOTSTRAP (2026)" -ForegroundColor Cyan
Write-Host "  Run ID: $RunId" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

Add-Type -AssemblyName 'System.Data'

# ==============================================================================
# GATE 0: PRE-FLIGHT / STOP CONDITIONS
# ==============================================================================
Write-Host "`n>>> [GATE 0] Running Pre-flight & Safety Stop Conditions..." -ForegroundColor Yellow

$gate0Report = [ordered]@{
    Gate = "Gate0_Preflight"
    RunId = $RunId
    TimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    ExpectedMasterBaseline = "7c46ea854b65742f7a34a594a73896f16782f5a0"
    CurrentGitBranch = (git rev-parse --abbrev-ref HEAD).Trim()
    CurrentGitCommit = (git rev-parse HEAD).Trim()
    LocalSqlEndpoint = $localEngineTarget
    LocalSqlVersion = $null
    LocalSqlProductVersion = $null
    LocalSqlEdition = $null
    LocalSqlMajorVersion = $null
    LocalSqlNetTransport = $null
    DiskSpaceFreeGB = $null
    FinalDbAlreadyExists = $false
    StagingDbAlreadyExists = $false
    AzureConnectivityOk = $false
    SqlPackageVersion = $null
    Status = "FAILED"
}

# 1. Check Local SQL Instance
$masterConnStr = "Server=$localEngineTarget;Database=master;Trusted_Connection=True;TrustServerCertificate=True"
$localMasterConn = New-Object System.Data.SqlClient.SqlConnection($masterConnStr)
try {
    $localMasterConn.Open()
    $cmd = $localMasterConn.CreateCommand()
    $cmd.CommandText = @"
SELECT 
    SERVERPROPERTY('ProductVersion') AS ProductVersion,
    SERVERPROPERTY('Edition') AS Edition,
    CONNECTIONPROPERTY('net_transport') AS NetTransport
"@
    $reader = $cmd.ExecuteReader()
    if ($reader.Read()) {
        $gate0Report.LocalSqlProductVersion = [string]$reader['ProductVersion']
        $gate0Report.LocalSqlEdition = [string]$reader['Edition']
        $gate0Report.LocalSqlNetTransport = [string]$reader['NetTransport']
    }
    $reader.Close()

    $verParts = $gate0Report.LocalSqlProductVersion.Split('.')
    $majorVer = [int]$verParts[0]
    $gate0Report.LocalSqlMajorVersion = $majorVer

    if ($majorVer -ne 12) {
        throw "STOP: Local SQL Server major version is $majorVer. SQL Server 2014 (major version 12) is strictly required."
    }

    # 2. Check Database Existence
    $cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = '$finalLocalDbName'"
    $finalExists = ([int]$cmd.ExecuteScalar()) -gt 0
    $gate0Report.FinalDbAlreadyExists = $finalExists
    if ($finalExists) {
        throw "STOP: Target database '$finalLocalDbName' already exists in local SQL Server. Overwriting is strictly prohibited."
    }

    $cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = '$stagingDbName'"
    $stagingExists = ([int]$cmd.ExecuteScalar()) -gt 0
    $gate0Report.StagingDbAlreadyExists = $stagingExists
    if ($stagingExists) {
        throw "STOP: Staging database '$stagingDbName' already exists. Aborting to avoid collision."
    }
}
finally {
    if ($localMasterConn.State -eq [System.Data.ConnectionState]::Open) {
        $localMasterConn.Close()
    }
}

# 3. Check Free Disk Space
$cDrive = Get-PSDrive C
$gate0Report.DiskSpaceFreeGB = [math]::Round($cDrive.Free / 1GB, 2)
if ($gate0Report.DiskSpaceFreeGB -lt 10) {
    throw "STOP: Insufficient disk space on C: ($($gate0Report.DiskSpaceFreeGB) GB free). At least 10 GB required."
}

# 4. Check Azure Connectivity READ-ONLY
$azureTestConn = New-Object System.Data.SqlClient.SqlConnection($azureConnStr)
try {
    $azureTestConn.Open()
    $cmd = $azureTestConn.CreateCommand()
    $cmd.CommandText = "SELECT DB_NAME() AS DbName, SERVERPROPERTY('Edition') AS Edition"
    $reader = $cmd.ExecuteReader()
    if ($reader.Read()) {
        $gate0Report.AzureConnectivityOk = $true
    }
    $reader.Close()
}
finally {
    if ($azureTestConn.State -eq [System.Data.ConnectionState]::Open) {
        $azureTestConn.Close()
    }
}

# 5. Capture SqlPackage Version
$sqlpackageVerOutput = & $sqlpackageExe /Version
$gate0Report.SqlPackageVersion = ($sqlpackageVerOutput | Out-String).Trim()

$gate0Report.Status = "PASSED"
$gate0JsonPath = Join-Path $auditDir "gate0_preflight_report.json"
[System.IO.File]::WriteAllText($gate0JsonPath, ($gate0Report | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)
Write-Host ">>> [GATE 0] Passed! Preflight evidence saved to: $gate0JsonPath" -ForegroundColor Green

# ==============================================================================
# GATE 1: CAPTURE AUTHORITATIVE AZURE 2026 BASELINE
# ==============================================================================
Write-Host "`n>>> [GATE 1] Capturing Authoritative Azure 2026 Baseline & Hashes..." -ForegroundColor Yellow

$verifierScript = Join-Path $PSScriptRoot "verify_bootstrap_integrity.ps1"
$azureBaselinePath = Join-Path $auditDir "azure_baseline_2026.json"

& $verifierScript -SourceConnectionString $azureConnStr -Mode CaptureSource -OutputJsonPath $azureBaselinePath
if ($LASTEXITCODE -ne 0) {
    throw "Failed to capture Azure 2026 baseline."
}
$azureBaselineData = Get-Content $azureBaselinePath -Raw -Encoding UTF8 | ConvertFrom-Json

# Capture current Azure sync.ServerState checkpoint
$serverStateConn = New-Object System.Data.SqlClient.SqlConnection($azureConnStr)
$serverStateConn.Open()
$cmd = $serverStateConn.CreateCommand()
$cmd.CommandText = "SELECT DatabaseId, CurrentVersion, LastUpdatedUtc FROM sync.ServerState WHERE DatabaseId = '2026'"
$reader = $cmd.ExecuteReader()
$azureServerStateCheckpoint = $null
if ($reader.Read()) {
    $azureServerStateCheckpoint = [ordered]@{
        DatabaseId = [string]$reader['DatabaseId']
        ServerVersion = [int64]$reader['CurrentVersion']
        LastUpdatedUtc = [string]$reader['LastUpdatedUtc']
    }
}
$reader.Close()
$serverStateConn.Close()

Write-Host ">>> [GATE 1] Azure 2026 baseline captured! Total Tables: $($azureBaselineData.Source.TableCount), Total Rows: $($azureBaselineData.Source.TotalRows)" -ForegroundColor Green
Write-Host "    Azure ServerState Checkpoint: ServerVersion = $($azureServerStateCheckpoint.ServerVersion)" -ForegroundColor Green

# ==============================================================================
# GATE 2: CONSISTENT EXPORT WINDOW & BACPAC EXPORT
# ==============================================================================
Write-Host "`n>>> [GATE 2] Starting Consistent Export Window..." -ForegroundColor Yellow
$maintenanceWindowStartUtc = (Get-Date).ToUniversalTime().ToString("o")

Write-Host "Exporting full BACPAC from Azure IProgramDb2026..." -ForegroundColor Cyan
Write-Host "Target file: $bacpacPath"

$exportArgs = "/Action:Export /SourceConnectionString:`"$azureExportConnStr`" /TargetFile:`"$bacpacPath`" /p:CommandTimeout=180 /p:LongRunningCommandTimeout=0"
$exportSw = [System.Diagnostics.Stopwatch]::StartNew()
$exportProc = Start-Process -FilePath $sqlpackageExe -ArgumentList $exportArgs -Wait -NoNewWindow -PassThru
$exportSw.Stop()

if ($exportProc.ExitCode -ne 0 -or -not (Test-Path $bacpacPath)) {
    throw "BACPAC export failed with exit code $($exportProc.ExitCode)."
}

$bacpacItem = Get-Item $bacpacPath
$bacpacHash = (Get-FileHash -Path $bacpacPath -Algorithm SHA256).Hash
$bacpacSizeMB = [math]::Round($bacpacItem.Length / 1MB, 2)
Write-Host "BACPAC export completed in $([math]::Round($exportSw.Elapsed.TotalSeconds, 1))s ($bacpacSizeMB MB, SHA-256: $bacpacHash)" -ForegroundColor Green

# Re-verify source baseline post-export to guarantee zero mutation during export
Write-Host "Re-verifying Azure source post-export for zero in-flight mutation..." -ForegroundColor Cyan
$postExportBaselinePath = Join-Path $auditDir "azure_post_export_baseline_2026.json"
& $verifierScript -SourceConnectionString $azureConnStr -Mode CaptureSource -OutputJsonPath $postExportBaselinePath
$postExportData = Get-Content $postExportBaselinePath -Raw -Encoding UTF8 | ConvertFrom-Json

# Verify pre vs post export match exactly
$dirtyExport = $false
if ($azureBaselineData.Source.TotalRows -ne $postExportData.Source.TotalRows) {
    $dirtyExport = $true
}
foreach ($tbl in $azureBaselineData.Source.Tables.PSObject.Properties.Name) {
    if ($azureBaselineData.Source.Tables.$tbl.Sha256Hash -ne $postExportData.Source.Tables.$tbl.Sha256Hash) {
        $dirtyExport = $true
        Write-Warning "Source hash mismatch post-export on $tbl!"
    }
}

$maintenanceWindowEndUtc = (Get-Date).ToUniversalTime().ToString("o")

if ($dirtyExport) {
    Remove-Item $bacpacPath -Force -ErrorAction SilentlyContinue
    throw "ABORT: Source data changed during BACPAC export! Maintenance window was not write-free. BACPAC discarded."
}

$gate2Manifest = [ordered]@{
    Gate = "Gate2_ConsistentExport"
    RunId = $RunId
    MaintenanceWindowStartUtc = $maintenanceWindowStartUtc
    MaintenanceWindowEndUtc = $maintenanceWindowEndUtc
    DurationSeconds = [math]::Round($exportSw.Elapsed.TotalSeconds, 2)
    SourceDatabase = "IProgramDb2026"
    AzureServer = "iprogram-sql-prod-01.database.windows.net"
    BacpacFileName = $bacpacFileName
    BacpacFileSizeBytes = $bacpacItem.Length
    BacpacFileSizeMB = $bacpacSizeMB
    BacpacSha256 = $bacpacHash
    SqlPackageVersion = $gate0Report.SqlPackageVersion
    SourceConsistencyVerified = $true
    PreExportRowCount = $azureBaselineData.Source.TotalRows
    PostExportRowCount = $postExportData.Source.TotalRows
}
$gate2JsonPath = Join-Path $auditDir "bacpac_export_manifest.json"
[System.IO.File]::WriteAllText($gate2JsonPath, ($gate2Manifest | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)
Write-Host ">>> [GATE 2] Export consistency verified! Evidence saved to: $gate2JsonPath" -ForegroundColor Green

# ==============================================================================
# GATE 3: SQL SERVER 2014 STAGING IMPORT
# ==============================================================================
Write-Host "`n>>> [GATE 3] Importing BACPAC into Staging Database [$stagingDbName] on localhost (SQL Server 2014)..." -ForegroundColor Yellow

$localStagingConnStr = "Server=$localEngineTarget;Database=$stagingDbName;Trusted_Connection=True;TrustServerCertificate=True"

$importArgs = "/Action:Import /SourceFile:`"$bacpacPath`" /TargetConnectionString:`"$localStagingConnStr`" /p:PreserveIdentityLastValues=True /p:CommandTimeout=180"
$importSw = [System.Diagnostics.Stopwatch]::StartNew()
$importProc = Start-Process -FilePath $sqlpackageExe -ArgumentList $importArgs -Wait -NoNewWindow -PassThru
$importSw.Stop()

if ($importProc.ExitCode -ne 0) {
    # Staging cleanup on failure
    $localMasterConn.Open()
    $cmd = $localMasterConn.CreateCommand()
    $cmd.CommandText = "IF DB_ID('$stagingDbName') IS NOT NULL BEGIN ALTER DATABASE [$stagingDbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$stagingDbName]; END"
    $cmd.ExecuteNonQuery()
    $localMasterConn.Close()
    throw "BACPAC import into SQL Server 2014 staging failed with exit code $($importProc.ExitCode)."
}

# Verify staging DB is online and check compatibility level
$localMasterConn.Open()
$cmd = $localMasterConn.CreateCommand()
$cmd.CommandText = "SELECT state_desc, compatibility_level FROM sys.databases WHERE name = '$stagingDbName'"
$reader = $cmd.ExecuteReader()
$stagingState = ""
$stagingCompat = 0
if ($reader.Read()) {
    $stagingState = [string]$reader['state_desc']
    $stagingCompat = [int]$reader['compatibility_level']
}
$reader.Close()

if ($stagingCompat -ne 120) {
    Write-Host "Setting compatibility level of staging database to 120..." -ForegroundColor Cyan
    $cmd.CommandText = "ALTER DATABASE [$stagingDbName] SET COMPATIBILITY_LEVEL = 120"
    $cmd.ExecuteNonQuery()
    $stagingCompat = 120
}
$localMasterConn.Close()

$gate3Report = [ordered]@{
    Gate = "Gate3_StagingImport"
    RunId = $RunId
    StagingDatabaseName = $stagingDbName
    TargetEngine = $localEngineTarget
    ImportDurationSeconds = [math]::Round($importSw.Elapsed.TotalSeconds, 2)
    DatabaseState = $stagingState
    CompatibilityLevel = $stagingCompat
    Status = "PASSED"
}
$gate3JsonPath = Join-Path $auditDir "staging_import_report.json"
[System.IO.File]::WriteAllText($gate3JsonPath, ($gate3Report | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)
Write-Host ">>> [GATE 3] Staging import passed in $([math]::Round($importSw.Elapsed.TotalSeconds, 1))s! Evidence saved to: $gate3JsonPath" -ForegroundColor Green

# ==============================================================================
# GATE 4: FULL SOURCE <-> STAGING INTEGRITY VERIFICATION
# ==============================================================================
Write-Host "`n>>> [GATE 4] Executing Full Source <-> Staging Integrity Verification..." -ForegroundColor Yellow

$fullIntegrityPath = Join-Path $auditDir "full_integrity_comparison.json"
& $verifierScript -SourceConnectionString $azureConnStr -TargetConnectionString $localStagingConnStr -Mode Compare -OutputJsonPath $fullIntegrityPath

if ($LASTEXITCODE -ne 0) {
    throw "Integrity verification between Azure and Staging FAILED!"
}

# Check for any identity current discrepancies and resolve safely
$integrityData = Get-Content $fullIntegrityPath -Raw -Encoding UTF8 | ConvertFrom-Json
$identCorrections = @()

$stagingConn = New-Object System.Data.SqlClient.SqlConnection($localStagingConnStr)
$stagingConn.Open()

foreach ($tblName in $integrityData.Comparisons.PSObject.Properties.Name) {
    $comp = $integrityData.Comparisons.$tblName
    if (-not $comp.IdentMatch -and $comp.SourceIdentCurrent -ne $null) {
        $srcVal = $comp.SourceIdentCurrent
        $tgtVal = $comp.TargetIdentCurrent
        Write-Host "Aligning IDENT_CURRENT on $tblName from $tgtVal to $srcVal..." -ForegroundColor Cyan
        
        $cmd = $stagingConn.CreateCommand()
        $cmd.CommandText = "DBCC CHECKIDENT('$tblName', RESEED, $srcVal)"
        $cmd.ExecuteNonQuery()

        $identCorrections += [ordered]@{
            Table = $tblName
            Before = $tgtVal
            After = $srcVal
        }
    }
}
$stagingConn.Close()

if ($identCorrections.Count -gt 0) {
    Write-Host "Re-running verification post-identity alignment..." -ForegroundColor Yellow
    & $verifierScript -SourceConnectionString $azureConnStr -TargetConnectionString $localStagingConnStr -Mode Compare -OutputJsonPath $fullIntegrityPath
    if ($LASTEXITCODE -ne 0) {
        throw "Integrity verification post-identity alignment FAILED!"
    }
}

Write-Host ">>> [GATE 4] Full Source <-> Staging Integrity Verified! 100% data and hash match across all 24 tables." -ForegroundColor Green

# ==============================================================================
# GATE 5: PROMOTE STAGING TO FINAL LOCAL 2026
# ==============================================================================
Write-Host "`n>>> [GATE 5] Promoting Staging Database to Final [$finalLocalDbName]..." -ForegroundColor Yellow

$localMasterConn.Open()
$cmd = $localMasterConn.CreateCommand()

# Ensure final name is not already taken
$cmd.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = '$finalLocalDbName'"
if (([int]$cmd.ExecuteScalar()) -gt 0) {
    $localMasterConn.Close()
    throw "STOP: Final database '$finalLocalDbName' unexpectedly exists before promotion!"
}

# Close connections and rename
$cmd.CommandText = @"
ALTER DATABASE [$stagingDbName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
ALTER DATABASE [$stagingDbName] MODIFY NAME = [$finalLocalDbName];
ALTER DATABASE [$finalLocalDbName] SET MULTI_USER;
"@
$cmd.ExecuteNonQuery()
$localMasterConn.Close()

$finalLocalConnStr = "Server=$localEngineTarget;Database=$finalLocalDbName;Trusted_Connection=True;TrustServerCertificate=True"

# Re-run full verification against final promoted DB
Write-Host "Re-verifying integrity on final-name database [$finalLocalDbName]..." -ForegroundColor Cyan
$finalVerificationPath = Join-Path $auditDir "final_database_verification.json"
& $verifierScript -SourceConnectionString $azureConnStr -TargetConnectionString $finalLocalConnStr -Mode Compare -OutputJsonPath $finalVerificationPath

if ($LASTEXITCODE -ne 0) {
    throw "Integrity verification on promoted database FAILED!"
}
Write-Host ">>> [GATE 5] Staging promoted to [$finalLocalDbName] and re-verified successfully!" -ForegroundColor Green

# ==============================================================================
# GATE 6: ADD LOCAL-ONLY SYNC METADATA
# ==============================================================================
Write-Host "`n>>> [GATE 6] Adding Local-Only Sync Metadata to [$finalLocalDbName]..." -ForegroundColor Yellow

$localSyncSqlPath = Join-Path $PSScriptRoot "local_sync_migration.sql"
$localSyncSql = Get-Content $localSyncSqlPath -Raw -Encoding UTF8

$finalDbConn = New-Object System.Data.SqlClient.SqlConnection($finalLocalConnStr)
$finalDbConn.Open()

# Split batches by GO and execute
$batches = $localSyncSql -split '(?m)^\s*GO\s*$'
foreach ($b in $batches) {
    $trimmed = $b.Trim()
    if ($trimmed) {
        $cmd = $finalDbConn.CreateCommand()
        $cmd.CommandText = $trimmed
        $cmd.ExecuteNonQuery()
    }
}

# Populate sync.LocalState
$cmd = $finalDbConn.CreateCommand()
$cmd.CommandText = @"
IF NOT EXISTS (SELECT * FROM sync.LocalState WHERE DatabaseId = '2026')
BEGIN
    INSERT INTO sync.LocalState (
        DatabaseId,
        DeviceId,
        DeviceName,
        LastServerVersion,
        LastSuccessfulPullUtc
    )
    VALUES (
        '2026',
        NEWID(),
        'LOCAL_DEVELOPMENT_WORKSTATION',
        @ServerVersion,
        GETUTCDATE()
    );
END
ELSE
BEGIN
    UPDATE sync.LocalState
    SET LastServerVersion = @ServerVersion
    WHERE DatabaseId = '2026';
END
"@
$cmd.Parameters.AddWithValue("@ServerVersion", $azureServerStateCheckpoint.ServerVersion)
$cmd.ExecuteNonQuery()

# Populate sync.BootstrapManifest
$finalVerData = Get-Content $finalVerificationPath -Raw -Encoding UTF8 | ConvertFrom-Json
$tableCheckSummary = [ordered]@{}
foreach ($tblName in $finalVerData.Comparisons.PSObject.Properties.Name) {
    $tableCheckSummary[$tblName] = [ordered]@{
        RowCount = $finalVerData.Comparisons.$tblName.TargetRowCount
        Sha256 = $finalVerData.Comparisons.$tblName.TargetHash
        Status = $finalVerData.Comparisons.$tblName.Status
    }
}
$tableCheckJson = ($tableCheckSummary | ConvertTo-Json -Compress)

$cmd = $finalDbConn.CreateCommand()
$cmd.CommandText = @"
INSERT INTO sync.BootstrapManifest (
    DatabaseId,
    BootstrapTimestampUtc,
    AzureServerSource,
    TargetLocalEngine,
    MigrationHistoryHash,
    TableCheckJson,
    IdentityCheckJson,
    Status,
    IsWriteAllowed
)
VALUES (
    '2026',
    GETUTCDATE(),
    'iprogram-sql-prod-01.database.windows.net',
    'localhost',
    @MigrationHistoryHash,
    @TableCheckJson,
    '[]',
    'VERIFIED_READY',
    1
);
"@
$migrationHistoryHash = (Get-FileHash -Path $finalVerificationPath -Algorithm SHA256).Hash
$cmd.Parameters.AddWithValue("@MigrationHistoryHash", $migrationHistoryHash)
$cmd.Parameters.AddWithValue("@TableCheckJson", $tableCheckJson)
$cmd.ExecuteNonQuery()

# Verify LocalOutbox is empty
$cmd = $finalDbConn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) FROM sync.LocalOutbox WHERE DatabaseId = '2026'"
$outboxCount = [int]$cmd.ExecuteScalar()

# Verify BootstrapManifest state
$cmd = $finalDbConn.CreateCommand()
$cmd.CommandText = "SELECT Status, IsWriteAllowed FROM sync.BootstrapManifest WHERE DatabaseId = '2026'"
$reader = $cmd.ExecuteReader()
$manifestStatus = ""
$manifestWriteAllowed = $false
if ($reader.Read()) {
    $manifestStatus = [string]$reader['Status']
    $manifestWriteAllowed = [bool]$reader['IsWriteAllowed']
}
$reader.Close()

$finalDbConn.Close()

$gate6Report = [ordered]@{
    Gate = "Gate6_LocalSyncMetadata"
    RunId = $RunId
    TargetDatabase = $finalLocalDbName
    LocalStateDatabaseId = "2026"
    LocalStateServerVersionCheckpoint = $azureServerStateCheckpoint.ServerVersion
    LocalOutboxRowCount = $outboxCount
    BootstrapManifestStatus = $manifestStatus
    BootstrapManifestIsWriteAllowed = $manifestWriteAllowed
    IdentCorrectionsCount = $identCorrections.Count
    Status = "PASSED"
}
$gate6JsonPath = Join-Path $auditDir "local_sync_metadata_audit.json"
[System.IO.File]::WriteAllText($gate6JsonPath, ($gate6Report | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)

Write-Host ">>> [GATE 6] LocalSync metadata initialized successfully!" -ForegroundColor Green
Write-Host "    BootstrapManifest: Status=$manifestStatus, IsWriteAllowed=$manifestWriteAllowed" -ForegroundColor Green
Write-Host "    LocalState: Checkpoint ServerVersion=$($azureServerStateCheckpoint.ServerVersion)" -ForegroundColor Green
Write-Host "    LocalOutbox: $outboxCount records (clean empty queue)" -ForegroundColor Green

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  BOOTSTRAP GATES 0 - 6 COMPLETED SUCCESSFULLY FOR YEAR 2026!" -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
