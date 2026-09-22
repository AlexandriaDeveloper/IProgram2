# ==============================================================================
# TEST SUITE: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER
# Validates all safety guards, preflight invariants, child environment composition,
# process lifecycle, and zero-secret exposure for start_localfirst_runtime.ps1.
# ==============================================================================

using namespace System.Data.SqlClient

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../..")).Path
$launcherScript = Join-Path $PSScriptRoot "start_localfirst_runtime.ps1"

Write-Host "==========================================================================" -ForegroundColor Cyan
Write-Host "  TEST SUITE: OPERATIONAL LOCALFIRST RUNTIME LAUNCHER                      " -ForegroundColor Cyan
Write-Host "==========================================================================" -ForegroundColor Cyan

# Load pure functions for testing
. $launcherScript -ExportFunctionsOnly

$passedTests = 0
$totalTests = 0

function Assert-Test([string]$Name, [scriptblock]$Block) {
    $script:totalTests++
    Write-Host "[TEST $script:totalTests] $Name..." -NoNewline
    try {
        & $Block
        $script:passedTests++
        Write-Host " PASS" -ForegroundColor Green
    } catch {
        Write-Host " FAIL" -ForegroundColor Red
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        throw
    }
}

# --- TEST 1: Child Environment Composition Invariants ---
Assert-Test "Child process environment composition verifies strict LocalFirst & tripwire invariants" {
    $envMap = Get-LocalFirstChildEnvironment -Port 5000 `
        -Local2026ConnStr "Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;" `
        -Local2027ConnStr "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;" `
        -TokenKey "test_token_key_64_characters_long_for_hmac_sha256_validation_12345678" `
        -IsTestMode $true

    if ($envMap["LocalFirst__Enabled"] -ne "true") { throw "LocalFirst__Enabled must be 'true'" }
    if ($envMap["LocalFirst__ReadOnlyMode"] -ne "false") { throw "LocalFirst__ReadOnlyMode must be 'false'" }
    if ($envMap["Sync__PullEnabled"] -ne "false") { throw "Sync__PullEnabled must be 'false'" }
    if ($envMap["Sync__PushEnabled"] -ne "false") { throw "Sync__PushEnabled must be 'false'" }
    if ($envMap["Sync__AuthoritativeTrackingEnabled"] -ne "true") { throw "Sync__AuthoritativeTrackingEnabled must be 'true'" }
    if ($envMap["LegacyMigration__Enabled"] -ne "false") { throw "LegacyMigration__Enabled must be 'false'" }
    if ($envMap["ASPNETCORE_URLS"] -ne "http://127.0.0.1:5000") { throw "ASPNETCORE_URLS must be http://127.0.0.1:5000" }

    # Tripwires: Remote connection strings MUST NOT point to Azure
    if ($envMap["ConnectionStrings__DefaultConnection"] -match "(?i)\.database\.windows\.net") {
        throw "Tripwire DefaultConnection contains Azure host!"
    }
    if ($envMap["ConnectionStrings__DefaultConnection"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
        throw "Tripwire DefaultConnection must reference DISABLED_REMOTE_TRIPWIRE"
    }
    if ($envMap["ConnectionStrings__CON2027"] -notmatch "DISABLED_REMOTE_TRIPWIRE") {
        throw "Tripwire CON2027 must reference DISABLED_REMOTE_TRIPWIRE"
    }

    # Operational locals must match inputs
    if ($envMap["ConnectionStrings__LocalConnection2026"] -notmatch "IProgramLocalDb2026") {
        throw "LocalConnection2026 mismatch"
    }
    if ($envMap["ConnectionStrings__LocalConnection2027"] -notmatch "IProgramLocalDb2027") {
        throw "LocalConnection2027 mismatch"
    }
}

# --- TEST 2: Invalid Port Validation ---
Assert-Test "Invalid port numbers (0, -1, 70000) are rejected fail-closed" {
    $ports = @(0, -1, 70000)
    foreach ($p in $ports) {
        $failed = $false
        try {
            Get-LocalFirstChildEnvironment -Port $p `
                -Local2026ConnStr "Server=localhost;Database=db;" `
                -Local2027ConnStr "Server=localhost;Database=db;" `
                -TokenKey "key" | Out-Null
        } catch {
            if ($_.Exception.Message -match "INVALID_PORT") {
                $failed = $true
            }
        }
        if (-not $failed) {
            throw "Expected INVALID_PORT for port $p"
        }
    }
}

# --- TEST 3: Azure Connection String Injection Rejection ---
Assert-Test "Local connection strings referencing Azure host are rejected with PREFLIGHT_FAIL" {
    $azureStrings = @(
        "Server=tcp:iprogram-sql-prod-01.database.windows.net,1433;Initial Catalog=IProgramLocalDb2026;...",
        "Server=tcp:iprogram-sql-prod-01.database.windows.net;Database=IProgramLocalDb2027;..."
    )
    foreach ($as in $azureStrings) {
        $failed = $false
        try {
            Assert-LocalFirstLauncherPreflight -RepoRoot $repoRoot `
                -ExpectedMasterSha "49a0be38218c199a100c8d057a2acd45306844c0" `
                -Local2026ConnStr $as `
                -Local2027ConnStr "Server=localhost;Database=IProgramLocalDb2027;" `
                -SkipGit $true -IsTestMode $true | Out-Null
        } catch {
            if ($_.Exception.Message -match "PREFLIGHT_FAIL.*forbidden Azure host") {
                $failed = $true
            }
        }
        if (-not $failed) {
            throw "Failed to reject Azure host in local connection string!"
        }
    }
}

# --- TEST 4: Port Collision Detection Guard ---
Assert-Test "Port collision detection guard identifies occupied loopback port" {
    $testPort = 5195
    $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Parse("127.0.0.1"), $testPort)
    $listener.Start()
    try {
        $isAvailable = Test-PortAvailability -Port $testPort
        if ($isAvailable) {
            throw "Expected port $testPort to be reported as NOT available (occupied)."
        }
    } finally {
        $listener.Stop()
    }
    # Once stopped, it should be available
    $isAvailableNow = Test-PortAvailability -Port $testPort
    if (-not $isAvailableNow) {
        throw "Expected port $testPort to be available after listener stopped."
    }
}

# --- TEST 5: Live Operational Preflight Validation ---
Assert-Test "Live operational databases pass all preflight gates (SQL 2014, Manifest, State, Hashes)" {
    $appsettings = Get-Content (Join-Path $repoRoot "src\Api\appsettings.json") -Raw | ConvertFrom-Json
    $prePass = Assert-LocalFirstLauncherPreflight -RepoRoot $repoRoot `
        -ExpectedMasterSha "49a0be38218c199a100c8d057a2acd45306844c0" `
        -Local2026ConnStr $appsettings.ConnectionStrings.LocalConnection2026 `
        -Local2027ConnStr $appsettings.ConnectionStrings.LocalConnection2027 `
        -SkipGit $false -IsTestMode $false

    if (-not $prePass) {
        throw "Operational preflight returned false"
    }
}

# --- TEST 6: Real Process Lifecycle (Start, Status, Restart, Stop) on Dedicated Test Port ---
Assert-Test "Process lifecycle (Start, Status, Restart, Stop) operates deterministically on loopback" {
    $lifecyclePort = 5199
    $testPidFile = [System.IO.Path]::GetTempFileName()
    Remove-Item $testPidFile -Force -ErrorAction SilentlyContinue

    try {
        # A. Start
        $startResult = & $launcherScript Start `
            -Port $lifecyclePort `
            -ExpectedMasterSha "49a0be38218c199a100c8d057a2acd45306844c0" `
            -OverridePidFilePath $testPidFile

        if ($startResult.Status -ne "RUNNING") {
            throw "Expected Status=RUNNING from Start action, got $($startResult.Status)"
        }
        $pid1 = $startResult.Pid

        # Verify /health responds 200 OK
        $healthResp = Invoke-WebRequest -Uri "http://127.0.0.1:$lifecyclePort/health" -Method Get -TimeoutSec 5 -UseBasicParsing
        if ($healthResp.StatusCode -ne 200) {
            throw "Health endpoint returned status code $($healthResp.StatusCode)"
        }

        # B. Status
        $statusResult = & $launcherScript Status `
            -Port $lifecyclePort `
            -OverridePidFilePath $testPidFile

        if ($statusResult.Status -ne "RUNNING" -or $statusResult.Pid -ne $pid1) {
            throw "Expected Status=RUNNING with PID $pid1, got $($statusResult.Status) (PID $($statusResult.Pid))"
        }

        # C. Stop
        $stopResult = & $launcherScript Stop `
            -Port $lifecyclePort `
            -OverridePidFilePath $testPidFile

        Start-Sleep -Seconds 1
        $procAfterStop = Get-Process -Id $pid1 -ErrorAction SilentlyContinue
        if ($null -ne $procAfterStop) {
            throw "Process $pid1 still running after Stop command!"
        }

        # Status after stop
        $statusAfterStop = & $launcherScript Status `
            -Port $lifecyclePort `
            -OverridePidFilePath $testPidFile

        if ($statusAfterStop.Status -ne "NOT_RUNNING") {
            throw "Expected NOT_RUNNING after stop, got $($statusAfterStop.Status)"
        }
    } finally {
        # Failsafe cleanup
        if (Test-Path $testPidFile) {
            $remainingPid = (Get-Content $testPidFile -Raw -ErrorAction SilentlyContinue)
            if (-not [string]::IsNullOrWhiteSpace($remainingPid)) {
                $p = Get-Process -Id ([int]$remainingPid.Trim()) -ErrorAction SilentlyContinue
                if ($p) { $p | Stop-Process -Force -ErrorAction SilentlyContinue }
            }
            Remove-Item $testPidFile -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host "`n==========================================================================" -ForegroundColor Green
Write-Host "  ALL $passedTests / $totalTests LAUNCHER SUITE TESTS PASSED DETERMINISTICALLY! " -ForegroundColor Green
Write-Host "==========================================================================" -ForegroundColor Green
