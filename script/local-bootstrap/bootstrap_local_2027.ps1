# Bootstrap Local SQL Server 2014 Database for Year 2027 (IProgramLocalDb2027)
# Slice 4.2C: One-time deterministic bootstrap from current Azure IProgramDb2027 (READ-ONLY)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$apiProj = Join-Path $repoRoot "src\Api\Auth.Api.csproj"

# Retrieve Azure connection string from User Secrets in-memory (SANITIZED)
$secrets = dotnet user-secrets list --project $apiProj 2>$null
$azure2027Cs = ""
foreach ($line in $secrets) {
    if ($line.StartsWith("ConnectionStrings:CON2027 = ")) {
        $azure2027Cs = $line.Substring("ConnectionStrings:CON2027 = ".Length).Trim()
        break
    }
}

if ([string]::IsNullOrWhiteSpace($azure2027Cs)) {
    Write-Error "Could not retrieve ConnectionStrings:CON2027 from user secrets."
    exit 1
}

$azure2027Cs = $azure2027Cs -replace "Connection Timeout=\d+", "Connection Timeout=60"
$azure2027Cs = $azure2027Cs -replace "TrustServerCertificate=False", "TrustServerCertificate=True"
if ($azure2027Cs -notmatch "TrustServerCertificate") {
    $azure2027Cs += ";TrustServerCertificate=True"
}

$localMasterCs = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;"
$localTargetDb = "IProgramLocalDb2027"
$localTargetCs = "Server=localhost;Database=$localTargetDb;Integrated Security=True;TrustServerCertificate=True;"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.2C: LOCAL BOOTSTRAP FOR YEAR 2027 ($localTargetDb)" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 1. Create target database on localhost if it does not exist
Write-Host "`n[Step 1] Ensuring $localTargetDb exists on localhost..." -ForegroundColor Yellow
$connMaster = New-Object System.Data.SqlClient.SqlConnection($localMasterCs)
$connMaster.Open()
$cmdMaster = $connMaster.CreateCommand()
$cmdMaster.CommandText = "SELECT COUNT(*) FROM sys.databases WHERE name = '$localTargetDb';"
$exists = [int]$cmdMaster.ExecuteScalar()

if ($exists -eq 0) {
    Write-Host "  Creating database $localTargetDb on localhost (Compatibility 120)..." -ForegroundColor Yellow
    $cmdMaster.CommandText = "CREATE DATABASE [$localTargetDb] COLLATE SQL_Latin1_General_CP1_CI_AS;"
    $cmdMaster.ExecuteNonQuery() | Out-Null
    $cmdMaster.CommandText = "ALTER DATABASE [$localTargetDb] SET COMPATIBILITY_LEVEL = 120;"
    $cmdMaster.ExecuteNonQuery() | Out-Null
    Write-Host "  Database $localTargetDb created successfully." -ForegroundColor Green
} else {
    Write-Host "  Database $localTargetDb already exists." -ForegroundColor Cyan
}
$connMaster.Close()

# 2. Ensure schemas exist in target
Write-Host "`n[Step 2] Ensuring schemas (dbo, sync) exist in $localTargetDb..." -ForegroundColor Yellow
$connTarget = New-Object System.Data.SqlClient.SqlConnection($localTargetCs)
$connTarget.Open()
$cmdTarget = $connTarget.CreateCommand()
$cmdTarget.CommandText = "IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = 'sync') EXEC('CREATE SCHEMA [sync]');"
$cmdTarget.ExecuteNonQuery() | Out-Null
$connTarget.Close()
Write-Host "  Schemas verified." -ForegroundColor Green

# 3. Load SMO and connect to Azure IProgramDb2027
Write-Host "`n[Step 3] Connecting SMO to Azure IProgramDb2027 (Read-Only)..." -ForegroundColor Yellow
[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.Smo') | Out-Null
[System.Reflection.Assembly]::LoadWithPartialName('Microsoft.SqlServer.ConnectionInfo') | Out-Null

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($azure2027Cs)
$serverConn = New-Object Microsoft.SqlServer.Management.Common.ServerConnection($builder.DataSource, $builder.UserID, $builder.Password)
$serverConn.DatabaseName = $builder.InitialCatalog
$serverConn.ConnectTimeout = 60
$serverConn.Connect()

$srv = New-Object Microsoft.SqlServer.Management.Smo.Server($serverConn)
$azureDb = $srv.Databases[$builder.InitialCatalog]

Write-Host "  SMO connected to $($azureDb.Name). Total tables: $($azureDb.Tables.Count)" -ForegroundColor Green

# Configure Table Scripter (Tables with PKs, unique constraints, defaults, checks - NO FKs or non-PK indexes yet)
$tableScripter = New-Object Microsoft.SqlServer.Management.Smo.Scripter($srv)
$tableScripter.Options.Indexes = $false
$tableScripter.Options.DriPrimaryKey = $true
$tableScripter.Options.DriChecks = $true
$tableScripter.Options.DriDefaults = $true
$tableScripter.Options.DriUniqueKeys = $true
$tableScripter.Options.DriForeignKeys = $false
$tableScripter.Options.IncludeHeaders = $false
$tableScripter.Options.TargetDatabaseEngineType = [Microsoft.SqlServer.Management.Common.DatabaseEngineType]::Standalone
$tableScripter.Options.TargetServerVersion = [Microsoft.SqlServer.Management.Smo.SqlServerVersion]::Version120

# 4. Create Tables in Target Database
Write-Host "`n[Step 4] Creating table structures in $localTargetDb..." -ForegroundColor Yellow
$connTarget = New-Object System.Data.SqlClient.SqlConnection($localTargetCs)
$connTarget.Open()

# Table creation order: independent tables first, or disable FK checking during copy
foreach ($t in $azureDb.Tables) {
    $schema = $t.Schema
    $name = $t.Name
    
    # Check if table already exists in target
    $cmdCheck = $connTarget.CreateCommand()
    $cmdCheck.CommandText = "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = '$schema' AND t.name = '$name';"
    $tExists = [int]$cmdCheck.ExecuteScalar()
    if ($tExists -eq 0) {
        Write-Host "  Creating table [$schema].[$name]..." -NoNewline
        $scripts = $tableScripter.Script($t)
        foreach ($sql in $scripts) {
            if (-not [string]::IsNullOrWhiteSpace($sql)) {
                $cmdExec = $connTarget.CreateCommand()
                $cmdExec.CommandText = $sql
                $cmdExec.ExecuteNonQuery() | Out-Null
            }
        }
        Write-Host " Done." -ForegroundColor Green
    } else {
        Write-Host "  Table [$schema].[$name] already exists." -ForegroundColor DarkGray
    }
}
$connTarget.Close()
$serverConn.Disconnect()

function Open-SqlConnectionWithRetry($connection, $maxAttempts = 5) {
    for ($i = 1; $i -le $maxAttempts; $i++) {
        try {
            $connection.Open()
            return
        } catch {
            if ($i -eq $maxAttempts) { throw }
            Write-Host "  Retrying connection ($i/$maxAttempts)..." -ForegroundColor Yellow
            Start-Sleep -Seconds (2 * $i)
        }
    }
}

# 5. Copy Data via Streaming SqlBulkCopy (preserving IDENTITY and NULLs)
Write-Host "`n[Step 5] Copying authoritative data from Azure to $localTargetDb..." -ForegroundColor Yellow
$connAzure = New-Object System.Data.SqlClient.SqlConnection($azure2027Cs)
Open-SqlConnectionWithRetry $connAzure

$connTarget = New-Object System.Data.SqlClient.SqlConnection($localTargetCs)
$connTarget.Open()

foreach ($t in $azureDb.Tables) {
    $schema = $t.Schema
    $name = $t.Name
    
    # Check target row count
    $cmdTargetCount = $connTarget.CreateCommand()
    $cmdTargetCount.CommandText = "SELECT COUNT(*) FROM [$schema].[$name];"
    $targetRows = [long]$cmdTargetCount.ExecuteScalar()
    
    if ($targetRows -gt 0) {
        Write-Host "  Table [$schema].[$name] already contains $targetRows rows. Skipping copy." -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Copying data for [$schema].[$name]..." -NoNewline
    $cmdAzureSelect = $connAzure.CreateCommand()
    $cmdAzureSelect.CommandTimeout = 300
    $cmdAzureSelect.CommandText = "SELECT * FROM [$schema].[$name];"
    
    $reader = $cmdAzureSelect.ExecuteReader()
    
    $bulkOpts = [System.Data.SqlClient.SqlBulkCopyOptions]"KeepIdentity, KeepNulls, TableLock"
    $bulkCopy = New-Object System.Data.SqlClient.SqlBulkCopy($connTarget, $bulkOpts, $null)
    $bulkCopy.DestinationTableName = "[$schema].[$name]"
    $bulkCopy.BulkCopyTimeout = 300
    $bulkCopy.BatchSize = 5000

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $bulkCopy.WriteToServer($reader)
    $sw.Stop()
    $reader.Close()
    $bulkCopy.Close()

    # Verify target count
    $newCount = [long]$cmdTargetCount.ExecuteScalar()
    Write-Host " $newCount rows copied in $([math]::Round($sw.Elapsed.TotalSeconds, 2))s." -ForegroundColor Green
}
$connAzure.Close()
$connTarget.Close()

# 6. Create Non-PK Indexes (including hot-path and SyncId unique indexes)
Write-Host "`n[Step 6] Creating non-PK indexes in $localTargetDb..." -ForegroundColor Yellow
$serverConn.Connect()
$srv = New-Object Microsoft.SqlServer.Management.Smo.Server($serverConn)
$azureDb = $srv.Databases[$builder.InitialCatalog]

$connTarget = New-Object System.Data.SqlClient.SqlConnection($localTargetCs)
$connTarget.Open()

$indexScripter = New-Object Microsoft.SqlServer.Management.Smo.Scripter($srv)
$indexScripter.Options.Indexes = $true
$indexScripter.Options.ClusteredIndexes = $false
$indexScripter.Options.NonClusteredIndexes = $true
$indexScripter.Options.IncludeHeaders = $false
$indexScripter.Options.TargetDatabaseEngineType = [Microsoft.SqlServer.Management.Common.DatabaseEngineType]::Standalone
$indexScripter.Options.TargetServerVersion = [Microsoft.SqlServer.Management.Smo.SqlServerVersion]::Version120

foreach ($t in $azureDb.Tables) {
    $schema = $t.Schema
    $name = $t.Name
    
    foreach ($idx in $t.Indexes) {
        if ($idx.IsIndexForProperty -or $idx.IndexKeyType -eq [Microsoft.SqlServer.Management.Smo.IndexKeyType]::DriPrimaryKey) {
            continue
        }
        
        # Check if index exists on target
        $cmdIdxCheck = $connTarget.CreateCommand()
        $cmdIdxCheck.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = '$($idx.Name)';"
        $idxExists = [int]$cmdIdxCheck.ExecuteScalar()
        if ($idxExists -eq 0) {
            Write-Host "  Creating index $($idx.Name) on [$schema].[$name]..." -NoNewline
            $scripts = $indexScripter.Script($idx)
            foreach ($sql in $scripts) {
                if (-not [string]::IsNullOrWhiteSpace($sql)) {
                    $cmdExec = $connTarget.CreateCommand()
                    $cmdExec.CommandText = $sql
                    $cmdExec.ExecuteNonQuery() | Out-Null
                }
            }
            Write-Host " Done." -ForegroundColor Green
        }
    }
}
$connTarget.Close()

# 7. Create Foreign Key Constraints
Write-Host "`n[Step 7] Creating Foreign Keys in $localTargetDb..." -ForegroundColor Yellow
$connTarget = New-Object System.Data.SqlClient.SqlConnection($localTargetCs)
$connTarget.Open()

$fkScripter = New-Object Microsoft.SqlServer.Management.Smo.Scripter($srv)
$fkScripter.Options.DriForeignKeys = $true
$fkScripter.Options.ScriptDrops = $false
$fkScripter.Options.IncludeHeaders = $false
$fkScripter.Options.TargetDatabaseEngineType = [Microsoft.SqlServer.Management.Common.DatabaseEngineType]::Standalone
$fkScripter.Options.TargetServerVersion = [Microsoft.SqlServer.Management.Smo.SqlServerVersion]::Version120

foreach ($t in $azureDb.Tables) {
    $schema = $t.Schema
    $name = $t.Name
    
    foreach ($fk in $t.ForeignKeys) {
        $cmdFkCheck = $connTarget.CreateCommand()
        $cmdFkCheck.CommandText = "SELECT COUNT(*) FROM sys.foreign_keys WHERE name = '$($fk.Name)';"
        $fkExists = [int]$cmdFkCheck.ExecuteScalar()
        if ($fkExists -eq 0) {
            Write-Host "  Creating FK $($fk.Name) on [$schema].[$name]..." -NoNewline
            $scripts = $fkScripter.Script($fk)
            foreach ($sql in $scripts) {
                if (-not [string]::IsNullOrWhiteSpace($sql)) {
                    $cmdExec = $connTarget.CreateCommand()
                    $cmdExec.CommandText = $sql
                    $cmdExec.ExecuteNonQuery() | Out-Null
                }
            }
            Write-Host " Done." -ForegroundColor Green
        }
    }
}
$connTarget.Close()

$serverConn.Disconnect()
Write-Host "`n[SUCCESS] Local database $localTargetDb bootstrap completed." -ForegroundColor Green
