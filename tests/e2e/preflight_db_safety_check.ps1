<#
.SYNOPSIS
    Preflight DB Safety Verification Script for E2E Testing.
.DESCRIPTION
    Proves that the API runtime database connections used for E2E resolve strictly
    to approved local development databases (IProgramDb2026, IProgramDb2027) on localhost.
    Fails closed if runtime config resolves to an Azure or remote server, or the quarantined DB.
#>

[CmdletBinding()]
param (
    [string]$BaseUrl = "http://localhost:5000"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " E2E RUNTIME DATABASE SAFETY PREFLIGHT VERIFICATION" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$endpoint = "$BaseUrl/api/diagnostics/e2e-db-safety"
Write-Host "Querying runtime diagnostic endpoint: $endpoint"

try {
    $response = Invoke-RestMethod -Uri $endpoint -Method Get -TimeoutSec 10
}
catch {
    Write-Error "Failed to connect to API safety diagnostics endpoint at $endpoint. Ensure the backend API is running in Development mode on port 5000.`nError: $_"
    exit 1
}

if (-not $response.safetyCheckPassed) {
    Write-Host "[FAIL CLOSED] Database safety check failed!" -ForegroundColor Red
    $response.databases | Format-Table -Property DatabaseId, ServerClassification, ApprovedDatabase, InitialCatalog, IsQuarantinedDatabase, IsAzureOrRemote, IsSafe
    exit 1
}

Write-Host "[PASSED] All runtime database connections verified safe." -ForegroundColor Green
Write-Host "Total Verified Databases: $($response.databaseCount)"
$response.databases | Format-Table -Property DatabaseId, ServerClassification, ApprovedDatabase, InitialCatalog, IsQuarantinedDatabase, IsAzureOrRemote, IsSafe

Write-Host "Safety Summary:" -ForegroundColor Yellow
Write-Host "  - Local SQL Server: TRUE"
Write-Host "  - Remote/Azure SQL: BLOCKED (False)"
Write-Host "  - Quarantine DB (IProgramLocalDb2026): BYPASSED (False)"
Write-Host "  - Approved DB Targets: IProgramDb2026, IProgramDb2027"
Write-Host "==========================================================" -ForegroundColor Cyan
exit 0
