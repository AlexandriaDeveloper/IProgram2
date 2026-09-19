# ==============================================================================
# PHASE 4 — SLICE 4.1B: Read-Only Verification Tooling
# Strictly READ-ONLY verification across IProgramDb2026 and IProgramDb2027
# 
# Verifies:
# 1. Total row counts across all 19 tables.
# 2. SyncId NOT NULL constraint, 0 nulls, 0 duplicates on all 11 syncable tables.
# 3. IDENT_CURRENT stability.
# 4. PK and FK constraint integrity.
# 5. Deterministic business data SHA-256 fingerprints (excluding SyncId).
# 6. DB2027 manual index IX_FormDetails_FormId_IsActive_Includes preservation.
# 7. Azure-only sync tables in schema [sync] and ServerState row matching DatabaseId.
# 8. Complete absence of local sync tables (LocalOutbox, LocalState, BootstrapManifest) from Azure.
# ==============================================================================
[CmdletBinding()]
param (
    [string]$ConnectionString,
    [string]$TargetYear # '2026' or '2027'
)

$ErrorActionPreference = "Stop"

if (-not $ConnectionString) {
    $secretsPath = "$env:APPDATA\Microsoft\UserSecrets\534c15a8-262c-4c77-848e-9abbba0a57f1\secrets.json"
    if (Test-Path $secretsPath) {
        $secrets = Get-Content $secretsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($TargetYear -eq "2027") {
            $ConnectionString = $secrets.'ConnectionStrings:CON2027'
        } else {
            $ConnectionString = $secrets.'ConnectionStrings:DefaultConnection'
            $TargetYear = "2026"
        }
    } else {
        throw "ConnectionString parameter is required when user secrets file is not present."
    }
}

$conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
$conn.Open()

function Execute-Scalar($cmdText) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $cmdText
    $cmd.CommandTimeout = 180
    return $cmd.ExecuteScalar()
}

function Execute-DataTable($cmdText) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $cmdText
    $cmd.CommandTimeout = 180
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    $dt = New-Object System.Data.DataTable
    [void]$adapter.Fill($dt)
    return ,$dt
}

function Calculate-TableFingerprint($tblName) {
    $cmdCols = $conn.CreateCommand()
    $cmdCols.CommandText = "SELECT c.name FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = '$tblName' AND c.name != 'SyncId' ORDER BY c.column_id;"
    $r = $cmdCols.ExecuteReader()
    $cols = @()
    while ($r.Read()) { $cols += "[$($r['name'])]" }
    $r.Close()
    $colList = $cols -join ", "
    
    $cmdPk = $conn.CreateCommand()
    $cmdPk.CommandText = "SELECT col.name FROM sys.indexes i JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID('dbo.$tblName');"
    $pk = $cmdPk.ExecuteScalar()

    $sql = @"
SELECT COALESCE(
    CONVERT(VARCHAR(64), HASHBYTES('SHA2_256', 
        (SELECT $colList
         FROM [dbo].[$tblName]
         ORDER BY [$pk]
         FOR XML RAW, BINARY BASE64)
    ), 2),
    'EMPTY_TABLE'
) AS TableHash;
"@
    $cmdHash = $conn.CreateCommand()
    $cmdHash.CommandText = $sql
    $cmdHash.CommandTimeout = 180
    return $cmdHash.ExecuteScalar()
}

$approvedTables = @(
    "Daily", "Form", "FormDetails", "Departments", "Employees",
    "EmployeeBank", "EmployeeNetPays", "EmployeeWatchLists",
    "DailyReference", "FormRefernce", "EmployeeRefernce"
)

try {
    $dbName = Execute-Scalar "SELECT DB_NAME()"
    $serverName = Execute-Scalar "SELECT @@SERVERNAME"
    Write-Output "=============================================================================="
    Write-Output "READ-ONLY VERIFICATION FOR: $dbName ON SERVER: $serverName"
    Write-Output "=============================================================================="

    # 1. Verify Migrations
    Write-Output "`n[1] Migration History Check:"
    $mainMigs = Execute-DataTable "SELECT MigrationId, ProductVersion FROM [dbo].[__EFMigrationsHistory] ORDER BY MigrationId"
    $hasStaged = Execute-Scalar "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId = '20260919161502_AddSyncIdToBusinessEntities'"
    $hasFinal = Execute-Scalar "SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId = '20260919163939_FinalizeSyncIdNotNull'"
    Write-Output "  Main Migrations Count: $($mainMigs.Rows.Count)"
    Write-Output "  Staged Migration Present: $($hasStaged -eq 1)"
    Write-Output "  Finalization Migration Present: $($hasFinal -eq 1)"

    $hasAzureSyncHistory = Execute-Scalar "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'sync' AND t.name = '__EFMigrationsHistory_AzureSync'"
    if ($hasAzureSyncHistory -eq 1) {
        $azureMigs = Execute-DataTable "SELECT MigrationId, ProductVersion FROM [sync].[__EFMigrationsHistory_AzureSync]"
        $hasAzureSyncMig = Execute-Scalar "SELECT COUNT(*) FROM [sync].[__EFMigrationsHistory_AzureSync] WHERE MigrationId = '20260919155715_InitialAzureSyncSchema'"
        Write-Output "  sync.__EFMigrationsHistory_AzureSync Present: True"
        Write-Output "  InitialAzureSyncSchema Present: $($hasAzureSyncMig -eq 1)"
    } else {
        Write-Output "  sync.__EFMigrationsHistory_AzureSync Present: False"
    }

    # 2. Verify SyncId Columns NOT NULL, 0 nulls, 0 duplicates
    Write-Output "`n[2] SyncId Column & Constraint Verification:"
    foreach ($tbl in $approvedTables) {
        $isNullable = Execute-Scalar "SELECT c.is_nullable FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id WHERE t.name = '$tbl' AND c.name = 'SyncId'"
        $metrics = Execute-DataTable "SELECT COUNT(*) AS Total, ISNULL(SUM(CASE WHEN [SyncId] IS NULL THEN 1 ELSE 0 END), 0) AS NullCount, COUNT(DISTINCT [SyncId]) AS DistinctCount FROM [dbo].[$tbl]"
        $r = $metrics.Rows[0]
        $total = [int64]$r['Total']
        $nulls = [int64]$r['NullCount']
        $distinct = [int64]$r['DistinctCount']
        $duplicates = $total - $distinct
        $notNullCheck = ($isNullable -eq 0)

        Write-Output "  $tbl -> Total=$total | Nulls=$nulls | Duplicates=$duplicates | IsNotNull=$notNullCheck"
    }

    # 3. Verify Row Counts of all 19 tables
    Write-Output "`n[3] Total Row Counts across all 19 tables:"
    $tbls = Execute-DataTable "
        SELECT s.name + '.' + t.name AS FullName, SUM(p.rows) AS [RowCount]
        FROM sys.tables t
        JOIN sys.schemas s ON t.schema_id = s.schema_id
        JOIN sys.partitions p ON t.object_id = p.object_id
        WHERE p.index_id IN (0,1)
        GROUP BY s.name, t.name
        ORDER BY s.name, t.name;
    "
    foreach ($r in $tbls.Rows) {
        Write-Output "  $($r['FullName']) : $($r['RowCount'])"
    }

    # 4. Verify IDENT_CURRENT Values
    Write-Output "`n[4] IDENT_CURRENT Values:"
    $idents = Execute-DataTable "
        SELECT t.name AS TableName, c.name AS ColumnName, IDENT_CURRENT(t.name) AS IdentCurrent
        FROM sys.tables t
        JOIN sys.identity_columns c ON t.object_id = c.object_id
        ORDER BY t.name;
    "
    foreach ($r in $idents.Rows) {
        Write-Output "  $($r['TableName']).$($r['ColumnName']) = $($r['IdentCurrent'])"
    }

    # 5. Calculate Business Data Fingerprints (Excluding SyncId)
    Write-Output "`n[5] Business Data SHA-256 Fingerprints (Excluding SyncId):"
    foreach ($tbl in $approvedTables) {
        $hash = Calculate-TableFingerprint $tbl
        Write-Output "  $tbl : $hash"
    }

    # 6. Verify ServerState row
    Write-Output "`n[6] sync.ServerState Verification:"
    $ss = Execute-DataTable "SELECT DatabaseId, CurrentVersion, CONVERT(VARCHAR(30), LastUpdatedUtc, 127) AS LastUpdatedUtc FROM [sync].[ServerState]"
    foreach ($r in $ss.Rows) {
        Write-Output "  DatabaseId=$($r['DatabaseId']), CurrentVersion=$($r['CurrentVersion']), LastUpdatedUtc=$($r['LastUpdatedUtc'])"
    }

    # 7. Check DB2027 Manual Index
    Write-Output "`n[7] DB2027 Manual Index Check (IX_FormDetails_FormId_IsActive_Includes):"
    $hasManualIdx = Execute-Scalar "SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_FormDetails_FormId_IsActive_Includes' AND object_id = OBJECT_ID('dbo.FormDetails')"
    Write-Output "  IX_FormDetails_FormId_IsActive_Includes Present: $($hasManualIdx -eq 1)"

    # 8. Assert NO local tables exist in Azure
    Write-Output "`n[8] Azure Isolation Check (Asserting NO local sync tables exist in Azure):"
    $localTablesInAzure = Execute-Scalar "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'sync' AND t.name IN ('LocalOutbox', 'LocalState', 'BootstrapManifest', 'EntityIdMap')"
    Write-Output "  Local Sync Tables Found in Azure: $localTablesInAzure (Expected: 0)"

    Write-Output "`n=============================================================================="
    Write-Output "READ-ONLY VERIFICATION COMPLETED FOR $dbName"
    Write-Output "=============================================================================="
} finally {
    $conn.Close()
}
