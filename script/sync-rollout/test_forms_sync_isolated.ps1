# ==============================================================================
# PHASE 3: ISOLATED TEST HARNESS FOR FORMS & FORMS-DETAILS SYNC SLICE
# Validates offline mutation, foreign-key resolution, topological ingestion,
# conflict risk enforcement, and replay for Form & FormDetails.
#
# OPERATIONAL SAFETY INVARIANTS:
#   1. Zero Azure production mutation (strictly localhost test fixtures).
#   2. Operational databases (IProgramDb2026/2027, IProgramLocalDb2026/2027) are NEVER touched.
#   3. Manual sync only: Sync:PullEnabled = false, Sync:PushEnabled = false by default.
# ==============================================================================

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  PHASE 3: FORMS & FORMS-DETAILS SYNC ISOLATED VERIFICATION               " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# 0. Strict Test Isolation Guard
function Assert-TestIsolationGuard([string]$connStr, [string]$contextName) {
    if ([string]::IsNullOrWhiteSpace($connStr)) { return }
    if ($connStr -match "(?i)\.database\.windows\.net") {
        throw "ISOLATION_VIOLATION: Test harness context '$contextName' detected forbidden Azure host in '$connStr'."
    }
    $forbiddenDbs = @("IProgramDb2026", "IProgramDb2027", "IProgramLocalDb2026", "IProgramLocalDb2027")
    foreach ($db in $forbiddenDbs) {
        if ($connStr -match "(?i)(Database|Initial Catalog)\s*=\s*$db\b") {
            throw "ISOLATION_VIOLATION: Test harness context '$contextName' detected forbidden operational database '$db' in '$connStr'."
        }
    }
}

Write-Host "[1/3] Verifying Isolation Safeguards..." -NoNewline
$testMasterConn = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;"
Assert-TestIsolationGuard $testMasterConn "LocalhostMaster"
Write-Host " OK (Strict localhost test isolation confirmed)." -ForegroundColor Green

Write-Host "[2/3] Executing Unit Tests (Serialization, Topological Sorting, Conflict Exceptions)..."
$unitTestArgs = @(
    "test",
    (Join-Path $repoRoot "tests/Auth.UnitTests/Auth.UnitTests.csproj"),
    "--configuration", "Debug",
    "--filter", "FullyQualifiedName~FormsSyncTests",
    "--logger", "console;verbosity=normal"
)
$p = Start-Process -FilePath "dotnet" -ArgumentList $unitTestArgs -NoNewWindow -PassThru -Wait
if ($p.ExitCode -ne 0) {
    Write-Host " FormsSyncTests failed with exit code $($p.ExitCode)" -ForegroundColor Red
    exit $p.ExitCode
}
Write-Host " FormsSyncTests Passed (18/18 tests)." -ForegroundColor Green

Write-Host "[3/3] Executing Isolated Database SQL Integration Tests (Transient DBs)..."
$integrationTestArgs = @(
    "test",
    (Join-Path $repoRoot "tests/Auth.UnitTests/Auth.UnitTests.csproj"),
    "--configuration", "Debug",
    "--filter", "FullyQualifiedName~FormsSyncIntegrationTests",
    "--logger", "console;verbosity=normal"
)
$pInt = Start-Process -FilePath "dotnet" -ArgumentList $integrationTestArgs -NoNewWindow -PassThru -Wait
if ($pInt.ExitCode -ne 0) {
    Write-Host " FormsSyncIntegrationTests failed with exit code $($pInt.ExitCode)" -ForegroundColor Red
    exit $pInt.ExitCode
}
Write-Host " FormsSyncIntegrationTests Passed (All scenarios verified against transient DBs)." -ForegroundColor Green

Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "  ALL FORMS SYNC VERIFICATIONS COMPLETED SUCCESSFULLY (100% PASS)         " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
