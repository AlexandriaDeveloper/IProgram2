# Gate 7 Application Compatibility Smoke Test Runner (Local Read-Only)
param(
    [ValidateSet("2026", "2027")]
    [string]$Year = "2026",
    [string]$TargetDatabase = "",
    [string]$OutputJsonPath = ""
)

$ErrorActionPreference = "Stop"

$canonicalDb = if ($Year -eq "2027") { "IProgramLocalDb2027" } else { "IProgramLocalDb2026" }
if (-not [string]::IsNullOrWhiteSpace($TargetDatabase)) {
    if ($TargetDatabase -ne $canonicalDb) {
        throw "TargetDatabase mismatch: '$TargetDatabase' does not match canonical database '$canonicalDb' for Year '$Year'."
    }
} else {
    $TargetDatabase = $canonicalDb
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if (-not $OutputJsonPath) {
    $sliceDir = if ($Year -eq "2027") { "sync-slice-4-2c\2027" } else { "sync-slice-4-2b\2026" }
    $OutputJsonPath = Join-Path $repoRoot "docs\audit\$sliceDir\application_smoke_test_report.json"
}

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  GATE 7: APPLICATION COMPATIBILITY SMOKE TEST (READ-ONLY) ON $TargetDatabase" -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

$localConnStr = "Server=localhost;Database=$TargetDatabase;Trusted_Connection=True;TrustServerCertificate=True"
Add-Type -AssemblyName 'System.Data'

$report = [ordered]@{
    Gate = "Gate7_SmokeTest"
    TimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
    TargetDatabase = $TargetDatabase
    LocalEngine = "localhost (SQL Server 2014)"
    Tests = [ordered]@{}
    OverallStatus = "FAILED"
}

$allPassed = $true

# Test 1: Connectivity & Core Count
try {
    Write-Host "Test 1: ApplicationContext Connectivity & Row Reading..." -NoNewline
    $conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT COUNT(*) FROM dbo.Employees"
    $empCount = [int64]$cmd.ExecuteScalar()
    $conn.Close()

    $report.Tests["ApplicationContext_Read"] = [ordered]@{
        Status = "PASS"
        EmployeesCount = $empCount
    }
    Write-Host " PASS ($empCount employees)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["ApplicationContext_Read"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 2: Identity / Security Tables
try {
    Write-Host "Test 2: Identity & Security Tables Queryable..." -NoNewline
    $conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT COUNT(*) FROM dbo.AspNetUsers; SELECT COUNT(*) FROM dbo.AspNetRoles;"
    $reader = $cmd.ExecuteReader()
    $userCount = 0
    $roleCount = 0
    if ($reader.Read()) { $userCount = [int]$reader[0] }
    if ($reader.NextResult() -and $reader.Read()) { $roleCount = [int]$reader[0] }
    $reader.Close()
    $conn.Close()

    $report.Tests["Identity_Tables"] = [ordered]@{
        Status = "PASS"
        UsersCount = $userCount
        RolesCount = $roleCount
    }
    Write-Host " PASS (Users: $userCount, Roles: $roleCount)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["Identity_Tables"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 3: Representative Entities
try {
    Write-Host "Test 3: Representative Business Entities..." -NoNewline
    $conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
    $conn.Open()
    $counts = [ordered]@{}
    $tables = @("Daily", "DailyReference", "Departments", "EmployeeBank", "EmployeeNetPays", "Form", "FormDetails")
    foreach ($tbl in $tables) {
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT COUNT(*) FROM dbo.[$tbl]"
        $counts[$tbl] = [int64]$cmd.ExecuteScalar()
    }
    $conn.Close()

    $report.Tests["Representative_Entities"] = [ordered]@{
        Status = "PASS"
        Counts = $counts
    }
    Write-Host " PASS (All 7 entities verified)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["Representative_Entities"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 4: Common LINQ / SQL 2014 Patterns (Joins, Aggregations, Grouping)
try {
    Write-Host "Test 4: SQL Server 2014 T-SQL Patterns (Joins & Grouping)..." -NoNewline
    $conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT TOP 5 
    e.DepartmentId, 
    d.Name AS DepartmentName, 
    COUNT(e.Id) AS EmployeeCount
FROM dbo.Employees e
LEFT JOIN dbo.Departments d ON e.DepartmentId = d.Id
WHERE e.DepartmentId IS NOT NULL
GROUP BY e.DepartmentId, d.Name
ORDER BY EmployeeCount DESC
"@
    $reader = $cmd.ExecuteReader()
    $groupRows = 0
    while ($reader.Read()) { $groupRows++ }
    $reader.Close()
    $conn.Close()

    $report.Tests["TSql_Patterns_2014"] = [ordered]@{
        Status = "PASS"
        GroupRowsReturned = $groupRows
    }
    Write-Host " PASS ($groupRows groups evaluated)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["TSql_Patterns_2014"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 5: LocalSync Metadata
try {
    Write-Host "Test 5: LocalSync Metadata Verification..." -NoNewline
    $conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)
    $conn.Open()
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT Status, IsWriteAllowed FROM sync.BootstrapManifest WHERE DatabaseId = '$Year';
SELECT ServerVersionCheckpoint = LastServerVersion FROM sync.LocalState WHERE DatabaseId = '$Year';
SELECT OutboxCount = COUNT(*) FROM sync.LocalOutbox WHERE DatabaseId = '$Year';
"@
    $reader = $cmd.ExecuteReader()
    $bStatus = ""
    $bWriteAllowed = $false
    $checkpoint = 0
    $outboxCount = 0

    if ($reader.Read()) {
        $bStatus = [string]$reader['Status']
        $bWriteAllowed = [bool]$reader['IsWriteAllowed']
    }
    if ($reader.NextResult() -and $reader.Read()) {
        $checkpoint = [int64]$reader['ServerVersionCheckpoint']
    }
    if ($reader.NextResult() -and $reader.Read()) {
        $outboxCount = [int]$reader['OutboxCount']
    }
    $reader.Close()
    $conn.Close()

    $manifestOk = ($bStatus -eq "VERIFIED_READY" -and $bWriteAllowed -eq $true -and $checkpoint -ge 0 -and $outboxCount -eq 0)
    if (-not $manifestOk) {
        throw "LocalSync metadata invariant failed: Status=$bStatus, WriteAllowed=$bWriteAllowed, Checkpoint=$checkpoint, Outbox=$outboxCount"
    }

    $report.Tests["LocalSync_Metadata"] = [ordered]@{
        Status = "PASS"
        BootstrapStatus = $bStatus
        IsWriteAllowed = $bWriteAllowed
        ServerVersionCheckpoint = $checkpoint
        LocalOutboxCount = $outboxCount
    }
    Write-Host " PASS (Status: $bStatus, WriteAllowed: $bWriteAllowed, Checkpoint: $checkpoint)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["LocalSync_Metadata"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 6: AzureSync Guard Rejection
try {
    Write-Host "Test 6: AzureSync Physical Guard Rejection Unit Tests..." -NoNewline
    $testProj = Join-Path $repoRoot "tests\Auth.UnitTests\Auth.UnitTests.csproj"
    $bindingOutput = & dotnet test $testProj -c Release --filter "FullyQualifiedName~SyncSecurityBindingTests" --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Binding guard unit tests failed with exit code $LASTEXITCODE. Output: $bindingOutput"
    }
    
    # Parse test count from output
    $passed = 0
    $failed = 0
    $skipped = 0
    $total = 0
    foreach ($line in $bindingOutput) {
        if ($line -match "Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)") {
            $failed = [int]$matches[1]
            $passed = [int]$matches[2]
            $skipped = [int]$matches[3]
            $total = [int]$matches[4]
            break
        }
    }
    
    if ($total -eq 0 -or $failed -gt 0 -or $passed -eq 0) {
        throw "Binding guard tests did not pass as expected: Passed=$passed, Failed=$failed, Skipped=$skipped, Total=$total"
    }

    $report.Tests["AzureSync_Guard"] = [ordered]@{
        Status = "PASS"
        Filter = "FullyQualifiedName~SyncSecurityBindingTests"
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
        Total = $total
        Description = "AzureSyncContext and SyncSecurityBinding strictly enforce database and endpoint isolation"
    }
    Write-Host " PASS ($passed binding guard tests passed)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["AzureSync_Guard"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
}

# Test 7: .NET LocalDbRequired Unit Smoke Tests (with REQUIRE_LOCAL_DB=true)
try {
    Write-Host "Test 7: .NET LocalDbRequired Smoke Suite (REQUIRE_LOCAL_DB=true)..." -NoNewline
    $env:REQUIRE_LOCAL_DB = "true"
    $testProj = Join-Path $repoRoot "tests\Auth.UnitTests\Auth.UnitTests.csproj"
    $testOutput = & dotnet test $testProj -c Release --filter "Category=LocalDbRequired" --verbosity minimal 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE. Output: $testOutput"
    }
    
    # Parse test counts
    $passed = 0
    $failed = 0
    $skipped = 0
    $total = 0
    foreach ($line in $testOutput) {
        if ($line -match "Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)") {
            $failed = [int]$matches[1]
            $passed = [int]$matches[2]
            $skipped = [int]$matches[3]
            $total = [int]$matches[4]
            break
        }
    }
    
    # Strictly require exactly 12 passed tests (6 for 2026 + 6 for 2027)
    $expectedCount = 12
    if ($total -ne $expectedCount -or $passed -ne $expectedCount -or $failed -ne 0 -or $skipped -ne 0) {
        throw "Exact test count assertion failed! Expected $expectedCount passed, 0 failed, 0 skipped. Actual: Passed=$passed, Failed=$failed, Skipped=$skipped, Total=$total."
    }
    
    $report.Tests["DotNet_LocalDbRequired_Suite"] = [ordered]@{
        Status = "PASS"
        Filter = "Category=LocalDbRequired"
        RequireLocalDb = $true
        ExpectedTotal = $expectedCount
        Passed = $passed
        Failed = $failed
        Skipped = $skipped
        Total = $total
    }
    Write-Host " PASS ($passed passed, $failed failed, $skipped skipped - exact count $expectedCount verified)" -ForegroundColor Green
} catch {
    $allPassed = $false
    $report.Tests["DotNet_LocalDbRequired_Suite"] = [ordered]@{ Status = "FAIL"; Error = $_.Exception.Message }
    Write-Host " FAIL: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    $env:REQUIRE_LOCAL_DB = $null
}

$report.OverallStatus = if ($allPassed) { "PASS" } else { "FAIL" }

$parentDir = Split-Path -Parent $OutputJsonPath
if (-not (Test-Path $parentDir)) {
    New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
}
[System.IO.File]::WriteAllText($OutputJsonPath, ($report | ConvertTo-Json -Depth 5), [System.Text.Encoding]::UTF8)
Write-Host "`n>>> [GATE 7] Smoke tests finished with status: $($report.OverallStatus)" -ForegroundColor $(if ($allPassed) { "Green" } else { "Red" })
Write-Host "    Evidence saved to: $OutputJsonPath" -ForegroundColor Green

if ($report.OverallStatus -eq "FAIL") {
    exit 1
} else {
    exit 0
}
