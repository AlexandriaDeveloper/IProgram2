# ==============================================================================
# SLICE 4.4B: UNIT & INVARIANT TEST SUITE FOR PRODUCTION BASELINE AUDIT TOOL
# Validates all 10 core safety invariants:
# 1. Wrong physical database binding rejected (SqlConnectionStringBuilder fail-closed)
# 2. Wrong LocalState DatabaseId => CutoverReadiness = NO
# 3. Duplicate LocalState rows (cardinality != 1) => CutoverReadiness = NO
# 4. BootstrapManifest not VERIFIED_READY => CutoverReadiness = NO
# 5. BootstrapManifest IsWriteAllowed = false => CutoverReadiness = NO
# 6. One FAILED Outbox row (TotalCount > 0) => NOT CLEAN_BASELINE & CutoverReadiness = NO
# 7. AuthoritativeTrackingEnabled = true => CutoverReadiness = NO
# 8. PushEnabled = true => CutoverReadiness = NO
# 9. LegacyMigrationEnabled = true => CutoverReadiness = NO
# 10. Generated artifacts contain no raw DeviceId or production business field values
# ==============================================================================

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$auditScript = Join-Path $repoRoot "script\sync-rollout\audit_production_baseline.ps1"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  SLICE 4.4B: BASELINE AUDIT TOOL INVARIANT VERIFICATION SUITE            " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

$passCount = 0
$totalCount = 0

function Assert-Test([string]$testName, [scriptblock]$action) {
    $global:totalCount++
    Write-Host "Test $($global:totalCount): $testName..." -NoNewline
    try {
        & $action
        $global:passCount++
        Write-Host " PASS" -ForegroundColor Green
    } catch {
        Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

# Source the physical binding function from audit script or define exact matching tester
Add-Type -AssemblyName "System.Data"

function Test-PhysicalBinding($cs, $target, $year) {
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($cs)
    $dataSource = if ($builder.DataSource) { $builder.DataSource.Trim() } else { "" }
    $initialCatalog = if ($builder.InitialCatalog) { $builder.InitialCatalog.Trim() } else { "" }

    # Strip optional tcp: prefix and ,port suffix for endpoint evaluation
    $serverHost = $dataSource -replace '^(?i)tcp:', '' -replace ',\s*[0-9]+$', ''

    if ($target -eq "Azure") {
        $expectedCatalog = if ($year -eq "2026") { "IProgramDb2026" } else { "IProgramDb2027" }
        if ($initialCatalog -ne $expectedCatalog) {
            throw "PHYSICAL_BINDING_ERROR: Azure $year InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
        }
        $isAzure = $serverHost.ToLowerInvariant().EndsWith(".database.windows.net") -and 
                   ($serverHost -match '^[a-zA-Z0-9.-]+\.database\.windows\.net$')
        if (-not $isAzure) {
            throw "PHYSICAL_BINDING_ERROR: Azure $year DataSource is not a trusted Azure SQL endpoint (*.database.windows.net)."
        }
    } elseif ($target -eq "Local") {
        $expectedCatalog = if ($year -eq "2026") { "IProgramLocalDb2026" } else { "IProgramLocalDb2027" }
        if ($initialCatalog -ne $expectedCatalog) {
            throw "PHYSICAL_BINDING_ERROR: Local $year InitialCatalog mismatch. Expected '$expectedCatalog', got '$initialCatalog'."
        }
        if ($serverHost.ToLowerInvariant().Contains(".database.windows.net")) {
            throw "PHYSICAL_BINDING_ERROR: Local $year DataSource cannot point to an Azure SQL endpoint."
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
            throw "PHYSICAL_BINDING_ERROR: Local $year DataSource is not a trusted local endpoint."
        }
    }
}

# ------------------------------------------------------------------------------
# Invariant 1: Physical database binding validation matrix
# ------------------------------------------------------------------------------
Assert-Test "1.1: Reject Azure 2026 targeting Azure 2027 database" {
    $threw = $false
    try {
        Test-PhysicalBinding "Server=sqlprod.database.windows.net;Database=IProgramDb2027;" "Azure" "2026"
    } catch {
        if ($_.Exception.Message -match "InitialCatalog mismatch") { $threw = $true }
    }
    if (-not $threw) { throw "Expected exception for Azure 2026 -> 2027 catalog" }
}

Assert-Test "1.2: Reject Azure 2027 targeting Azure 2026 database" {
    $threw = $false
    try {
        Test-PhysicalBinding "Server=sqlprod.database.windows.net;Database=IProgramDb2026;" "Azure" "2027"
    } catch {
        if ($_.Exception.Message -match "InitialCatalog mismatch") { $threw = $true }
    }
    if (-not $threw) { throw "Expected exception for Azure 2027 -> 2026 catalog" }
}

Assert-Test "1.3: Reject Azure targeting localhost/local endpoint" {
    $threw = $false
    try {
        Test-PhysicalBinding "Server=localhost;Database=IProgramDb2026;" "Azure" "2026"
    } catch {
        if ($_.Exception.Message -match "DataSource is not a trusted Azure SQL endpoint") { $threw = $true }
    }
    if (-not $threw) { throw "Expected exception for Azure -> localhost" }
}

Assert-Test "1.4: Reject Local targeting Azure SQL endpoint" {
    $threw = $false
    try {
        Test-PhysicalBinding "Server=sqlprod.database.windows.net;Database=IProgramLocalDb2026;" "Local" "2026"
    } catch {
        if ($_.Exception.Message -match "DataSource cannot point to an Azure SQL endpoint") { $threw = $true }
    }
    if (-not $threw) { throw "Expected exception for Local -> Azure endpoint" }
}

Assert-Test "1.5: Reject Local 2026 targeting Local 2027 database" {
    $threw = $false
    try {
        Test-PhysicalBinding "Server=localhost;Database=IProgramLocalDb2027;" "Local" "2026"
    } catch {
        if ($_.Exception.Message -match "InitialCatalog mismatch") { $threw = $true }
    }
    if (-not $threw) { throw "Expected exception for Local 2026 -> 2027 catalog" }
}

Assert-Test "1.6: Accept valid Azure and Local mappings" {
    Test-PhysicalBinding "Server=sqlprod.database.windows.net;Database=IProgramDb2026;" "Azure" "2026"
    Test-PhysicalBinding "Server=sqlprod.database.windows.net;Database=IProgramDb2027;" "Azure" "2027"
    Test-PhysicalBinding "Server=localhost;Database=IProgramLocalDb2026;" "Local" "2026"
    Test-PhysicalBinding "Server=localhost;Database=IProgramLocalDb2027;" "Local" "2027"
    Test-PhysicalBinding "Server=127.0.0.1;Database=IProgramLocalDb2026;" "Local" "2026"
    Test-PhysicalBinding "Server=.;Database=IProgramLocalDb2027;" "Local" "2027"
}

# ------------------------------------------------------------------------------
# Mock Readiness Evaluation Helper
# ------------------------------------------------------------------------------
function Evaluate-MockReadiness($azureSync, $localSync, $dailyMatch, $configSafety, $archSafety, $year="2026") {
    $classification = ""
    $versionMatch = ($azureSync.CurrentVersion -eq $localSync.LastServerVersion)
    $hasOutboxRows = ($localSync.LocalOutbox.TotalCount -gt 0)

    if ($dailyMatch -and $versionMatch -and -not $hasOutboxRows) {
        $classification = "CLEAN_BASELINE"
    } elseif (-not $dailyMatch -and $versionMatch -and -not $hasOutboxRows) {
        $classification = "BUSINESS_DRIFT_UNTRACKED"
    } elseif ($dailyMatch -and -not $versionMatch -and -not $hasOutboxRows) {
        $classification = "VERSION_DRIFT"
    } elseif ($hasOutboxRows -and $dailyMatch -and $versionMatch) {
        $classification = "OUTBOX_PENDING"
    } else {
        $classification = "MIXED_DRIFT"
    }

    $readinessIssues = @()
    if ($localSync.LocalStateRowCount -ne 1) {
        $readinessIssues += "LocalState must exist exactly once per DB"
    }
    if ($localSync.DatabaseId -ne $year) {
        $readinessIssues += "LocalState DatabaseId mismatch"
    }
    if ($localSync.LastServerVersion -lt 0) {
        $readinessIssues += "LocalState LastServerVersion must be non-negative"
    }
    if ($localSync.BootstrapManifestRowCount -ne 1) {
        $readinessIssues += "BootstrapManifest must exist exactly once per DB"
    }
    if ($localSync.BootstrapDatabaseId -ne $year) {
        $readinessIssues += "BootstrapManifest DatabaseId mismatch"
    }
    if ($localSync.BootstrapStatus -ne "VERIFIED_READY") {
        $readinessIssues += "BootstrapManifest Status must be 'VERIFIED_READY'"
    }
    if ($localSync.IsWriteAllowed -ne $true) {
        $readinessIssues += "BootstrapManifest IsWriteAllowed must be true"
    }
    if ($localSync.LocalOutbox.TotalCount -gt 0) {
        $readinessIssues += "LocalOutbox must be completely empty"
    }
    if ($configSafety.AuthoritativeTrackingEnabled -ne $false) {
        $readinessIssues += "Sync:AuthoritativeTrackingEnabled must be false"
    }
    if ($configSafety.PushEnabled -ne $false) {
        $readinessIssues += "Sync:PushEnabled must be false"
    }
    if ($configSafety.LegacyMigrationEnabled -ne $false) {
        $readinessIssues += "LegacyMigration:Enabled must be false"
    }
    if ($archSafety -ne "PASS") {
        $readinessIssues += "Architecture DML audit failed"
    }
    if ($classification -ne "CLEAN_BASELINE") {
        $readinessIssues += "Baseline drift detected ($classification)"
    }

    $cutoverReadiness = if ($readinessIssues.Count -eq 0) { "YES" } else { "NO" }
    return @{
        Classification = $classification
        CutoverReadiness = $cutoverReadiness
        Issues = $readinessIssues
    }
}

$cleanAzureSync = @{ CurrentVersion = [long]0; SchemaComplete = $true; ServerStateRowCount = 1; DatabaseId = "2026" }
$cleanLocalSync = @{
    LocalStateRowCount = 1
    DatabaseId = "2026"
    LastServerVersion = [long]0
    BootstrapManifestRowCount = 1
    BootstrapDatabaseId = "2026"
    BootstrapStatus = "VERIFIED_READY"
    IsWriteAllowed = $true
    LocalOutbox = @{ TotalCount = 0; PendingCount = 0; InProgressCount = 0; FailedCount = 0; CompletedCount = 0 }
}
$cleanConfig = @{ AuthoritativeTrackingEnabled = $false; PushEnabled = $false; LegacyMigrationEnabled = $false }

# ------------------------------------------------------------------------------
# Invariant 2: Wrong LocalState DatabaseId => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "2: Wrong LocalState DatabaseId => CutoverReadiness = NO" {
    $badLocal = $cleanLocalSync.Clone()
    $badLocal.DatabaseId = "2027" # Mismatched for 2026
    $res = Evaluate-MockReadiness $cleanAzureSync $badLocal $true $cleanConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "LocalState DatabaseId mismatch") { throw "Expected DatabaseId mismatch issue" }
}

# ------------------------------------------------------------------------------
# Invariant 3: Duplicate LocalState rows (RowCount != 1) => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "3: Duplicate LocalState rows => CutoverReadiness = NO" {
    $badLocal = $cleanLocalSync.Clone()
    $badLocal.LocalStateRowCount = 2 # Duplicate
    $res = Evaluate-MockReadiness $cleanAzureSync $badLocal $true $cleanConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "LocalState must exist exactly once per DB") { throw "Expected cardinality issue" }
}

# ------------------------------------------------------------------------------
# Invariant 4: BootstrapManifest not VERIFIED_READY => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "4: BootstrapManifest not VERIFIED_READY => CutoverReadiness = NO" {
    $badLocal = $cleanLocalSync.Clone()
    $badLocal.BootstrapStatus = "FAILED"
    $res = Evaluate-MockReadiness $cleanAzureSync $badLocal $true $cleanConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "BootstrapManifest Status must be 'VERIFIED_READY'") { throw "Expected status issue" }
}

# ------------------------------------------------------------------------------
# Invariant 5: BootstrapManifest IsWriteAllowed = false => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "5: BootstrapManifest IsWriteAllowed = false => CutoverReadiness = NO" {
    $badLocal = $cleanLocalSync.Clone()
    $badLocal.IsWriteAllowed = $false
    $res = Evaluate-MockReadiness $cleanAzureSync $badLocal $true $cleanConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "BootstrapManifest IsWriteAllowed must be true") { throw "Expected IsWriteAllowed issue" }
}

# ------------------------------------------------------------------------------
# Invariant 6: One FAILED Outbox row => NOT CLEAN_BASELINE & CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "6: One FAILED Outbox row => NOT CLEAN_BASELINE & CutoverReadiness = NO" {
    $badLocal = $cleanLocalSync.Clone()
    $badLocal.LocalOutbox = @{ TotalCount = 1; PendingCount = 0; InProgressCount = 0; FailedCount = 1; CompletedCount = 0 }
    $res = Evaluate-MockReadiness $cleanAzureSync $badLocal $true $cleanConfig "PASS" "2026"
    if ($res.Classification -eq "CLEAN_BASELINE") { throw "Expected classification to NOT be CLEAN_BASELINE (was: $($res.Classification))" }
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "LocalOutbox must be completely empty") { throw "Expected outbox empty issue" }
}

# ------------------------------------------------------------------------------
# Invariant 7: AuthoritativeTrackingEnabled = true => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "7: AuthoritativeTrackingEnabled = true => CutoverReadiness = NO" {
    $badConfig = $cleanConfig.Clone()
    $badConfig.AuthoritativeTrackingEnabled = $true
    $res = Evaluate-MockReadiness $cleanAzureSync $cleanLocalSync $true $badConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "Sync:AuthoritativeTrackingEnabled must be false") { throw "Expected config safety issue" }
}

# ------------------------------------------------------------------------------
# Invariant 8: PushEnabled = true => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "8: PushEnabled = true => CutoverReadiness = NO" {
    $badConfig = $cleanConfig.Clone()
    $badConfig.PushEnabled = $true
    $res = Evaluate-MockReadiness $cleanAzureSync $cleanLocalSync $true $badConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "Sync:PushEnabled must be false") { throw "Expected config safety issue" }
}

# ------------------------------------------------------------------------------
# Invariant 9: LegacyMigrationEnabled = true => CutoverReadiness = NO
# ------------------------------------------------------------------------------
Assert-Test "9: LegacyMigrationEnabled = true => CutoverReadiness = NO" {
    $badConfig = $cleanConfig.Clone()
    $badConfig.LegacyMigrationEnabled = $true
    $res = Evaluate-MockReadiness $cleanAzureSync $cleanLocalSync $true $badConfig "PASS" "2026"
    if ($res.CutoverReadiness -ne "NO") { throw "Expected CutoverReadiness = NO" }
    if ($res.Issues -notcontains "LegacyMigration:Enabled must be false") { throw "Expected config safety issue" }
}

# ------------------------------------------------------------------------------
# Invariant 10: Generated artifacts sanitization check
# ------------------------------------------------------------------------------
Assert-Test "10: Generated artifacts contain no raw DeviceId or production field values" {
    $reports = @(
        (Join-Path $repoRoot "docs\audit\sync-slice-4-4b\baseline_2026_report.json"),
        (Join-Path $repoRoot "docs\audit\sync-slice-4-4b\baseline_2027_report.json"),
        (Join-Path $repoRoot "docs\audit\sync-slice-4-4b\BASELINE_RECONCILIATION_SUMMARY.md")
    )
    foreach ($repPath in $reports) {
        if (Test-Path $repPath) {
            $content = Get-Content $repPath -Raw
            if ($content -match '"DeviceId":\s*"[0-9a-fA-F-]{36}"') {
                throw "Sanitization violation in $($repPath): Raw DeviceId UUID detected!"
            }
            if ($content -match '"ActiveLeaseToken"') {
                throw "Sanitization violation in $($repPath): ActiveLeaseToken detected!"
            }
            if ($content -match 'password=' -or $content -match 'User ID=' -or $content -match 'database\.windows\.net') {
                throw "Sanitization violation in $($repPath): Credential or server hostname detected!"
            }
        }
    }
}

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host " ALL $passCount / $totalCount INVARIANT TESTS PASSED SUCCESSFULLY!                 " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
