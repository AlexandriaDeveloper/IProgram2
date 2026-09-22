# PowerShell Script Syntax Verifier (Non-executing)
# Deterministically validates syntax of bootstrap scripts using the PowerShell Language Parser.

$ErrorActionPreference = "Stop"

$repoRoot = (Get-Item $PSScriptRoot).Parent.Parent.FullName

$targetScripts = @(
    "script/local-bootstrap/bootstrap_local_2027.ps1",
    "script/local-bootstrap/adopt_2027_clone.ps1",
    "script/local-bootstrap/cleanup_azure_sync_tables_local_2027.ps1",
    "script/local-bootstrap/verify_bootstrap_integrity.ps1",
    "script/local-bootstrap/smoke_test_local_2026.ps1",
    "script/local-bootstrap/verify_offline_readonly_smoke.ps1",
    "script/local-bootstrap/verify_offline_write_smoke.ps1",
    "script/local-bootstrap/verify_offline_push_smoke.ps1",
    "script/local-bootstrap/verify_authoritative_daily_tracking_smoke.ps1",
    "script/sync-rollout/audit_production_baseline.ps1",
    "script/sync-rollout/test_audit_production_baseline.ps1",
    "script/sync-rollout/audit_authoritative_cutover.ps1",
    "script/sync-rollout/execute_daily_pull_catchup.ps1",
    "script/sync-rollout/test_execute_daily_pull_catchup_isolated.ps1",
    "script/sync-rollout/test_isolated_local_cutover_rehearsal.ps1",
    "script/sync-rollout/test_slice_4_5d_production_enablement.ps1",
    "script/sync-rollout/start_localfirst_runtime.ps1",
    "script/sync-rollout/test_start_localfirst_runtime.ps1"
)

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " PowerShell Script Syntax-Parse Verification (Non-Executing)" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$allPassed = $true
$results = @()

foreach ($relPath in $targetScripts) {
    $fullPath = Join-Path $repoRoot ($relPath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    
    if (-not (Test-Path $fullPath)) {
        Write-Host " [MISSING] $relPath" -ForegroundColor Red
        $allPassed = $false
        $results += [PSCustomObject]@{ Script = $relPath; Status = "MISSING"; ErrorCount = 1; Errors = @("File not found") }
        continue
    }

    $content = [System.IO.File]::ReadAllText($fullPath)
    $tokens = $null
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($content, [ref]$tokens, [ref]$errors)

    if ($errors.Count -gt 0) {
        Write-Host " [SYNTAX ERROR] $relPath ($($errors.Count) errors)" -ForegroundColor Red
        foreach ($e in $errors) {
            Write-Host "   -> Line $($e.Extent.StartLineNumber): $($e.Message)" -ForegroundColor Red
        }
        $allPassed = $false
        $results += [PSCustomObject]@{ Script = $relPath; Status = "FAIL"; ErrorCount = $errors.Count; Errors = ($errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) }
    } else {
        Write-Host " [PASS] $relPath (0 errors, $($tokens.Count) tokens parsed)" -ForegroundColor Green
        $results += [PSCustomObject]@{ Script = $relPath; Status = "PASS"; ErrorCount = 0; Errors = @() }
    }
}

Write-Host "============================================================" -ForegroundColor Cyan
if ($allPassed) {
    Write-Host " ALL $($targetScripts.Count) POWERSHELL SCRIPTS PARSED WITH 0 SYNTAX ERRORS" -ForegroundColor Green
    exit 0
} else {
    Write-Host " POWERSHELL SCRIPT SYNTAX VERIFICATION FAILED" -ForegroundColor Red
    exit 1
}
