$ErrorActionPreference = "Stop"

$localConnStr = "Server=localhost;Database=IProgramLocalDb2027;Integrated Security=True;TrustServerCertificate=True;"
$conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
$conn.Open()

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  CLEANUP AZURE SYNC OBJECTS & INITIALIZE LOCAL SYNC FOR YEAR 2027" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 1. Drop Azure-only sync tables
$azureOnlyTables = @(
    "ServerChangeFeed",
    "Tombstones",
    "ProcessedOperations",
    "ServerState",
    "__EFMigrationsHistory_AzureSync"
)

foreach ($t in $azureOnlyTables) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "IF OBJECT_ID('sync.$t', 'U') IS NOT NULL DROP TABLE [sync].[$t];"
    $cmd.ExecuteNonQuery() | Out-Null
    Write-Host "  Dropped [sync].[$t] from IProgramLocalDb2027." -ForegroundColor Green
}

# 2. Verify LocalOutbox is 0
$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT COUNT(*) FROM sync.LocalOutbox;"
$outboxCount = [int64]$cmd.ExecuteScalar()
Write-Host "  Verified LocalOutbox count: $outboxCount (must be 0)." -ForegroundColor $(if ($outboxCount -eq 0) { "Green" } else { "Red" })
if ($outboxCount -ne 0) {
    throw "LocalOutbox must be 0!"
}

# 3. Initialize sync.LocalState for DatabaseId = '2027'
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sync.LocalState WHERE DatabaseId = '2027')
BEGIN
    INSERT INTO sync.LocalState (DatabaseId, DeviceId, DeviceName, LastServerVersion)
    VALUES ('2027', NEWID(), 'Workstation', 0);
    PRINT 'Inserted LocalState row for 2027.';
END
ELSE
BEGIN
    UPDATE sync.LocalState
    SET LastServerVersion = 0
    WHERE DatabaseId = '2027';
    PRINT 'Updated LocalState row for 2027.';
END
"@
$cmd.ExecuteNonQuery() | Out-Null
Write-Host "  Initialized sync.LocalState (DatabaseId='2027', LastServerVersion=0)." -ForegroundColor Green

# 4. Initialize sync.BootstrapManifest with Status = 'REVIEW_HOLD', IsWriteAllowed = 0
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sync.BootstrapManifest WHERE DatabaseId = '2027')
BEGIN
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
        '2027', 
        SYSUTCDATETIME(), 
        'iprogram-sql-prod-01.database.windows.net/IProgramDb2027', 
        'localhost (SQL Server 2014 - Compat 120)', 
        'F20A3A31CDD47E7C7B56E62C9890775836FECD261D111F6E02082BF2AC1FE4F6', 
        '{}', 
        '{}', 
        'REVIEW_HOLD', 
        0
    );
    PRINT 'Inserted BootstrapManifest row for 2027 with REVIEW_HOLD.';
END
ELSE
BEGIN
    UPDATE sync.BootstrapManifest
    SET Status = 'REVIEW_HOLD',
        IsWriteAllowed = 0
    WHERE DatabaseId = '2027';
    PRINT 'Updated BootstrapManifest row for 2027 to REVIEW_HOLD.';
END
"@
$cmd.ExecuteNonQuery() | Out-Null
Write-Host "  Initialized sync.BootstrapManifest (Status='REVIEW_HOLD', IsWriteAllowed=0)." -ForegroundColor Green

$conn.Close()
Write-Host "`nLocal operational sync setup completed." -ForegroundColor Green
