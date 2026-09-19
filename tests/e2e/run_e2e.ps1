<#
.SYNOPSIS
    Repeatable, Process-Scoped E2E Test Runner for IProgram.
.DESCRIPTION
    Executes the full End-to-End testing pipeline with strict environment isolation:
    1. Sets process-scoped environment variable overrides for local databases and E2E diagnostics.
    2. Builds the Angular client using the dedicated E2E configuration (environment.e2e.ts with relative /api/).
    3. Launches the ASP.NET Core API daemon within the isolated process environment.
    4. Executes runtime database safety preflight checks to guarantee zero Azure/quarantine access.
    5. Runs the Playwright test suite.
    6. Shuts down the background API daemon and clears environment overrides upon completion.
    
    Zero persistent mutations to User Secrets, appsettings, or system environment.
#>

[CmdletBinding()]
param (
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RootDir = Resolve-Path "$ScriptDir\..\.."
$ClientDir = "$RootDir\Client"
$ApiDir = "$RootDir\src\Api"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " IPROGRAM REPEATABLE PROCESS-SCOPED E2E RUNNER" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Establish Process-Scoped Environment Overrides (Disappears on process exit)
$env:ConnectionStrings__DefaultConnection = "Server=localhost;Database=IProgramDb2026;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=true;"
$env:ConnectionStrings__CON2027 = "Server=localhost;Database=IProgramDb2027;Integrated Security=True;TrustServerCertificate=True;MultipleActiveResultSets=true;"
$env:E2E__DiagnosticsEnabled = "true"
$env:ASPNETCORE_ENVIRONMENT = "Development"

Write-Host "[1/6] Process-scoped environment overrides established:" -ForegroundColor Green
Write-Host "  - ConnectionStrings__DefaultConnection => localhost (IProgramDb2026)"
Write-Host "  - ConnectionStrings__CON2027           => localhost (IProgramDb2027)"
Write-Host "  - E2E__DiagnosticsEnabled              => true"
Write-Host "  - User Secrets / appsettings           => UNTOUCHED"

# 2. Build Solution and Angular Client with E2E Configuration
if (-not $SkipBuild) {
    Write-Host "[2/6] Compiling Angular Client with E2E configuration (--configuration e2e)..." -ForegroundColor Yellow
    Push-Location $ClientDir
    try {
        npm run build:e2e
        if ($LASTEXITCODE -ne 0) { throw "Angular E2E build failed." }
    }
    finally {
        Pop-Location
    }

    Write-Host "[2/6] Building Backend Solution (Release)..." -ForegroundColor Yellow
    Push-Location $RootDir
    try {
        dotnet build IProgram.sln -c Release
        if ($LASTEXITCODE -ne 0) { throw "Backend build failed." }
    }
    finally {
        Pop-Location
    }
} else {
    Write-Host "[2/6] Skipping build step (-SkipBuild specified)." -ForegroundColor Yellow
}

# 3. Launch Backend API Daemon in Background with Isolated Environment
Write-Host "[3/6] Starting ASP.NET Core API daemon on http://localhost:5000..." -ForegroundColor Yellow
$apiProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run --no-build -c Release --urls http://localhost:5000" `
    -WorkingDirectory $ApiDir `
    -PassThru

$apiExited = $false
try {
    # Poll for API readiness
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Seconds 1
        if ($apiProcess.HasExited) {
            $apiExited = $true
            throw "API process exited prematurely with code $($apiProcess.ExitCode)."
        }
        try {
            $res = Invoke-WebRequest -Uri "http://localhost:5000/health" -Method Get -UseBasicParsing -TimeoutSec 2
            if ($res.StatusCode -eq 200) {
                $ready = $true
                break
            }
        } catch { }
    }

    if (-not $ready) {
        throw "Timed out waiting for API daemon to start on http://localhost:5000."
    }
    Write-Host "  -> API daemon is healthy and listening on http://localhost:5000." -ForegroundColor Green

    # 4. Execute Runtime Database Safety Preflight
    Write-Host "[4/6] Executing runtime database safety preflight..." -ForegroundColor Yellow
    & "$ScriptDir\preflight_db_safety_check.ps1"
    if ($LASTEXITCODE -ne 0) {
        throw "Database safety preflight check failed!"
    }

    # 5. Execute Playwright Full Test Suite
    Write-Host "[5/6] Executing Playwright Full E2E Test Suite..." -ForegroundColor Yellow
    Push-Location $ScriptDir
    try {
        npx playwright test
        $playwrightExitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}
finally {
    # 6. Clean Shutdown and Teardown
    Write-Host "[6/6] Tearing down background API process and clearing process environment..." -ForegroundColor Yellow
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
        Write-Host "  -> API process $($apiProcess.Id) terminated cleanly." -ForegroundColor Green
    }

    Remove-Item Env:\ConnectionStrings__DefaultConnection -ErrorAction SilentlyContinue
    Remove-Item Env:\ConnectionStrings__CON2027 -ErrorAction SilentlyContinue
    Remove-Item Env:\E2E__DiagnosticsEnabled -ErrorAction SilentlyContinue
    Write-Host "  -> Process-scoped overrides cleared." -ForegroundColor Green
}

Write-Host "==========================================================" -ForegroundColor Cyan
if ($playwrightExitCode -eq 0) {
    Write-Host " E2E SUITE COMPLETED SUCCESSFULLY (EXIT 0)" -ForegroundColor Green
} else {
    Write-Host " E2E SUITE FAILED (EXIT $playwrightExitCode)" -ForegroundColor Red
}
Write-Host "==========================================================" -ForegroundColor Cyan
exit $playwrightExitCode
