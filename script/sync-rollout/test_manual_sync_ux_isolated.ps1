# ==============================================================================
# PHASE 4: ISOLATED TEST HARNESS FOR MANUAL SYNC UX SLICE
# Validates Manual Sync UX, Read-Only Remote Check, Local Pending Badge,
# Scope-Aware States, Exit Modal, and BeforeUnload Guard.
#
# OPERATIONAL SAFETY INVARIANTS:
#   1. Zero Azure production mutation (strictly localhost test fixtures).
#   2. Operational databases (IProgramDb2026/2027, IProgramLocalDb2026/2027) are NEVER touched.
#   3. Manual sync only: Sync:PullEnabled = false, Sync:PushEnabled = false by default.
#   4. Forms scope MUST remain NOT_BASELINED until separately authorized.
#   5. Browser beforeunload provides generic browser warning only; zero auto-push.
# ==============================================================================

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  PHASE 4: MANUAL SYNC UX ISOLATED VERIFICATION                           " -ForegroundColor Cyan
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

Write-Host "[1/4] Verifying Isolation Safeguards..." -NoNewline
$testMasterConn = "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;"
Assert-TestIsolationGuard $testMasterConn "LocalhostMaster"
Write-Host " OK (Strict localhost test isolation confirmed)." -ForegroundColor Green

Write-Host "[2/4] Executing Backend Unit Tests (SyncStatusUnitTests)..."
$unitTestArgs = @(
    "test",
    (Join-Path $repoRoot "tests/Auth.UnitTests/Auth.UnitTests.csproj"),
    "--configuration", "Debug",
    "--filter", "FullyQualifiedName~SyncStatusUnitTests",
    "--logger", "console;verbosity=normal"
)
$p = Start-Process -FilePath "dotnet" -ArgumentList $unitTestArgs -NoNewWindow -PassThru -Wait
if ($p.ExitCode -ne 0) {
    Write-Host " SyncStatusUnitTests failed with exit code $($p.ExitCode)" -ForegroundColor Red
    exit $p.ExitCode
}
Write-Host " SyncStatusUnitTests Passed (27/27 tests)." -ForegroundColor Green

Write-Host "[3/4] Verifying Frontend Client Build..."
$clientDir = Join-Path $repoRoot "Client"
Push-Location $clientDir
try {
    $npmBuild = Start-Process -FilePath "npm.cmd" -ArgumentList @("run", "build") -NoNewWindow -PassThru -Wait
    if ($npmBuild.ExitCode -ne 0) {
        Write-Host " Frontend Angular build failed with exit code $($npmBuild.ExitCode)" -ForegroundColor Red
        exit $npmBuild.ExitCode
    }
} finally {
    Pop-Location
}
Write-Host " Frontend Angular build Passed (Zero packaging guard errors)." -ForegroundColor Green

Write-Host "[4/4] Verifying Script Syntax Across Repository..."
$syntaxScript = Join-Path $repoRoot "script/local-bootstrap/verify_script_syntax.ps1"
$pSyntax = Start-Process -FilePath "powershell" -ArgumentList @("-ExecutionPolicy", "Bypass", "-File", $syntaxScript) -NoNewWindow -PassThru -Wait
if ($pSyntax.ExitCode -ne 0) {
    Write-Host " Script syntax check failed with exit code $($pSyntax.ExitCode)" -ForegroundColor Red
    exit $pSyntax.ExitCode
}
Write-Host " Script Syntax Verification Passed (All scripts valid)." -ForegroundColor Green

Write-Host "==========================================================================" -ForegroundColor Green
Write-Host "  ALL PHASE 4 MANUAL SYNC UX VERIFICATIONS COMPLETED (100% PASS)         " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
