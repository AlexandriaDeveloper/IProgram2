# ==============================================================================
# SLICE 4.5E: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER
# Safe, repeatable, fail-closed operator tool for starting, stopping, and
# monitoring the LocalFirst runtime profile on the operational workstation.
#
# OPERATIONAL INVARIANTS:
#   1. Process-local configuration only: Never edits committed appsettings.json.
#   2. Local loopback binding: 127.0.0.1 only (default port 5000).
#   3. Zero automatic sync: Pull=false, Push=false, No background workers.
#   4. Tripwire remote connections: DefaultConnection and CON2027 point to
#      loopback tripwires to guarantee zero silent Azure fallback.
#   5. Preflight validation: Verifies SQL 2014 compatibility level 120,
#      VERIFIED_READY BootstrapManifest, LastServerVersion >= 8,
#      0 pending outbox rows, and exact Daily cryptographic hash parity.
#   6. Zero secrets exposed: Never prints or logs connection strings or JWT keys.
# ==============================================================================

using namespace System.Data.SqlClient
using namespace System.Net.Sockets

[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [ValidateSet("Start", "Stop", "Status", "Restart", "Run")]
    [string]$Action = "Start",

    [int]$Port = 5000,
    [string]$ExpectedMasterSha = "49a0be38218c199a100c8d057a2acd45306844c0",
    [switch]$SkipGitVerification,
    [switch]$AllowIsolatedTestMode,
    [string]$OverrideLocal2026ConnStr,
    [string]$OverrideLocal2027ConnStr,
    [string]$OverridePidFilePath,
    [switch]$Wait,
    [switch]$ExportFunctionsOnly
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path

# --- 1. Pure Helper: Calculate Daily Hash (Authoritative Deterministic Implementation) ---
function Calculate-DailyHashAndCounts($conn) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT 
    [SyncId],
    [Name],
    [DailyDate],
    [Closed],
    [CreatedAt],
    [CreatedBy],
    [UpdatedAt],
    [UpdatedBy],
    [DeactivatedAt],
    [DeactivatedBy],
    [IsActive]
FROM [dbo].[Daily]
ORDER BY [SyncId] ASC;
"@
    
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms, [System.Text.Encoding]::UTF8)

    $reader = $cmd.ExecuteReader()
    $count = 0
    $activeCount = 0
    $inactiveCount = 0

    while ($reader.Read()) {
        $count++
        $syncId = $reader.GetGuid(0)
        $name = if ($reader.IsDBNull(1)) { $null } else { $reader.GetString(1) }
        $dailyDate = if ($reader.IsDBNull(2)) { $null } else { $reader.GetDateTime(2) }
        $closed = $reader.GetBoolean(3)
        $createdAt = if ($reader.IsDBNull(4)) { $null } else { $reader.GetDateTime(4) }
        $createdBy = if ($reader.IsDBNull(5)) { $null } else { $reader.GetString(5) }
        $updatedAt = if ($reader.IsDBNull(6)) { $null } else { $reader.GetDateTime(6) }
        $updatedBy = if ($reader.IsDBNull(7)) { $null } else { $reader.GetString(7) }
        $deactivatedAt = if ($reader.IsDBNull(8)) { $null } else { $reader.GetDateTime(8) }
        $deactivatedBy = if ($reader.IsDBNull(9)) { $null } else { $reader.GetString(9) }
        $isActive = $reader.GetBoolean(10)

        if ($isActive) { $activeCount++ } else { $inactiveCount++ }

        $bw.Write([byte]0xFF)
        $bw.Write($syncId.ToByteArray())

        if ($name -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($name) }
        if ($dailyDate -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($dailyDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [System.Globalization.CultureInfo]::InvariantCulture)) }
        $bw.Write($closed)
        if ($createdAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($createdAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [System.Globalization.CultureInfo]::InvariantCulture)) }
        if ($createdBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($createdBy) }
        if ($updatedAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($updatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [System.Globalization.CultureInfo]::InvariantCulture)) }
        if ($updatedBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($updatedBy) }
        if ($deactivatedAt -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($deactivatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", [System.Globalization.CultureInfo]::InvariantCulture)) }
        if ($deactivatedBy -eq $null) { $bw.Write([byte]0x00) } else { $bw.Write([byte]0x01); $bw.Write($deactivatedBy) }
        $bw.Write($isActive)
    }
    $reader.Close()

    $bw.Flush()
    $bytes = $ms.ToArray()
    $hashBytes = $sha.ComputeHash($bytes)
    $hashStr = [BitConverter]::ToString($hashBytes).Replace("-", "")

    $bw.Dispose()
    $ms.Dispose()
    $sha.Dispose()

    return [PSCustomObject]@{
        TotalRows = $count
        ActiveRows = $activeCount
        InactiveRows = $inactiveCount
        DeterministicSha256 = $hashStr
    }
}

# --- 2. Pure Helper: Child Environment Composition ---
function Get-LocalFirstChildEnvironment {
    param(
        [int]$Port,
        [string]$Local2026ConnStr,
        [string]$Local2027ConnStr,
        [string]$TokenKey,
        [bool]$IsTestMode = $false
    )

    if ($Port -le 0 -or $Port -gt 65535) {
        throw "INVALID_PORT: Port $Port is out of valid range (1-65535)."
    }
    if ([string]::IsNullOrWhiteSpace($Local2026ConnStr)) {
        throw "CONFIGURATION_ERROR: LocalConnection2026 cannot be null or whitespace."
    }
    if ([string]::IsNullOrWhiteSpace($Local2027ConnStr)) {
        throw "CONFIGURATION_ERROR: LocalConnection2027 cannot be null or whitespace."
    }
    if ([string]::IsNullOrWhiteSpace($TokenKey)) {
        throw "CONFIGURATION_ERROR: Token:Key cannot be null or whitespace."
    }

    # Tripwire remote connection strings to guarantee zero accidental Azure access
    $tripwireRemote = "Server=127.0.0.1;Database=DISABLED_REMOTE_TRIPWIRE;Integrated Security=True;TrustServerCertificate=True;"

    $envMap = [System.Collections.Generic.Dictionary[string, string]]::new()
    $envMap["ASPNETCORE_ENVIRONMENT"] = "Production"
    $envMap["ASPNETCORE_URLS"] = "http://127.0.0.1:$Port"
    $envMap["LocalFirst__Enabled"] = "true"
    $envMap["LocalFirst__ReadOnlyMode"] = "false"
    $envMap["Sync__PullEnabled"] = "false"
    $envMap["Sync__PushEnabled"] = "false"
    $envMap["Sync__AuthoritativeTrackingEnabled"] = "true"
    $envMap["LegacyMigration__Enabled"] = "false"

    # Operational local database connections
    $envMap["ConnectionStrings__LocalConnection2026"] = $Local2026ConnStr
    $envMap["ConnectionStrings__LocalConnection2027"] = $Local2027ConnStr

    # Remote fail-closed tripwires
    $envMap["ConnectionStrings__DefaultConnection"] = $tripwireRemote
    $envMap["ConnectionStrings__CON2027"] = $tripwireRemote

    # JWT key
    $envMap["Token__Key"] = $TokenKey

    return $envMap
}

# --- 3. Operational Preflight Gate ---
function Assert-LocalFirstLauncherPreflight {
    param(
        [string]$RepoRoot,
        [string]$ExpectedMasterSha,
        [string]$Local2026ConnStr,
        [string]$Local2027ConnStr,
        [bool]$SkipGit = $false,
        [bool]$IsTestMode = $false
    )

    # A. Git State
    if (-not $SkipGit) {
        $head = (git -C $RepoRoot rev-parse HEAD 2>$null)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
            throw "PREFLIGHT_FAIL: Unable to resolve local git HEAD commit."
        }
        $head = $head.ToString().Trim()
        if (-not [string]::IsNullOrWhiteSpace($ExpectedMasterSha) -and $head -ne $ExpectedMasterSha) {
            # Check if on an approved branch branched from ExpectedMasterSha
            $mergeBase = (git -C $RepoRoot merge-base HEAD $ExpectedMasterSha 2>$null)
            $mergeBaseStr = if ($mergeBase) { $mergeBase.ToString().Trim() } else { "" }
            if ($mergeBaseStr -ne $ExpectedMasterSha) {
                throw "PREFLIGHT_FAIL: Local HEAD ($head) does not descend from ExpectedMasterSha ($ExpectedMasterSha)."
            }
        }
    }

    # B. Committed Configuration Guard
    $appsettingsPath = Join-Path $RepoRoot "src\Api\appsettings.json"
    if (-not (Test-Path $appsettingsPath)) {
        throw "PREFLIGHT_FAIL: appsettings.json not found at '$appsettingsPath'."
    }
    $appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
    if ($appsettings.LocalFirst.Enabled -ne $false) {
        throw "PREFLIGHT_FAIL: Committed LocalFirst:Enabled in appsettings.json must be false."
    }
    if ($appsettings.Sync.PullEnabled -ne $false) {
        throw "PREFLIGHT_FAIL: Committed Sync:PullEnabled in appsettings.json must be false."
    }
    if ($appsettings.Sync.PushEnabled -ne $false) {
        throw "PREFLIGHT_FAIL: Committed Sync:PushEnabled in appsettings.json must be false."
    }
    if ($appsettings.Sync.AuthoritativeTrackingEnabled -ne $true) {
        throw "PREFLIGHT_FAIL: Committed Sync:AuthoritativeTrackingEnabled in appsettings.json must be true."
    }

    # C. Local Databases Preflight
    $pairs = @(
        @{ Year = "2026"; ConnStr = $Local2026ConnStr; ExpectedRows = 30; ExpectedHash = "9929AC6BFD1637B5F523726919C1CF5F66F5E1A7367DA0CBC236825E840D04B5" },
        @{ Year = "2027"; ConnStr = $Local2027ConnStr; ExpectedRows = 14; ExpectedHash = "359D518757795FDC33CE28C0678BC02AE81EC14A8CB5EE08FEBCF4A1B09BC3B1" }
    )

    foreach ($p in $pairs) {
        $yr = $p.Year
        $cs = $p.ConnStr
        if ($cs -match "(?i)\.database\.windows\.net") {
            throw "PREFLIGHT_FAIL: Local connection string for $yr detected forbidden Azure host."
        }

        $conn = New-Object SqlConnection($cs)
        try {
            $conn.Open()
        } catch {
            throw "PREFLIGHT_FAIL: Cannot connect to operational local database for ${yr}: $($_.Exception.Message)"
        }

        try {
            # 1. Compatibility level (120 for SQL Server 2014)
            if (-not $IsTestMode) {
                $cmdC = $conn.CreateCommand()
                $cmdC.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME();"
                $compat = [int]$cmdC.ExecuteScalar()
                if ($compat -ne 120) {
                    throw "PREFLIGHT_FAIL: Database for $yr has compatibility_level $compat (Expected 120 / SQL Server 2014)."
                }
            }

            # 2. BootstrapManifest
            $cmdM = $conn.CreateCommand()
            $cmdM.CommandText = "SELECT TOP 1 Status, IsWriteAllowed FROM [sync].[BootstrapManifest] ORDER BY Id DESC;"
            $rM = $cmdM.ExecuteReader()
            $mStatus = $null
            $mWriteAllowed = $false
            if ($rM.Read()) {
                $mStatus = [string]$rM["Status"]
                $mWriteAllowed = [bool]$rM["IsWriteAllowed"]
            }
            $rM.Close()
            if ($mStatus -ne "VERIFIED_READY" -or -not $mWriteAllowed) {
                throw "PREFLIGHT_FAIL: BootstrapManifest for $yr is not VERIFIED_READY with IsWriteAllowed=true (Status=$mStatus, WriteAllowed=$mWriteAllowed)."
            }

            # 3. LocalState Checkpoint & Lease
            $cmdS = $conn.CreateCommand()
            $cmdS.CommandText = "SELECT LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc FROM [sync].[LocalState] WHERE DatabaseId = @dbId;"
            $cmdS.Parameters.AddWithValue("@dbId", $yr) | Out-Null
            $rS = $cmdS.ExecuteReader()
            $ver = -1
            $lease = $null
            if ($rS.Read()) {
                $ver = [int64]$rS["LastServerVersion"]
                $lease = if ($rS["ActiveLeaseToken"] -ne [DBNull]::Value) { [string]$rS["ActiveLeaseToken"] } else { $null }
            }
            $rS.Close()
            if ($ver -lt 8) {
                throw "PREFLIGHT_FAIL: LocalState for $yr has LastServerVersion $ver (Expected >= 8 following catch-up)."
            }
            if (-not [string]::IsNullOrWhiteSpace($lease)) {
                throw "PREFLIGHT_FAIL: LocalState for $yr has active lease token '$lease'."
            }

            # 4. LocalOutbox Cleanliness
            $cmdO = $conn.CreateCommand()
            $cmdO.CommandText = "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE DatabaseId = @dbId AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');"
            $cmdO.Parameters.AddWithValue("@dbId", $yr) | Out-Null
            $pendingOutbox = [int]$cmdO.ExecuteScalar()
            if ($pendingOutbox -ne 0) {
                throw "PREFLIGHT_FAIL: LocalOutbox for $yr contains $pendingOutbox active/failed rows (Expected 0)."
            }

            # 5. Daily Hash & Count Parity
            if (-not $IsTestMode) {
                $stats = Calculate-DailyHashAndCounts $conn
                if ($stats.TotalRows -ne $p.ExpectedRows) {
                    throw "PREFLIGHT_FAIL: Daily TotalRows for $yr is $($stats.TotalRows) (Expected $($p.ExpectedRows))."
                }
                if ($stats.DeterministicSha256 -ne $p.ExpectedHash) {
                    throw "PREFLIGHT_FAIL: Daily SHA-256 hash for $yr mismatch. Got $($stats.DeterministicSha256), expected $($p.ExpectedHash)."
                }
            }

            # 6. AspNetUsers Existence
            $cmdU = $conn.CreateCommand()
            $cmdU.CommandText = "SELECT COUNT(*) FROM [dbo].[AspNetUsers];"
            $userCount = [int]$cmdU.ExecuteScalar()
            if ($userCount -le 0) {
                throw "PREFLIGHT_FAIL: AspNetUsers in local database $yr is empty."
            }
        } finally {
            $conn.Close()
        }
    }

    return $true
}

# --- 4. Process Helper: Test Port Liveness ---
function Test-PortAvailability {
    param([int]$Port)
    try {
        $tcp = New-Object TcpClient
        $tcp.Connect("127.0.0.1", $Port)
        $tcp.Close()
        return $false # Connected means port is IN USE
    } catch {
        return $true # Exception means port is FREE
    }
}

if ($ExportFunctionsOnly) {
    return
}

# ==============================================================================
# SCRIPT EXECUTION ENTRYPOINT
# ==============================================================================

$pidFile = if (-not [string]::IsNullOrWhiteSpace($OverridePidFilePath)) {
    $OverridePidFilePath
} else {
    Join-Path $PSScriptRoot ".localfirst_runtime.pid"
}

# Resolve Connection Strings
$appsettingsPath = Join-Path $repoRoot "src\Api\appsettings.json"
$appsettings = Get-Content $appsettingsPath -Raw | ConvertFrom-Json

$local2026ConnStr = if (-not [string]::IsNullOrWhiteSpace($OverrideLocal2026ConnStr)) {
    $OverrideLocal2026ConnStr
} else {
    $appsettings.ConnectionStrings.LocalConnection2026
}

$local2027ConnStr = if (-not [string]::IsNullOrWhiteSpace($OverrideLocal2027ConnStr)) {
    $OverrideLocal2027ConnStr
} else {
    $appsettings.ConnectionStrings.LocalConnection2027
}

# Resolve Token Key securely
$tokenKey = $null
if (-not [string]::IsNullOrWhiteSpace($env:Token__Key)) {
    $tokenKey = $env:Token__Key
} else {
    $apiProj = Join-Path $repoRoot "src\Api\Auth.Api.csproj"
    if (Test-Path $apiProj) {
        $secrets = dotnet user-secrets list --project $apiProj 2>$null
        foreach ($line in $secrets) {
            if ($line.StartsWith("Token:Key = ")) {
                $tokenKey = $line.Substring("Token:Key = ".Length).Trim()
            }
        }
    }
    if ([string]::IsNullOrWhiteSpace($tokenKey) -and $appsettings.Token -and -not [string]::IsNullOrWhiteSpace($appsettings.Token.Key)) {
        $tokenKey = $appsettings.Token.Key
    }
    if ([string]::IsNullOrWhiteSpace($tokenKey)) {
        $tokenKey = [System.Guid]::NewGuid().ToString("N") + [System.Guid]::NewGuid().ToString("N")
    }
}

switch ($Action) {
    "Status" {
        if (-not (Test-Path $pidFile)) {
            Write-Host "LocalFirst runtime is NOT RUNNING (no PID file found)." -ForegroundColor Yellow
            return [PSCustomObject]@{ Status = "NOT_RUNNING"; Pid = $null; Port = $null }
        }
        $raw = Get-Content $pidFile -Raw -ErrorAction SilentlyContinue
        $pidText = if ($raw) { $raw.Trim() } else { "" }
        if (-not $pidText) {
            Write-Host "LocalFirst runtime is NOT RUNNING (PID file is empty)." -ForegroundColor Yellow
            return [PSCustomObject]@{ Status = "NOT_RUNNING"; Pid = $null; Port = $null }
        }
        $runningProc = Get-Process -Id ([int]$pidText) -ErrorAction SilentlyContinue
        if ($null -eq $runningProc) {
            Write-Host "LocalFirst runtime PID file exists ($pidText) but process is DEAD." -ForegroundColor Red
            return [PSCustomObject]@{ Status = "STALE_PID"; Pid = [int]$pidText; Port = $null }
        }
        Write-Host "LocalFirst runtime is RUNNING (PID: $($runningProc.Id), ProcessName: $($runningProc.ProcessName))." -ForegroundColor Green
        return [PSCustomObject]@{ Status = "RUNNING"; Pid = $runningProc.Id; Port = $Port }
    }

    "Stop" {
        Write-Host "Stopping LocalFirst runtime..." -NoNewline
        if (-not (Test-Path $pidFile)) {
            Write-Host " Already stopped (no PID file)." -ForegroundColor Yellow
            return $true
        }
        $raw = Get-Content $pidFile -Raw -ErrorAction SilentlyContinue
        $pidText = if ($raw) { $raw.Trim() } else { "" }
        if ($pidText) {
            $runningProc = Get-Process -Id ([int]$pidText) -ErrorAction SilentlyContinue
            if ($runningProc) {
                $runningProc | Stop-Process -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 1
            }
        }
        Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
        Write-Host " Stopped $(if ($pidText) { "(PID: $pidText terminated)" } else { "(no active process)" })." -ForegroundColor Green
        return $true
    }

    "Restart" {
        Write-Host "Restarting LocalFirst runtime..." -ForegroundColor Cyan
        & $PSCommandPath Stop -Port $Port -ExpectedMasterSha $ExpectedMasterSha `
            -SkipGitVerification:$SkipGitVerification `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverridePidFilePath $OverridePidFilePath
        Start-Sleep -Seconds 2
        & $PSCommandPath Start -Port $Port -ExpectedMasterSha $ExpectedMasterSha `
            -SkipGitVerification:$SkipGitVerification `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverridePidFilePath $OverridePidFilePath
        return
    }

    "Run" {
        & $PSCommandPath Start -Port $Port -ExpectedMasterSha $ExpectedMasterSha `
            -SkipGitVerification:$SkipGitVerification `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverridePidFilePath $OverridePidFilePath -Wait
        return
    }

    "Start" {
        Write-Host "==========================================================================" -ForegroundColor Cyan
        Write-Host "  STARTING OPERATIONAL LOCALFIRST RUNTIME                                 " -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # 1. Check if already running
        if (Test-Path $pidFile) {
            $raw = Get-Content $pidFile -Raw -ErrorAction SilentlyContinue
            $existingPid = if ($raw) { $raw.Trim() } else { "" }
            if ($existingPid) {
                $proc = Get-Process -Id ([int]$existingPid) -ErrorAction SilentlyContinue
                if ($proc) {
                    Write-Host "LocalFirst runtime is ALREADY RUNNING (PID: $($proc.Id)) on port $Port." -ForegroundColor Yellow
                    Write-Host "Local URL: http://127.0.0.1:$Port" -ForegroundColor Green
                    return [PSCustomObject]@{ Status = "ALREADY_RUNNING"; Pid = $proc.Id; Port = $Port; Url = "http://127.0.0.1:$Port" }
                }
            }
            Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
        }

        # 2. Port Collision Guard
        Write-Host "Checking port $Port availability..." -NoNewline
        $isPortFree = Test-PortAvailability -Port $Port
        if (-not $isPortFree) {
            Write-Host " OCCUPIED" -ForegroundColor Red
            throw "PORT_IN_USE: Port $Port is currently occupied by an external process. Per safety rules, will not terminate foreign processes. Specify a different -Port or release port $Port."
        }
        Write-Host " Available." -ForegroundColor Green

        # 3. Operational Preflight Gate
        Write-Host "Executing operational LocalFirst preflight validation..." -NoNewline
        Assert-LocalFirstLauncherPreflight -RepoRoot $repoRoot `
            -ExpectedMasterSha $ExpectedMasterSha `
            -Local2026ConnStr $local2026ConnStr `
            -Local2027ConnStr $local2027ConnStr `
            -SkipGit $SkipGitVerification `
            -IsTestMode $AllowIsolatedTestMode | Out-Null
        Write-Host " PASS." -ForegroundColor Green

        # 4. Compose Process-Local Environment
        $childEnv = Get-LocalFirstChildEnvironment -Port $Port `
            -Local2026ConnStr $local2026ConnStr `
            -Local2027ConnStr $local2027ConnStr `
            -TokenKey $tokenKey `
            -IsTestMode $AllowIsolatedTestMode

        # Save snapshot of current process environment before launching
        $envSnapshot = @{}
        foreach ($k in $childEnv.Keys) {
            if (Test-Path "Env:\$k") {
                $envSnapshot[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
            }
            [Environment]::SetEnvironmentVariable($k, $childEnv[$k], "Process")
        }

        $apiDll = Join-Path $repoRoot "src\Api\bin\Release\net10.0\Auth.Api.dll"
        if (-not (Test-Path $apiDll)) {
            throw "ARTIFACT_MISSING: Application binary not found at '$apiDll'. Run 'dotnet build -c Release' first."
        }

        $tempOut = [System.IO.Path]::GetTempFileName()
        $tempErr = [System.IO.Path]::GetTempFileName()

        try {
            Write-Host "Launching local application process..." -NoNewline
            $proc = Start-Process -FilePath "dotnet" `
                -ArgumentList "`"$apiDll`"" `
                -WorkingDirectory (Join-Path $repoRoot "src\Api") `
                -PassThru `
                -NoNewWindow `
                -RedirectStandardOutput $tempOut `
                -RedirectStandardError $tempErr

            $proc.Id | Out-File -FilePath $pidFile -Force
            Write-Host " Started (PID: $($proc.Id))." -ForegroundColor Green

            # Wait for health endpoint readiness
            Write-Host "Waiting for http://127.0.0.1:$Port/health readiness..." -NoNewline
            $healthUrl = "http://127.0.0.1:$Port/health"
            $ready = $false
            $maxWaitSec = 30
            for ($s = 1; $s -le ($maxWaitSec * 2); $s++) {
                if ($proc.HasExited) {
                    $errText = if (Test-Path $tempErr) { Get-Content $tempErr -Raw } else { "" }
                    throw "LOCALFIRST_STARTUP_FAILED: Process exited prematurely with code $($proc.ExitCode). Error: $errText"
                }
                try {
                    $resp = Invoke-WebRequest -Uri $healthUrl -Method Get -TimeoutSec 2 -UseBasicParsing
                    if ($resp.StatusCode -eq 200) {
                        $ready = $true
                        break
                    }
                } catch {
                    Start-Sleep -Milliseconds 500
                }
            }

            if (-not $ready) {
                throw "LOCALFIRST_STARTUP_TIMEOUT: Application failed to respond at $healthUrl within $maxWaitSec seconds."
            }

            Write-Host " HEALTHY." -ForegroundColor Green
            Write-Host "`n==========================================================================" -ForegroundColor Green
            Write-Host "  LOCALFIRST RUNTIME ACTIVATED SUCCESSFULLY                              " -ForegroundColor Green
            Write-Host "==========================================================================" -ForegroundColor Green
            Write-Host "Local URL:             http://127.0.0.1:$Port"
            Write-Host "Operational Databases: IProgramLocalDb2026, IProgramLocalDb2027"
            Write-Host "Runtime Mode:          LocalFirst=True, ReadOnly=False"
            Write-Host "Sync Status:           Pull=Disabled, Push=Disabled (Manual Sync Only)"
            Write-Host "Azure Connections:     BLOCKED (Loopback Tripwire Active)"
            Write-Host "Process PID:           $($proc.Id)"
            Write-Host "==========================================================================" -ForegroundColor Green

            if ($Wait) {
                Write-Host "LocalFirst runtime running in foreground (Ctrl+C or Stop command to terminate)..."
                try {
                    $proc.WaitForExit()
                } finally {
                    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
                }
                return
            }

            return [PSCustomObject]@{
                Status = "RUNNING"
                Pid = $proc.Id
                Port = $Port
                Url = "http://127.0.0.1:$Port"
            }
        } finally {
            # Restore parent process environment variables
            foreach ($k in $childEnv.Keys) {
                if ($envSnapshot.ContainsKey($k)) {
                    [Environment]::SetEnvironmentVariable($k, $envSnapshot[$k], "Process")
                } else {
                    [Environment]::SetEnvironmentVariable($k, $null, "Process")
                }
            }
        }
    }
}
