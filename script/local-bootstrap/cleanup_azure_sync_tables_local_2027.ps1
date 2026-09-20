param(
    [Parameter(Mandatory = $false)]
    [Nullable[long]]$ExpectedServerVersion = $null,
    [Parameter(Mandatory = $false)]
    [string]$AzureSourceConnectionString = ""
)

$ErrorActionPreference = "Stop"

function Test-AzureSourceBinding {
    param(
        [string]$connectionString,
        [string]$expectedDb = "IProgramDb2027"
    )
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        throw "ABORT: Azure connection string is null or empty."
    }
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($connectionString)
    $catalog = $b.InitialCatalog
    if ([string]::IsNullOrWhiteSpace($catalog)) {
        $catalog = $b["Database"]
    }
    if ($catalog -ne $expectedDb) {
        throw "SECURITY VIOLATION: Azure binding requires database '$expectedDb', but found '$catalog'."
    }
    $endpoint = $b.DataSource
    if ([string]::IsNullOrWhiteSpace($endpoint)) {
        throw "SECURITY VIOLATION: Azure endpoint cannot be empty."
    }
    
    # Strip protocol prefix
    $ep = $endpoint.Trim()
    if ($ep.StartsWith("tcp:", [System.StringComparison]::OrdinalIgnoreCase)) { $ep = $ep.Substring(4).Trim() }
    elseif ($ep.StartsWith("np:", [System.StringComparison]::OrdinalIgnoreCase)) { $ep = $ep.Substring(3).Trim() }
    elseif ($ep.StartsWith("lpc:", [System.StringComparison]::OrdinalIgnoreCase)) { $ep = $ep.Substring(4).Trim() }
    
    # Strip port suffix
    $commaIdx = $ep.IndexOf(',')
    if ($commaIdx -ge 0) { $ep = $ep.Substring(0, $commaIdx).Trim() }
    $colonIdx = $ep.IndexOf(':')
    if ($colonIdx -ge 0 -and $ep.IndexOf(':', $colonIdx + 1) -lt 0) { $ep = $ep.Substring(0, $colonIdx).Trim() }
    
    # Strip named instance
    $slashIdx = $ep.IndexOf('\')
    $hostPart = if ($slashIdx -ge 0) { $ep.Substring(0, $slashIdx).Trim() } else { $ep }
    
    $localHosts = @("localhost", ".", "(local)", "127.0.0.1", "::1", "[::1]", "(localdb)", $env:COMPUTERNAME)
    foreach ($lh in $localHosts) {
        if ($hostPart.Equals($lh, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "SECURITY VIOLATION: Azure binding cannot target local endpoint. Target must be a valid remote Azure endpoint."
        }
    }
    
    $b.ApplicationIntent = [System.Data.SqlClient.ApplicationIntent]::ReadOnly
    $b["Connect Timeout"] = 60
    $b.TrustServerCertificate = $true
    return $b.ConnectionString
}

# Resolve / capture ExpectedServerVersion from authoritative Azure read-only query if not provided
if ($null -eq $ExpectedServerVersion) {
    if ([string]::IsNullOrWhiteSpace($AzureSourceConnectionString)) {
        $apiProj = Resolve-Path (Join-Path $PSScriptRoot "../../src/Api/Auth.Api.csproj") -ErrorAction SilentlyContinue
        if ($apiProj) {
            $secrets = dotnet user-secrets list --project $apiProj 2>$null
            $secretKey = "ConnectionStrings:CON2027 = "
            foreach ($line in $secrets) {
                if ($line.StartsWith($secretKey)) {
                    $AzureSourceConnectionString = $line.Substring($secretKey.Length).Trim()
                    break
                }
            }
        }
    }
    
    if ([string]::IsNullOrWhiteSpace($AzureSourceConnectionString)) {
        throw "ABORT: ExpectedServerVersion was not provided and AzureSourceConnectionString could not be resolved from User Secrets."
    }
    
    $validSourceCs = Test-AzureSourceBinding -connectionString $AzureSourceConnectionString -expectedDb "IProgramDb2027"
    $azConn = New-Object System.Data.SqlClient.SqlConnection($validSourceCs)
    $azConn.Open()
    $azCmd = $azConn.CreateCommand()
    $azCmd.CommandText = "SELECT CurrentVersion FROM sync.ServerState WHERE DatabaseId = '2027';"
    $azVal = $azCmd.ExecuteScalar()
    $azConn.Close()
    if ($null -eq $azVal -or [DBNull]::Value.Equals($azVal)) {
        throw "ABORT: Could not read CurrentVersion from Azure sync.ServerState for DatabaseId='2027'."
    }
    $ExpectedServerVersion = [int64]$azVal
    Write-Host "  Captured authoritative Azure 2027 ServerState version: $ExpectedServerVersion" -ForegroundColor Green
} else {
    Write-Host "  Using provided ExpectedServerVersion: $ExpectedServerVersion" -ForegroundColor Green
}

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

# 3. Initialize sync.LocalState for DatabaseId = '2027' with dynamic checkpoint
$cmd = $conn.CreateCommand()
$cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sync.LocalState WHERE DatabaseId = '2027')
BEGIN
    INSERT INTO sync.LocalState (DatabaseId, DeviceId, DeviceName, LastServerVersion)
    VALUES ('2027', NEWID(), 'Workstation', @expectedVersion);
    PRINT 'Inserted LocalState row for 2027.';
END
ELSE
BEGIN
    UPDATE sync.LocalState
    SET LastServerVersion = @expectedVersion
    WHERE DatabaseId = '2027';
    PRINT 'Updated LocalState row for 2027.';
END
"@
$cmd.Parameters.AddWithValue("@expectedVersion", $ExpectedServerVersion) | Out-Null
$cmd.ExecuteNonQuery() | Out-Null
Write-Host "  Initialized sync.LocalState (DatabaseId='2027', LastServerVersion=$ExpectedServerVersion)." -ForegroundColor Green

# 4. Initialize sync.BootstrapManifest with Status = 'REVIEW_HOLD', IsWriteAllowed = 0, sanitized AzureServerSource
$manifestJson = [ordered]@{
    CapturedAzureServerStateVersion = $ExpectedServerVersion
    InitializationScope = "Slice 4.2C - Year 2027 Local Bootstrap"
} | ConvertTo-Json -Compress

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
        'Azure:IProgramDb2027', 
        'localhost (SQL Server 2014 - Compat 120)', 
        'F20A3A31CDD47E7C7B56E62C9890775836FECD261D111F6E02082BF2AC1FE4F6', 
        @manifestJson, 
        '{}', 
        'REVIEW_HOLD', 
        0
    );
    PRINT 'Inserted BootstrapManifest row for 2027 with REVIEW_HOLD.';
END
ELSE
BEGIN
    UPDATE sync.BootstrapManifest
    SET AzureServerSource = 'Azure:IProgramDb2027',
        Status = 'REVIEW_HOLD',
        IsWriteAllowed = 0,
        TableCheckJson = @manifestJson
    WHERE DatabaseId = '2027';
    PRINT 'Updated BootstrapManifest row for 2027 to REVIEW_HOLD.';
END
"@
$cmd.Parameters.AddWithValue("@manifestJson", $manifestJson) | Out-Null
$cmd.ExecuteNonQuery() | Out-Null
Write-Host "  Initialized sync.BootstrapManifest (Status='REVIEW_HOLD', IsWriteAllowed=0, AzureServerSource='Azure:IProgramDb2027')." -ForegroundColor Green

$conn.Close()
Write-Host "`nLocal operational sync setup completed." -ForegroundColor Green
