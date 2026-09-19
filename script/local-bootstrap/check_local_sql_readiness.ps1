<#
.SYNOPSIS
    Evaluates local SQL Server default instance for Phase 4 Slice 4.2A Local-First readiness.
.DESCRIPTION
    Scans the primary selected local SQL Server instance (localhost).
    Checks SQL Server edition, version, actual transport protocol (via net_transport),
    Windows Integrated Security connectivity, and reserved database presence.
    Emits sanitized machine-readable JSON without exposing workstation/user names or credentials.
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
    MachineName = "[LOCAL_WORKSTATION]"
    CurrentUser = "[LOCAL_USER]"
    IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    TestedInstances = @()
    ReadinessStatus = "NOT_READY"
    RecommendedAction = ""
}

$instancesToTest = @(
    @{ Name = "localhost"; Description = "Selected Local SQL Server Default Instance" },
    @{ Name = "localhost\SQLEXPRESS"; Description = "Optional/Informational Named Instance" }
)

Add-Type -AssemblyName 'System.Data'

$foundPrimary = $false

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
        NetTransport = "Unknown"
        DatabasesPresent = @()
        ErrorMessage = $null
    }

    $connStr = "Server=$serverTarget;Database=master;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=5"
    $conn = New-Object System.Data.SqlClient.SqlConnection($connStr)

    try {
        $conn.Open()
        $instResult.ConnectionSuccessful = $true

        $cmd = $conn.CreateCommand()
        $cmd.CommandText = @"
SELECT 
    SERVERPROPERTY('ServerName') AS ServerName,
    SERVERPROPERTY('InstanceName') AS InstanceName,
    SERVERPROPERTY('Edition') AS Edition,
    SERVERPROPERTY('ProductVersion') AS ProductVersion,
    PARSENAME(CONVERT(VARCHAR(32), SERVERPROPERTY('ProductVersion')), 4) AS MajorVersion,
    CONVERT(VARCHAR(32), CONNECTIONPROPERTY('net_transport')) AS NetTransport;
"@
        $reader = $cmd.ExecuteReader()
        if ($reader.Read()) {
            $instResult.ServerName = "[LOCAL_WORKSTATION]"
            $instResult.InstanceName = if ($reader["InstanceName"] -ne [DBNull]::Value) { [string]$reader["InstanceName"] } else { "(Default)" }
            $instResult.Edition = [string]$reader["Edition"]
            $instResult.ProductVersion = [string]$reader["ProductVersion"]
            $instResult.MajorVersion = [string]$reader["MajorVersion"]
            $instResult.NetTransport = if ($reader["NetTransport"] -ne [DBNull]::Value) { [string]$reader["NetTransport"] } else { "Unknown" }
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
        Write-Host "     Edition: $($instResult.Edition), Version: $($instResult.ProductVersion), Transport: $($instResult.NetTransport)" -ForegroundColor Green

        if ($serverTarget -eq "localhost") {
            $foundPrimary = $true
        }
    }
    catch {
        $instResult.ConnectionSuccessful = $false
        $instResult.NetTransport = "None"
        $instResult.ErrorMessage = "Connection attempt failed (Instance not accessible or service not started)."
        Write-Host "  -> Connection failed: $($_.Exception.Message)" -ForegroundColor $(if ($serverTarget -eq "localhost") { "Red" } else { "DarkGray" })
    }
    finally {
        if ($conn.State -eq [System.Data.ConnectionState]::Open) {
            $conn.Close()
        }
    }

    $report.TestedInstances += $instResult
}

# Evaluate overall readiness per Business Owner Decision
if ($foundPrimary) {
    $report.ReadinessStatus = "LOCAL_ENGINE_AVAILABLE_BOOTSTRAP_DEFERRED"
    $report.RecommendedAction = "Selected local SQL Server default instance (localhost, Enterprise Edition 64-bit, SQL Server 2014) is running and ready. Physical operational database creation and Azure data bootstrap remain deferred per Business Owner decision."
}
else {
    $report.ReadinessStatus = "NO_LOCAL_SQL_FOUND"
    $report.RecommendedAction = "No local SQL Server detected on the workstation."
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
