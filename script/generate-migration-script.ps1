param(
    [Parameter(Mandatory = $false)]
    [string]$FromMigration = "20260917213000_AddSummaryReviewMethod",

    [Parameter(Mandatory = $false)]
    [string]$ToMigration = "20260918185849_OptimizeHotPathIndexesSprint4B",

    [Parameter(Mandatory = $false)]
    [string]$OutputFile = "script/sprint4b_targeted_migration.sql",

    [Parameter(Mandatory = $false)]
    [switch]$FullHistory
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "    IProgram - Targeted Idempotent Migration Generator    " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verify dotnet ef tool is installed
Write-Host "[1/3] Checking dotnet-ef tool availability..." -ForegroundColor Yellow
try {
    $efVersion = dotnet ef --version 2>&1
    Write-Host "Found EF Core CLI Tools version: $efVersion" -ForegroundColor Green
} catch {
    Write-Error "dotnet-ef tool is not installed or not in PATH. Run: dotnet tool install --global dotnet-ef"
    exit 1
}

# 2. Build ef command arguments
$projectPath = "src/Infrastructure"
$startupProjectPath = "src/Api"
$contextName = "ApplicationContext"

# Resolve absolute output directory
$outDir = Split-Path -Path $OutputFile -Parent
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$cmdArgs = @(
    "ef", "migrations", "script"
)

if ($FullHistory -or [string]::IsNullOrWhiteSpace($FromMigration)) {
    Write-Host ""
    Write-Host "WARNING: Generating FULL MIGRATION HISTORY script from inception." -ForegroundColor Red -BackgroundColor DarkYellow
    Write-Host "WARNING: Full-history scripts contain initial schema definitions and alterations from the beginning of the project." -ForegroundColor Yellow
    Write-Host "WARNING: It is NOT automatically safe to run against existing operational/production databases." -ForegroundColor Yellow
    Write-Host "WARNING: Always prefer targeted release migrations (using -FromMigration and -ToMigration)." -ForegroundColor Yellow
    Write-Host ""
} else {
    Write-Host "Targeted migration range:" -ForegroundColor Cyan
    Write-Host "   From : $FromMigration" -ForegroundColor Gray
    Write-Host "   To   : $(if ($ToMigration) { $ToMigration } else { '[Latest Model]' })" -ForegroundColor Gray
    $cmdArgs += $FromMigration
    if ($ToMigration) {
        $cmdArgs += $ToMigration
    }
}

$cmdArgs += @(
    "--idempotent",
    "--context", $contextName,
    "--project", $projectPath,
    "--startup-project", $startupProjectPath,
    "--output", $OutputFile
)

Write-Host "[2/3] Generating migration script..." -ForegroundColor Yellow
Write-Host "Executing: dotnet $($cmdArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @cmdArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to generate EF Core migration script (Exit Code: $LASTEXITCODE)."
    exit $LASTEXITCODE
}

# 3. Verify output
if (Test-Path $OutputFile) {
    $fileInfo = Get-Item $OutputFile
    $lineCount = (Get-Content $OutputFile).Count
    Write-Host "[3/3] Migration script generated successfully!" -ForegroundColor Green
    Write-Host "Output File : $($fileInfo.FullName)" -ForegroundColor Cyan
    Write-Host "File Size   : $([math]::Round($fileInfo.Length / 1KB, 2)) KB" -ForegroundColor Cyan
    Write-Host "Lines       : $lineCount" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "OPERATOR NOTICE:" -ForegroundColor Yellow
    Write-Host "- EF idempotent scripts rely on the accuracy of [__EFMigrationsHistory]." -ForegroundColor Yellow
    Write-Host "- Before executing on an operational database, verify that $FromMigration is recorded and $ToMigration is NOT recorded." -ForegroundColor Yellow
    Write-Host "- If database history is inconsistent with the physical schema, STOP and do not execute automatically." -ForegroundColor Yellow
} else {
    Write-Error "Expected output file was not found: $OutputFile"
    exit 1
}
