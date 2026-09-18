param(
    [Parameter(Mandatory = $false)]
    [string]$FromMigration = "",

    [Parameter(Mandatory = $false)]
    [string]$ToMigration = "",

    [Parameter(Mandatory = $false)]
    [string]$OutputFile = "script/migration_idempotent.sql"
)

$ErrorActionPreference = "Stop"

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "   IProgram - Idempotent Migration Script Generator   " -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

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
Write-Host "[2/3] Generating idempotent SQL migration script..." -ForegroundColor Yellow
$projectPath = "src/Infrastructure"
$startupProjectPath = "src/Api"
$contextName = "ApplicationContext"

# Resolve absolute output path
$outDir = Split-Path -Path $OutputFile -Parent
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$cmdArgs = @(
    "ef", "migrations", "script",
    "--idempotent",
    "--context", $contextName,
    "--project", $projectPath,
    "--startup-project", $startupProjectPath,
    "--output", $OutputFile
)

if ($FromMigration) {
    $cmdArgs += $FromMigration
    if ($ToMigration) {
        $cmdArgs += $ToMigration
    }
}

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
    Write-Host "IMPORTANT: This idempotent script can be safely executed against any operational database (e.g. 2026, 2027) via SQL Server Management Studio or Azure Data Studio." -ForegroundColor Yellow
} else {
    Write-Error "Expected output file was not found: $OutputFile"
    exit 1
}
