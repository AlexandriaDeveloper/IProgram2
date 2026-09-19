<#
.SYNOPSIS
    Repeatable, unattended installation script for SQL Server 2022 Express.
.DESCRIPTION
    Installs SQL Server 2022 Express instance named SQLEXPRESS with Windows Integrated Security,
    enables TCP/IP local connectivity, and avoids installing extraneous components.
    Requires Windows Administrator elevation.
.NOTES
    Adheres strictly to IProgram Slice 4.2A security requirements:
    - Minimal footprint (Database Engine only).
    - Windows Integrated Security (no SQL SA password required).
    - Local connectivity enabled.
#>

param(
    [string]$InstanceName = "SQLEXPRESS",
    [string]$DownloadUrl = "https://download.microsoft.com/download/5/1/4/51452704-1d36-407b-8494-0f316279f530/SQL2022-SSEI-Expr.exe",
    [string]$TempDir = "$env:TEMP\IProgramSqlExpressSetup"
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  SQL Server 2022 Express Automated Installer (Slice 4.2A)  " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

# 1. Check Administrator Privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error @"
ADMINISTRATOR ELEVATION REQUIRED:
This installation script requires local Administrator privileges to configure Windows services and install SQL Server Express.
Please right-click PowerShell, select 'Run as administrator', and run:
    powershell -ExecutionPolicy Bypass -File .\script\local-bootstrap\install_sqlexpress.ps1
"@
    exit 1
}

# 2. Check if instance already exists
$existingService = Get-Service -Name "MSSQL`$$InstanceName" -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Service 'MSSQL`$$InstanceName' already exists. Status: $($existingService.Status)" -ForegroundColor Green
    if ($existingService.Status -ne "Running") {
        Write-Host "Starting service..." -ForegroundColor Yellow
        Start-Service -Name "MSSQL`$$InstanceName"
    }
    Write-Host "SQL Server Express instance '$InstanceName' is already installed and running." -ForegroundColor Green
    exit 0
}

# 3. Prepare Temp Directory
if (-not (Test-Path $TempDir)) {
    New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
}

$installerExe = Join-Path $TempDir "SQL2022-SSEI-Expr.exe"

# 4. Download Installer Bootstrapper
Write-Host "Downloading SQL Server 2022 Express installer..." -ForegroundColor Yellow
Write-Host "Source: $DownloadUrl" -ForegroundColor Gray
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
Invoke-WebRequest -Uri $DownloadUrl -OutFile $installerExe -UseBasicParsing

Write-Host "Download complete. Size: $([math]::Round((Get-Item $installerExe).Length / 1MB, 2)) MB" -ForegroundColor Green

# 5. Extract Media or Run Silent Install
$mediaDir = Join-Path $TempDir "Media"
Write-Host "Downloading full installation media silently into $mediaDir..." -ForegroundColor Yellow

$downloadArgs = "/ACTION=Download /MEDIATYPE=Core /QUIET /MEDIAPATH=`"$mediaDir`""
$dlProcess = Start-Process -FilePath $installerExe -ArgumentList $downloadArgs -Wait -PassThru -NoNewWindow

if ($dlProcess.ExitCode -ne 0) {
    Write-Error "Failed to download SQL Server media. Exit code: $($dlProcess.ExitCode)"
    exit 1
}

# Find setup.exe in the downloaded media folder
$setupExe = Get-ChildItem -Path $mediaDir -Filter "SETUP.EXE" -Recurse | Select-Object -First 1
if (-not $setupExe) {
    Write-Error "SETUP.EXE not found in downloaded media directory '$mediaDir'."
    exit 1
}

Write-Host "Found setup executable: $($setupExe.FullName)" -ForegroundColor Green

# 6. Execute Silent Setup
$currentUser = "$env:USERDOMAIN\$env:USERNAME"
Write-Host "Installing SQL Server Express (Instance: $InstanceName) using Windows Integrated Security for '$currentUser'..." -ForegroundColor Yellow

$setupArgs = @(
    "/ACTION=Install",
    "/IACCEPTSQLSERVERLICENSETERMS=1",
    "/Q",
    "/INSTANCENAME=$InstanceName",
    "/FEATURES=SQLEngine",
    "/SECURITYMODE=Windows",
    "/SQLSYSADMINACCOUNTS=`"BUILTIN\Administrators`" `"$currentUser`"",
    "/TCPENABLED=1",
    "/NPENABLED=1",
    "/SUPPRESSPRIVACYSTATEMENTNOTICE=1"
)

$installProcess = Start-Process -FilePath $setupExe.FullName -ArgumentList $setupArgs -Wait -PassThru -NoNewWindow

Write-Host "Installer finished with ExitCode: $($installProcess.ExitCode)" -ForegroundColor $(if ($installProcess.ExitCode -eq 0) { "Green" } else { "Red" })

if ($installProcess.ExitCode -ne 0) {
    Write-Error "SQL Server installation failed with exit code $($installProcess.ExitCode). Check setup bootstrap logs in '%ProgramFiles%\Microsoft SQL Server\160\Setup Bootstrap\Log'."
    exit 1
}

# 7. Verify Service Status
$service = Get-Service -Name "MSSQL`$$InstanceName" -ErrorAction SilentlyContinue
if ($service -and $service.Status -eq "Running") {
    Write-Host "SUCCESS: SQL Server Express instance '$InstanceName' is running and ready!" -ForegroundColor Green
} else {
    Write-Warning "Service 'MSSQL`$$InstanceName' status is: $($service.Status). Please verify service configuration."
}

# 8. Clean up temp files
try {
    Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
} catch {}

Write-Host "`nRun check_local_sql_readiness.ps1 to verify connectivity and generate readiness evidence." -ForegroundColor Cyan
