<#
.SYNOPSIS
    Evaluates local SQL Server instances for Phase 4 Slice 4.2A Local-First readiness.
.DESCRIPTION
    Scans for localhost\SQLEXPRESS and default localhost instances.
    Checks SQL Server edition, version, Windows Integrated Security connectivity,
    and reserved database presence without modifying any system state or logging credentials.
.PARAMETER OutputJsonPath
    Optional path to write the sanitized JSON readiness report.
#>
param(
    [string]$OutputJsonPath = ""
)

$ErrorActionPreference = "Continue"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  IProgram Local SQL Server Readiness Check (Slice 4.2A)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$report = [ordered]@{
    ReportType = "LocalSqlReadinessReport"
    Slice = "4.2A"
    GeneratedUtc = (Get-Date).ToUniversalTime().ToString("o")
    MachineName = $env:COMPUTERNAME
    CurrentUser = $env:USERNAME
    IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    TestedInstances = @()
    ReadinessStatus = "NOT_READY"
    RecommendedAction = ""
}

$instancesToTest = @(
    @{ Name = "localhost\SQLEXPRESS"; Description = "Preferred Local-First SQL Express Named Instance" },
    @{ Name = "localhost"; Description = "Default Local SQL Server Instance" }
)

Add-Type -AssemblyName 'System.Data'

$foundUsableInstance = $false

foreach ($inst in $instancesToTest) {
    $serverTarget = $inst.Name
    Write-Host "`nTesting instance: $serverTarget ($($inst.Description))..." -ForegroundColor Yellow

    $instResult = [ordered]@{
        ServerTarget = $serverTarget
        Description = $inst.Description
        ConnectionSuccessful = $false
        ServerName = $null
        InstanceName = $null
        Edition = $null
        ProductVersion = $null
        MajorVersion = $null
        AuthenticationMode = "Windows Integrated Security"
        TcpConnectivity = "Pending"
        DatabasesPresent = @()
        ErrorMessage = $null
    }

    $connStr = "Server=$serverTarget;Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)

    try {
        $conn.Open()
        $instResult.ConnectionSuccessful = $true
        $instResult.TcpConnectivity = "Successful"

        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
SELECT 
    SERVERPROPERTY('ServerName') AS ServerName,
    SERVERPROPERTY('InstanceName') AS InstanceName,
    SERVERPROPERTY('Edition') AS Edition,
    SERVERPROPERTY('ProductVersion') AS ProductVersion,
    PARSENAME(CONVERT(VARCHAR(32), SERVERPROPERTY('ProductVersion')), 4) AS MajorVersion;
"@
        $reader = $cmd.ExecuteReader()
        if ($reader.Read()) {
            $instResult.ServerName = [string]$reader["ServerName"]
            $instResult.InstanceName = if ($reader["InstanceName"] -ne [DBNull]::Value) { [string]$reader["InstanceName"] } else { "(Default)" }
            $instResult.Edition = [string]$reader["Edition"]
            $instResult.ProductVersion = [string]$reader["ProductVersion"]
            $instResult.MajorVersion = [string]$reader["MajorVersion"]
        }
        $reader.Close()

        # Check existing databases
        $cmdDb = $conn.CreateCommand()
        $cmdDb.CommandText = "SELECT name FROM sys.databases WHERE name IN ('IProgramLocalDb2026', 'IProgramLocalDb2027', 'IProgramDb2026', 'IProgramDb2027');"
        $dbReader = $cmdDb.ExecuteReader()
        $dbList = @()
        while ($dbReader.Read()) {
            $dbList += [string]$dbReader["name"]
        }
        $dbReader.Close()
        $instResult.DatabasesPresent = $dbList

        Write-Host "  -> Connected successfully!" -ForegroundColor Green
        Write-Host "     Server: $($instResult.ServerName), Edition: $($instResult.Edition), Version: $($instResult.ProductVersion)" -ForegroundColor Green

        if ($serverTarget -like "*SQLEXPRESS*" -or $instResult.Edition -like "*Express*") {
            $foundUsableInstance = $true
        }
    }
    catch {
        $instResult.ConnectionSuccessful = $false
        $instResult.TcpConnectivity = "Failed"
        $instResult.ErrorMessage = $_.Exception.Message
        Write-Host "  -> Connection failed: $($_.Exception.Message)" -ForegroundColor Red
    }
    finally {
        if ($conn.State -eq [System.Data.ConnectionState]::Open) {
            $conn.Close()
        }
    }

    $report.TestedInstances += $instResult
}

# Evaluate overall readiness
if ($foundUsableInstance) {
    $report.ReadinessStatus = "READY"
    $report.RecommendedAction = "SQL Server Express instance is available for Slice 4.2B database creation."
}
else {
    # Check if default instance is available
    $defaultInst = $report.TestedInstances | Where-Object { $_.ServerTarget -eq "localhost" -and $_.ConnectionSuccessful -eq $true }
    if ($defaultInst) {
        $report.ReadinessStatus = "ALTERNATE_LOCAL_SQL_FOUND"
        $report.RecommendedAction = "Default instance '$($defaultInst.ServerName)' ($($defaultInst.Edition)) is running. To use dedicated SQLEXPRESS per design, run install_sqlexpress.ps1 with Administrator privileges."
    }
    else {
        $report.ReadinessStatus = "NO_LOCAL_SQL_FOUND"
        $report.RecommendedAction = "No local SQL Server detected. Run install_sqlexpress.ps1 with Administrator privileges to install SQL Server 2022 Express."
    }
}

$jsonText = $report | ConvertTo-Json -Depth 5

if (![string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $parentDir = Split-Path -Path $OutputJsonPath -Parent
    if (![string]::IsNullOrEmpty($parentDir) -and !(Test-Path $parentDir)) {
        New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
    }
    $jsonText | Out-File -FilePath $OutputJsonPath -Encoding utf8
    Write-Host "`nSanitized readiness report written to: $OutputJsonPath" -ForegroundColor Cyan
}

Write-Host "`nOverall Readiness Status: $($report.ReadinessStatus)" -ForegroundColor Yellow
Write-Host "Action: $($report.RecommendedAction)" -ForegroundColor Yellow

return $report
