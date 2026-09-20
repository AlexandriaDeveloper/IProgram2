$ErrorActionPreference = "Stop"

$localConnStr = "Server=localhost;Database=IProgramLocalDb2027;Integrated Security=True;TrustServerCertificate=True;"
$conn = New-Object System.Data.SqlClient.SqlConnection($localConnStr)

try {
    $conn.Open()
    Write-Host "[ADOPTION 2027] Connected to localhost IProgramLocalDb2027." -ForegroundColor Green

    # 1. Verify LocalOutbox is 0
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = "SELECT COUNT(*) FROM sync.LocalOutbox WHERE DatabaseId = '2027';"
    $outboxCount = [int64]$cmd.ExecuteScalar()
    if ($outboxCount -ne 0) {
        throw "ABORT: LocalOutbox is not 0 (count: $outboxCount)!"
    }
    Write-Host "  -> LocalOutbox is 0 (verified)." -ForegroundColor Green

    # 2. Verify LocalState checkpoint equals Azure ServerState version (0 == 0)
    $cmd.CommandText = "SELECT LastServerVersion FROM sync.LocalState WHERE DatabaseId = '2027';"
    $localVersion = [int64]$cmd.ExecuteScalar()
    if ($localVersion -ne 0) {
        throw "ABORT: LocalState LastServerVersion is $localVersion (expected 0)!"
    }
    Write-Host "  -> LocalState LastServerVersion is $localVersion (matches Azure ServerState version 0)." -ForegroundColor Green

    # 3. Update sync.BootstrapManifest to VERIFIED_READY and IsWriteAllowed = true
    $revalInfo = [ordered]@{
        RevalidatedUtc = (Get-Date).ToUniversalTime().ToString("o")
        RevalidationScope = "Slice 4.2C - Year 2027 Local Bootstrap & Adoption"
        Authorization = "Business Owner and ChatGPT Architect Confirmed"
        ComparisonResult = "PASS - Exact match across 24 tables (25,473 rows)"
        AuditArtifact = "docs/audit/sync-slice-4-2c/2027/revalidation_comparison.json"
    } | ConvertTo-Json -Compress

    $cmd.CommandText = @"
        UPDATE sync.BootstrapManifest
        SET Status = 'VERIFIED_READY',
            IsWriteAllowed = 1,
            TableCheckJson = @revalJson
        WHERE DatabaseId = '2027';
"@
    $cmd.Parameters.AddWithValue("@revalJson", $revalInfo) | Out-Null
    $rowsAffected = $cmd.ExecuteNonQuery()
    Write-Host "  -> sync.BootstrapManifest updated: Status = VERIFIED_READY, IsWriteAllowed = 1 (Rows affected: $rowsAffected)." -ForegroundColor Green

    # 4. Verify updated manifest row
    $cmd.Parameters.Clear()
    $cmd.CommandText = "SELECT Id, DatabaseId, Status, IsWriteAllowed, TableCheckJson FROM sync.BootstrapManifest WHERE DatabaseId = '2027';"
    $reader = $cmd.ExecuteReader()
    while ($reader.Read()) {
        Write-Host ("  -> Verification: Status = {0}, IsWriteAllowed = {1}" -f $reader["Status"], $reader["IsWriteAllowed"])
    }
    $reader.Close()
}
finally {
    $conn.Close()
}
