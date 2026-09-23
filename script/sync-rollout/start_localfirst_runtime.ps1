# ==============================================================================
# SLICE 4.5E: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER (REVISED)
# Safe, repeatable, fail-closed operator tool for starting, stopping, and
# monitoring the LocalFirst runtime profile on the operational workstation.
#
# OPERATIONAL INVARIANTS:
#   1. Process-local configuration only: Never edits committed appsettings.json.
#   2. Local loopback binding: 127.0.0.1 only (default port 5000).
#   3. Zero automatic sync: Pull=false, Push=false, No background workers.
#   4. Tripwire remote connections: DefaultConnection and CON2027 point to
#      loopback tripwires to guarantee zero silent Azure fallback.
#   5. Preflight validation:
#      - Steady-state: Verifies SQL 2014 compatibility (level 120), VERIFIED_READY
#        BootstrapManifest, LastServerVersion >= 8, no active lease, zero IN_PROGRESS/
#        FAILED outbox rows (PENDING outbox rows explicitly allowed as normal offline state).
#      - Initial Cutover Baseline (-ValidateInitialCutoverBaseline): Additionally enforces
#        0 pending outbox rows and exact cryptographic Daily hashes.
#   6. Git Safety Gate: Requires exact 'master' branch, clean working tree, and
#      match against live origin/master (via git ls-remote) in operational mode.
#   7. Physical Local Binding: Parses with SqlConnectionStringBuilder to enforce
#      trusted localhost endpoints and exact operational catalogs (IProgramLocalDb2026/2027).
#      CLI overrides forbidden in operational mode.
#   8. Token Secret Boundary: Requires Token__Key from process/user environment.
#      Never invokes 'dotnet user-secrets list' or generates random operational keys.
#   9. Structured PID Safety: Records structured runtime metadata (PID, StartTimeUtc,
#      ProcessName, Port, CommitSha) in JSON. Never terminates foreign processes on PID reuse.
#  10. Artifact Identity: Ensures Auth.Api.dll is present and built locally before launch.
#  11. Log Lifecycle: Writes to tracked logs directory with sanitization.
# ==============================================================================

using namespace System.Data.SqlClient
using namespace System.Net.Sockets

[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [ValidateSet("Start", "Stop", "Status", "Restart", "Run")]
    [string]$Action = "Start",

    [int]$Port = 5000,
    [switch]$ValidateInitialCutoverBaseline,
    [switch]$LocalOnlyProduction,
    [switch]$SkipGitVerification,
    [switch]$AllowNonMaster,
    [switch]$AllowIsolatedTestMode,
    [string]$OverrideLocal2026ConnStr,
    [string]$OverrideLocal2027ConnStr,
    [string]$OverrideStateFilePath,
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
        [bool]$IsTestMode = $false,
        [bool]$LocalOnlyProduction = $false
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
    if ($LocalOnlyProduction) {
        $envMap["LocalFirst__LocalOnlyProduction"] = "true"
        $envMap["LocalFirst__Mode"] = "LocalOnlyProduction"
        $envMap["Sync__AuthoritativeTrackingEnabled"] = "false"
    } else {
        $envMap["Sync__AuthoritativeTrackingEnabled"] = "true"
    }
    $envMap["Sync__PullEnabled"] = "false"
    $envMap["Sync__PushEnabled"] = "false"
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

# --- 3. Physical Local Binding Validation (P0-3) ---
function Assert-LocalPhysicalBinding {
    param(
        [string]$ConnStr,
        [string]$ExpectedYear,
        [bool]$IsTestMode = $false
    )

    if ([string]::IsNullOrWhiteSpace($ConnStr)) {
        throw "PREFLIGHT_FAIL: Connection string for year $ExpectedYear is null or empty."
    }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($ConnStr)
    $dataSource = $builder.DataSource
    $catalog = $builder.InitialCatalog

    # Check for Azure host or remote cloud host
    if ($dataSource -match "(?i)\.database\.windows\.net" -or $ConnStr -match "(?i)\.database\.windows\.net") {
        throw "PREFLIGHT_FAIL: Connection string for $ExpectedYear targets forbidden Azure host: '$dataSource'."
    }

    # Validate loopback / trusted local endpoint
    $isLocal = ($dataSource -match "^(?i)(localhost|127\.0\.0\.1|\.|\(local\)|\(localdb\))(\\.*)?(,\d+)?$")
    if (-not $isLocal) {
        throw "PREFLIGHT_FAIL: Connection string for $ExpectedYear targets non-local host: '$dataSource'. Only localhost/loopback endpoints permitted."
    }

    # Validate catalog
    if (-not $IsTestMode) {
        $expectedCatalog = if ($ExpectedYear -eq "2026") { "IProgramLocalDb2026" } else { "IProgramLocalDb2027" }
        if ($catalog -ne $expectedCatalog) {
            throw "PREFLIGHT_FAIL: Operational connection string for $ExpectedYear must target catalog '$expectedCatalog' (Got: '$catalog')."
        }
    } else {
        # In test mode, operational catalogs are STRICTLY FORBIDDEN to ensure complete fixture isolation
        if ($catalog -in @("IProgramLocalDb2026", "IProgramLocalDb2027", "IProgramDb2026", "IProgramDb2027")) {
            throw "PREFLIGHT_FAIL: Test mode is forbidden from targeting operational catalog '$catalog'."
        }
    }

    return $builder
}

# --- 4. Git Operational Safety Gate (P0-2 & P0-A) ---
function Assert-GitOperationalSafety {
    param(
        [string]$RepoRoot,
        [bool]$SkipGit = $false,
        [bool]$AllowNonMaster = $false,
        [bool]$IsTestMode = $false
    )

    if (-not $IsTestMode) {
        if ($SkipGit) {
            throw "CLI_OVERRIDE_FORBIDDEN: -SkipGitVerification is strictly forbidden in operational mode. Operational LocalFirst runtime must verify git master parity. Pass -AllowIsolatedTestMode for test fixtures."
        }
        if ($AllowNonMaster) {
            throw "CLI_OVERRIDE_FORBIDDEN: -AllowNonMaster is strictly forbidden in operational mode. Operational LocalFirst runtime must run exclusively from 'master'. Pass -AllowIsolatedTestMode for test fixtures."
        }

        # 1. Branch must be master
        $branch = (git -C $RepoRoot branch --show-current 2>$null)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($branch)) {
            throw "PREFLIGHT_FAIL: Unable to determine git branch."
        }
        $branch = $branch.ToString().Trim()
        if ($branch -ne "master") {
            throw "PREFLIGHT_FAIL: Operational LocalFirst runtime must run from 'master' branch (Current branch: '$branch')."
        }

        # 2. Clean working tree check
        $status = (git -C $RepoRoot status --porcelain 2>$null)
        if (-not [string]::IsNullOrWhiteSpace($status)) {
            throw "PREFLIGHT_FAIL: Working tree has uncommitted modifications. Operational LocalFirst runtime requires a clean working tree."
        }

        # 3. Resolve live origin/master
        $remoteRef = (git -C $RepoRoot ls-remote origin refs/heads/master 2>$null)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($remoteRef)) {
            throw "PREFLIGHT_FAIL: Unable to resolve live origin/master via git ls-remote. Cannot verify operational master parity."
        }
        $remoteSha = ($remoteRef.Split("`t")[0]).Trim()

        $localHead = (git -C $RepoRoot rev-parse HEAD 2>$null)
        $localHead = if ($localHead) { $localHead.ToString().Trim() } else { "" }

        if ($localHead -ne $remoteSha) {
            throw "PREFLIGHT_FAIL: Local HEAD ($localHead) does not match live origin/master ($remoteSha)."
        }
    } else {
        if ($SkipGit) { return $true }
        if ($AllowNonMaster) {
            # In test mode with AllowNonMaster, verify local HEAD exists
            $localHead = (git -C $RepoRoot rev-parse HEAD 2>$null)
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($localHead)) {
                throw "PREFLIGHT_FAIL: Unable to resolve local git HEAD commit."
            }
            return $true
        }
    }

    return $true
}

# --- 5. Operational Preflight Gate (P0-1 & Steady-State) ---
function Assert-LocalFirstLauncherPreflight {
    param(
        [string]$RepoRoot,
        [string]$Local2026ConnStr,
        [string]$Local2027ConnStr,
        [bool]$ValidateInitialCutoverBaseline = $false,
        [bool]$SkipGit = $false,
        [bool]$AllowNonMaster = $false,
        [bool]$IsTestMode = $false
    )

    # A. Git State
    Assert-GitOperationalSafety -RepoRoot $RepoRoot -SkipGit $SkipGit -AllowNonMaster $AllowNonMaster -IsTestMode $IsTestMode | Out-Null

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
        Assert-LocalPhysicalBinding -ConnStr $cs -ExpectedYear $yr -IsTestMode $IsTestMode | Out-Null

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

            # 4. LocalOutbox Check
            $cmdO = $conn.CreateCommand()
            $cmdO.CommandText = "SELECT Status, COUNT(*) as Cnt FROM [sync].[LocalOutbox] WHERE DatabaseId = @dbId GROUP BY Status;"
            $cmdO.Parameters.AddWithValue("@dbId", $yr) | Out-Null
            $rO = $cmdO.ExecuteReader()
            $pendingCnt = 0
            $inProgressCnt = 0
            $failedCnt = 0
            while ($rO.Read()) {
                $st = [string]$rO["Status"]
                $cnt = [int]$rO["Cnt"]
                if ($st -eq "PENDING") { $pendingCnt = $cnt }
                elseif ($st -eq "IN_PROGRESS") { $inProgressCnt = $cnt }
                elseif ($st -eq "FAILED") { $failedCnt = $cnt }
            }
            $rO.Close()

            # Crash recovery / failure rules: IN_PROGRESS or FAILED rows block startup fail-closed
            if ($inProgressCnt -gt 0) {
                throw "PREFLIGHT_FAIL: LocalOutbox for $yr contains $inProgressCnt IN_PROGRESS rows. A previous sync was interrupted or crashed."
            }
            if ($failedCnt -gt 0) {
                throw "PREFLIGHT_FAIL: LocalOutbox for $yr contains $failedCnt FAILED rows. Operator attention required."
            }

            # Initial Cutover Baseline Mode: Requires 0 pending outbox and exact cryptographic hash
            if ($ValidateInitialCutoverBaseline) {
                if ($pendingCnt -ne 0) {
                    throw "PREFLIGHT_FAIL: Initial Cutover Baseline requires 0 pending outbox rows (Found $pendingCnt for $yr)."
                }
                if (-not $IsTestMode) {
                    $stats = Calculate-DailyHashAndCounts $conn
                    if ($stats.TotalRows -ne $p.ExpectedRows) {
                        throw "PREFLIGHT_FAIL: Daily TotalRows for $yr is $($stats.TotalRows) (Expected $($p.ExpectedRows))."
                    }
                    if ($stats.DeterministicSha256 -ne $p.ExpectedHash) {
                        throw "PREFLIGHT_FAIL: Daily SHA-256 hash for $yr mismatch. Got $($stats.DeterministicSha256), expected $($p.ExpectedHash)."
                    }
                }
            } else {
                # Steady-State: PENDING outbox rows are a normal offline state resulting from legitimate Daily edits
                Write-Verbose "Steady-state outbox status for $($yr): Pending = $pendingCnt, InProgress = 0, Failed = 0."
            }

            # 5. AspNetUsers Existence
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

# --- 6. Process Helper: Test Port Liveness ---
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

# --- 7. Structured Runtime State Helpers (P0-5) ---
function Get-RuntimeState($Path) {
    if (-not (Test-Path $Path)) { return $null }
    try {
        $raw = Get-Content $Path -Raw -ErrorAction SilentlyContinue
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Assert-ProcessMatchesState($proc, $state) {
    if ($null -eq $proc -or $null -eq $state) { return $false }
    if ($proc.Id -ne $state.pid) { return $false }
    
    # Process name check (must match dotnet)
    if ($proc.ProcessName -notmatch "^(?i)dotnet$") {
        return $false
    }

    # StartTime check (compare in UTC within 5s margin for process launch tolerance)
    try {
        $procStartUtc = $proc.StartTime.ToUniversalTime()
        $stateStartUtc = [DateTime]::Parse($state.startTimeUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AdjustToUniversal)
        $diffSeconds = [Math]::Abs(($procStartUtc - $stateStartUtc).TotalSeconds)
        if ($diffSeconds -gt 5) {
            return $false
        }
    } catch {
        return $false
    }

    return $true
}

# --- 7. Artifact Identity & Deterministic Build (P0-7 & P0-B) ---
function Assert-LocalReleaseArtifact {
    param(
        [string]$RepoRoot,
        [bool]$IsTestMode = $false
    )

    $apiDll = Join-Path $RepoRoot "src\Api\bin\Release\net10.0\Auth.Api.dll"
    $apiProj = Join-Path $RepoRoot "src\Api\Auth.Api.csproj"

    if (-not $IsTestMode) {
        Write-Host "Building Release binary locally from current verified commit..." -NoNewline
        $buildOut = (& dotnet build $apiProj -c Release --no-restore 2>&1)
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $apiDll)) {
            Write-Host " FAILED" -ForegroundColor Red
            throw "BUILD_FAILED: Local Release build failed. Cannot launch operational runtime.`n$buildOut"
        }
        Write-Host " Done." -ForegroundColor Green
    } else {
        if (-not (Test-Path $apiDll)) {
            $buildOut = (& dotnet build $apiProj -c Release --no-restore 2>&1)
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $apiDll)) {
                throw "BUILD_FAILED: Local Release build failed. Cannot launch runtime.`n$buildOut"
            }
        }
    }

    return $apiDll
}

if ($ExportFunctionsOnly) {
    return
}

# ==============================================================================
# SCRIPT EXECUTION ENTRYPOINT
# ==============================================================================

# Resolve Structured State File Path
$stateFilePath = if (-not [string]::IsNullOrWhiteSpace($OverrideStateFilePath)) {
    $OverrideStateFilePath
} elseif (-not [string]::IsNullOrWhiteSpace($OverridePidFilePath)) {
    $OverridePidFilePath
} else {
    Join-Path $PSScriptRoot ".localfirst_runtime_state.json"
}

# CLI Parameter Overrides Guard (P0-3 & P0-A)
if (-not $AllowIsolatedTestMode) {
    if ($SkipGitVerification) {
        throw "CLI_OVERRIDE_FORBIDDEN: -SkipGitVerification is strictly forbidden in operational mode. Operational LocalFirst runtime must verify git master parity. Pass -AllowIsolatedTestMode for test fixtures."
    }
    if ($AllowNonMaster) {
        throw "CLI_OVERRIDE_FORBIDDEN: -AllowNonMaster is strictly forbidden in operational mode. Operational LocalFirst runtime must run exclusively from 'master'. Pass -AllowIsolatedTestMode for test fixtures."
    }
    if (-not [string]::IsNullOrWhiteSpace($OverrideLocal2026ConnStr) -or -not [string]::IsNullOrWhiteSpace($OverrideLocal2027ConnStr)) {
        throw "CLI_OVERRIDE_FORBIDDEN: Connection string CLI overrides are strictly forbidden in operational mode to prevent secrets and foreign topologies from shell history. In operational mode, connections are loaded exclusively from appsettings.json. Pass -AllowIsolatedTestMode for test fixtures."
    }
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

# Resolve Token Key securely without enumerating user-secrets (P0-4)
$tokenKey = $null
if ($Action -in @("Start", "Run")) {
    if (-not [string]::IsNullOrWhiteSpace($env:Token__Key)) {
        $tokenKey = $env:Token__Key
    } elseif (-not [string]::IsNullOrWhiteSpace($env:Token_Key)) {
        $tokenKey = $env:Token_Key
    }

    if ([string]::IsNullOrWhiteSpace($tokenKey)) {
        if ($AllowIsolatedTestMode) {
            $tokenKey = "IsolatedTestTokenKeyForTestingOnly_32_characters_minimum_length_required_123"
        } else {
            throw "TOKEN_KEY_REQUIRED: Token__Key environment variable is required in operational mode. Set `$env:Token__Key` before launching. Enumerating user-secrets is blocked to prevent exposing production Azure credentials."
        }
    }
}

switch ($Action) {
    "Status" {
        $state = Get-RuntimeState $stateFilePath
        if ($null -eq $state) {
            Write-Host "LocalFirst runtime is NOT RUNNING (no state file found)." -ForegroundColor Yellow
            return [PSCustomObject]@{ Status = "NOT_RUNNING"; Pid = $null; Port = $null }
        }
        $candidateProc = Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
        if ($null -eq $candidateProc) {
            Write-Host "LocalFirst runtime state file exists (PID: $($state.pid)) but process is DEAD." -ForegroundColor Red
            return [PSCustomObject]@{ Status = "STALE_PID"; Pid = [int]$state.pid; Port = $state.port }
        }
        if (-not (Assert-ProcessMatchesState $candidateProc $state)) {
            Write-Host "FOREIGN_PID_REUSED: PID $($state.pid) exists but does NOT match recorded runtime identity (ProcessName: $($candidateProc.ProcessName), StartTime: $($candidateProc.StartTime)). Foreign process will NOT be touched." -ForegroundColor Red
            return [PSCustomObject]@{ Status = "FOREIGN_PID_REUSED"; Pid = $candidateProc.Id; Port = $state.port }
        }
        Write-Host "LocalFirst runtime is RUNNING (PID: $($candidateProc.Id), Port: $($state.port))." -ForegroundColor Green
        return [PSCustomObject]@{ Status = "RUNNING"; Pid = $candidateProc.Id; Port = $state.port }
    }

    "Stop" {
        Write-Host "Stopping LocalFirst runtime..." -NoNewline
        $state = Get-RuntimeState $stateFilePath
        if ($null -eq $state) {
            Write-Host " Already stopped (no state file)." -ForegroundColor Yellow
            return $true
        }
        $candidateProc = Get-Process -Id ([int]$state.pid) -ErrorAction SilentlyContinue
        if ($candidateProc) {
            if (-not (Assert-ProcessMatchesState $candidateProc $state)) {
                Write-Host " FOREIGN_PID_DETECTED: PID $($state.pid) is occupied by a foreign process ($($candidateProc.ProcessName)). REFUSING TO KILL FOREIGN PROCESS." -ForegroundColor Red
                Remove-Item $stateFilePath -Force -ErrorAction SilentlyContinue
                throw "SAFETY_REFUSAL: Process $($state.pid) does not match runtime identity. Foreign process will not be terminated."
            }
            $candidateProc | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
        Remove-Item $stateFilePath -Force -ErrorAction SilentlyContinue
        Write-Host " Stopped (PID: $($state.pid) terminated safely)." -ForegroundColor Green
        return $true
    }

    "Restart" {
        Write-Host "Restarting LocalFirst runtime..." -ForegroundColor Cyan
        & $PSCommandPath Stop -Port $Port `
            -SkipGitVerification:$SkipGitVerification `
            -AllowNonMaster:$AllowNonMaster `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverrideStateFilePath $stateFilePath
        Start-Sleep -Seconds 2
        & $PSCommandPath Start -Port $Port `
            -ValidateInitialCutoverBaseline:$ValidateInitialCutoverBaseline `
            -LocalOnlyProduction:$LocalOnlyProduction `
            -SkipGitVerification:$SkipGitVerification `
            -AllowNonMaster:$AllowNonMaster `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverrideStateFilePath $stateFilePath
        return
    }

    "Run" {
        & $PSCommandPath Start -Port $Port `
            -ValidateInitialCutoverBaseline:$ValidateInitialCutoverBaseline `
            -LocalOnlyProduction:$LocalOnlyProduction `
            -SkipGitVerification:$SkipGitVerification `
            -AllowNonMaster:$AllowNonMaster `
            -AllowIsolatedTestMode:$AllowIsolatedTestMode `
            -OverrideLocal2026ConnStr $OverrideLocal2026ConnStr `
            -OverrideLocal2027ConnStr $OverrideLocal2027ConnStr `
            -OverrideStateFilePath $stateFilePath -Wait
        return
    }

    "Start" {
        Write-Host "==========================================================================" -ForegroundColor Cyan
        Write-Host "  STARTING OPERATIONAL LOCALFIRST RUNTIME                                 " -ForegroundColor Cyan
        Write-Host "==========================================================================" -ForegroundColor Cyan

        # 1. Check if already running
        $existingState = Get-RuntimeState $stateFilePath
        if ($null -ne $existingState) {
            $existingProc = Get-Process -Id ([int]$existingState.pid) -ErrorAction SilentlyContinue
            if ($existingProc -and (Assert-ProcessMatchesState $existingProc $existingState)) {
                Write-Host "LocalFirst runtime is ALREADY RUNNING (PID: $($existingProc.Id)) on port $Port." -ForegroundColor Yellow
                Write-Host "Local URL: http://127.0.0.1:$Port" -ForegroundColor Green
                return [PSCustomObject]@{ Status = "ALREADY_RUNNING"; Pid = $existingProc.Id; Port = $Port; Url = "http://127.0.0.1:$Port" }
            } else {
                Remove-Item $stateFilePath -Force -ErrorAction SilentlyContinue
            }
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
            -Local2026ConnStr $local2026ConnStr `
            -Local2027ConnStr $local2027ConnStr `
            -ValidateInitialCutoverBaseline:$ValidateInitialCutoverBaseline `
            -SkipGit $SkipGitVerification `
            -AllowNonMaster $AllowNonMaster `
            -IsTestMode $AllowIsolatedTestMode | Out-Null
        Write-Host " PASS." -ForegroundColor Green

        # 4. Compose Process-Local Environment
        $childEnv = Get-LocalFirstChildEnvironment -Port $Port `
            -Local2026ConnStr $local2026ConnStr `
            -Local2027ConnStr $local2027ConnStr `
            -TokenKey $tokenKey `
            -IsTestMode $AllowIsolatedTestMode `
            -LocalOnlyProduction:$LocalOnlyProduction

        # Save snapshot of current process environment before launching
        $envSnapshot = @{}
        foreach ($k in $childEnv.Keys) {
            if (Test-Path "Env:\$k") {
                $envSnapshot[$k] = [Environment]::GetEnvironmentVariable($k, "Process")
            }
            [Environment]::SetEnvironmentVariable($k, $childEnv[$k], "Process")
        }

        # 5. Artifact Identity & Verification (P0-7 & P0-B)
        $apiDll = Assert-LocalReleaseArtifact -RepoRoot $repoRoot -IsTestMode $AllowIsolatedTestMode

        # 6. Log Directory & File (P1)
        $logsDir = Join-Path $PSScriptRoot "logs"
        if (-not (Test-Path $logsDir)) {
            New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
        }
        $timestampStr = (Get-Date).ToString("yyyyMMdd_HHmmss")
        $logPath = Join-Path $logsDir "runtime_${Port}_${timestampStr}.log"
        $errPath = Join-Path $logsDir "runtime_${Port}_${timestampStr}.err.log"

        $currentCommit = (git -C $repoRoot rev-parse HEAD 2>$null)
        $currentCommit = if ($currentCommit) { $currentCommit.ToString().Trim() } else { "UNKNOWN" }

        try {
            Write-Host "Launching local application process..." -NoNewline
            $proc = Start-Process -FilePath "dotnet" `
                -ArgumentList "`"$apiDll`"" `
                -WorkingDirectory (Join-Path $repoRoot "src\Api") `
                -PassThru `
                -NoNewWindow `
                -RedirectStandardOutput $logPath `
                -RedirectStandardError $errPath

            # Record Structured Runtime State (P0-5)
            $runtimeState = [ordered]@{
                pid          = $proc.Id
                startTimeUtc = $proc.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", [System.Globalization.CultureInfo]::InvariantCulture)
                processName  = $proc.ProcessName
                port         = $Port
                gitCommitSha = $currentCommit
                logPath      = $logPath
                errPath      = $errPath
            }
            $runtimeState | ConvertTo-Json -Depth 5 | Out-File -FilePath $stateFilePath -Force
            Write-Host " Started (PID: $($proc.Id))." -ForegroundColor Green

            # Wait for health endpoint readiness
            Write-Host "Waiting for http://127.0.0.1:$Port/health readiness..." -NoNewline
            $healthUrl = "http://127.0.0.1:$Port/health"
            $ready = $false
            $maxWaitSec = 30
            for ($s = 1; $s -le ($maxWaitSec * 2); $s++) {
                if ($proc.HasExited) {
                    $errText = if (Test-Path $errPath) { Get-Content $errPath -Raw } else { "" }
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
            Write-Host "State File:            $stateFilePath"
            Write-Host "Log Output:            $logPath"
            Write-Host "==========================================================================" -ForegroundColor Green

            if ($Wait) {
                Write-Host "LocalFirst runtime running in foreground (Ctrl+C or Stop command to terminate)..."
                try {
                    $proc.WaitForExit()
                } finally {
                    Remove-Item $stateFilePath -Force -ErrorAction SilentlyContinue
                }
                return
            }

            return [PSCustomObject]@{
                Status = "RUNNING"
                Pid    = $proc.Id
                Port   = $Port
                Url    = "http://127.0.0.1:$Port"
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
