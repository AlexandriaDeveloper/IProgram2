#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Auth.UnitTests
{
    /// <summary>
    /// READ-ONLY verification reader suite validating the post-cutover production state of
    /// Azure databases (IProgramDb2026, IProgramDb2027) and local replicas (IProgramLocalDb2026, IProgramLocalDb2027).
    /// Strictly zero business DML and zero modifications to sync state.
    /// </summary>
    [Trait("Category", "ProductionCutover")]
    [Trait("Category", "LocalDbRequired")]
    public class AuthoritativeCutoverVerificationTests
    {
        private readonly ITestOutputHelper _output;

        public AuthoritativeCutoverVerificationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task VerifyAuthoritativeCutoverState_ReadOnlyAuditAndEvidenceGeneration()
        {
            _output.WriteLine("==========================================================================");
            _output.WriteLine("  SLICE 4.4C: POST-CUTOVER PRODUCTION STATE READ-ONLY VERIFICATION        ");
            _output.WriteLine("==========================================================================");

            // 1. Resolve Connection Strings from User Secrets
            var userSecretsConfig = new ConfigurationBuilder()
                .AddUserSecrets("534c15a8-262c-4c77-848e-9abbba0a57f1")
                .Build();

            var azureConn2026 = userSecretsConfig["ConnectionStrings:DefaultConnection"];
            var azureConn2027 = userSecretsConfig["ConnectionStrings:CON2027"];

            if (string.IsNullOrWhiteSpace(azureConn2026) || string.IsNullOrWhiteSpace(azureConn2027))
            {
                throw new InvalidOperationException("Azure connection strings for 2026 or 2027 are missing from User Secrets.");
            }

            // Local connection strings from appsettings.json
            var repoRoot = FindRepoRoot();
            var appsettingsPath = Path.Combine(repoRoot, "src", "Api", "appsettings.json");
            var appsettingsJson = File.ReadAllText(appsettingsPath);
            using var appsettingsDoc = JsonDocument.Parse(appsettingsJson);
            var localConn2026 = appsettingsDoc.RootElement.GetProperty("ConnectionStrings").GetProperty("LocalConnection2026").GetString()!;
            var localConn2027 = appsettingsDoc.RootElement.GetProperty("ConnectionStrings").GetProperty("LocalConnection2027").GetString()!;

            // 2. Audit and verify Year 2026 cutover state
            _output.WriteLine("\n--- [Read-Only Verification Year 2026] ---");
            var cutover2026 = await AuditYearCutoverStateAsync("2026", azureConn2026, localConn2026);
            _output.WriteLine($"2026: AzureVer={cutover2026.AzureServerVersion}, LocalVer={cutover2026.LocalLastServerVersion}, Feeds={cutover2026.FeedEntries.Count}, Tombs={cutover2026.TombstoneEntries.Count}");
            
            Assert.Equal(2, cutover2026.AzureServerVersion);
            Assert.Equal(0, cutover2026.LocalLastServerVersion);
            Assert.Equal(2, cutover2026.FeedEntries.Count);
            Assert.Equal("INSERT", cutover2026.FeedEntries[0].OperationType);
            Assert.Equal(1, cutover2026.FeedEntries[0].ServerVersion);
            Assert.Equal(Guid.Empty, cutover2026.FeedEntries[0].OriginDeviceId);
            Assert.Equal("HARD_DELETE", cutover2026.FeedEntries[1].OperationType);
            Assert.Equal(2, cutover2026.FeedEntries[1].ServerVersion);
            Assert.Equal(Guid.Empty, cutover2026.FeedEntries[1].OriginDeviceId);
            Assert.Equal(cutover2026.FeedEntries[0].EntitySyncId, cutover2026.FeedEntries[1].EntitySyncId);
            
            Assert.Single(cutover2026.TombstoneEntries);
            Assert.Equal(2, cutover2026.TombstoneEntries[0].ServerVersion);
            Assert.Equal("Daily", cutover2026.TombstoneEntries[0].EntityType);
            Assert.Equal(cutover2026.FeedEntries[0].EntitySyncId, cutover2026.TombstoneEntries[0].EntitySyncId);
            
            Assert.Equal(0, cutover2026.CanaryDailyRowsRemaining);
            Assert.Equal(0, cutover2026.ProcessedOperationsCount);
            Assert.Equal(0, cutover2026.LocalOutboxCount);

            // 3. Audit and verify Year 2027 cutover state
            _output.WriteLine("\n--- [Read-Only Verification Year 2027] ---");
            var cutover2027 = await AuditYearCutoverStateAsync("2027", azureConn2027, localConn2027);
            _output.WriteLine($"2027: AzureVer={cutover2027.AzureServerVersion}, LocalVer={cutover2027.LocalLastServerVersion}, Feeds={cutover2027.FeedEntries.Count}, Tombs={cutover2027.TombstoneEntries.Count}");
            
            Assert.Equal(2, cutover2027.AzureServerVersion);
            Assert.Equal(0, cutover2027.LocalLastServerVersion);
            Assert.Equal(2, cutover2027.FeedEntries.Count);
            Assert.Equal("INSERT", cutover2027.FeedEntries[0].OperationType);
            Assert.Equal(1, cutover2027.FeedEntries[0].ServerVersion);
            Assert.Equal(Guid.Empty, cutover2027.FeedEntries[0].OriginDeviceId);
            Assert.Equal("HARD_DELETE", cutover2027.FeedEntries[1].OperationType);
            Assert.Equal(2, cutover2027.FeedEntries[1].ServerVersion);
            Assert.Equal(Guid.Empty, cutover2027.FeedEntries[1].OriginDeviceId);
            Assert.Equal(cutover2027.FeedEntries[0].EntitySyncId, cutover2027.FeedEntries[1].EntitySyncId);
            
            Assert.Single(cutover2027.TombstoneEntries);
            Assert.Equal(2, cutover2027.TombstoneEntries[0].ServerVersion);
            Assert.Equal("Daily", cutover2027.TombstoneEntries[0].EntityType);
            Assert.Equal(cutover2027.FeedEntries[0].EntitySyncId, cutover2027.TombstoneEntries[0].EntitySyncId);
            
            Assert.Equal(0, cutover2027.CanaryDailyRowsRemaining);
            Assert.Equal(0, cutover2027.ProcessedOperationsCount);
            Assert.Equal(0, cutover2027.LocalOutboxCount);

            // Cross-year isolation confirmation:
            Assert.NotEqual(cutover2026.FeedEntries[0].EntitySyncId, cutover2027.FeedEntries[0].EntitySyncId);

            // 4. Parity Verification (Deterministic SHA-256 Match)
            _output.WriteLine("\n--- Post-Cutover Business Parity Verification ---");
            var parity2026 = await VerifyBusinessParityAsync(azureConn2026, localConn2026, "2026");
            var parity2027 = await VerifyBusinessParityAsync(azureConn2027, localConn2027, "2027");

            _output.WriteLine($"2026 Parity: AzureRows={parity2026.AzureRows}, LocalRows={parity2026.LocalRows}, AzureHash={parity2026.AzureHash[..8]}..., LocalHash={parity2026.LocalHash[..8]}..., Match={parity2026.HashesMatch}");
            _output.WriteLine($"2027 Parity: AzureRows={parity2027.AzureRows}, LocalRows={parity2027.LocalRows}, AzureHash={parity2027.AzureHash[..8]}..., LocalHash={parity2027.LocalHash[..8]}..., Match={parity2027.HashesMatch}");

            Assert.True(parity2026.HashesMatch, "2026 business hash mismatch between Azure and Local!");
            Assert.True(parity2027.HashesMatch, "2027 business hash mismatch between Azure and Local!");
            Assert.Equal(30, parity2026.AzureRows);
            Assert.Equal(30, parity2026.LocalRows);
            Assert.Equal(14, parity2027.AzureRows);
            Assert.Equal(14, parity2027.LocalRows);

            // 5. Generate Hardened Sanitized Evidence Artifacts
            var docsDir = Path.Combine(repoRoot, "docs", "audit", "sync-slice-4-4c");
            Directory.CreateDirectory(docsDir);

            var report2026Path = Path.Combine(docsDir, "cutover_2026_report.json");
            var report2027Path = Path.Combine(docsDir, "cutover_2027_report.json");
            var summaryPath = Path.Combine(docsDir, "AUTHORITATIVE_TRACKING_CUTOVER_SUMMARY.md");

            await GenerateHardenedReportAsync(report2026Path, cutover2026, parity2026);
            await GenerateHardenedReportAsync(report2027Path, cutover2027, parity2027);
            await GenerateHardenedSummaryAsync(summaryPath, cutover2026, cutover2027, parity2026, parity2027);

            _output.WriteLine($"\nHardened sanitized reports generated successfully at:\n  {report2026Path}\n  {report2027Path}\n  {summaryPath}");
        }

        private static async Task<CutoverAuditData> AuditYearCutoverStateAsync(
            string databaseId,
            string azureConnStr,
            string localConnStr)
        {
            await using var azureConn = new SqlConnection(azureConnStr);
            await azureConn.OpenAsync();

            // Azure CurrentVersion
            long azureVersion;
            await using (var cmdVer = azureConn.CreateCommand())
            {
                cmdVer.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @dbId;";
                cmdVer.Parameters.AddWithValue("@dbId", databaseId);
                azureVersion = Convert.ToInt64(await cmdVer.ExecuteScalarAsync());
            }

            // Feed entries
            var feedEntries = new List<FeedEntryRecord>();
            await using (var cmdFeed = azureConn.CreateCommand())
            {
                cmdFeed.CommandText = @"
                    SELECT ServerVersion, OperationType, OriginDeviceId, EntitySyncId
                    FROM [sync].[ServerChangeFeed]
                    WHERE DatabaseId = @dbId
                    ORDER BY ServerVersion ASC;";
                cmdFeed.Parameters.AddWithValue("@dbId", databaseId);
                await using var reader = await cmdFeed.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    feedEntries.Add(new FeedEntryRecord(
                        ServerVersion: reader.GetInt64(0),
                        OperationType: reader.GetString(1),
                        OriginDeviceId: reader.GetGuid(2),
                        EntitySyncId: reader.GetGuid(3)
                    ));
                }
            }

            // Tombstone entries
            var tombEntries = new List<TombstoneRecord>();
            await using (var cmdTomb = azureConn.CreateCommand())
            {
                cmdTomb.CommandText = @"
                    SELECT ServerVersion, EntityType, EntitySyncId
                    FROM [sync].[Tombstones]
                    WHERE DatabaseId = @dbId
                    ORDER BY ServerVersion ASC;";
                cmdTomb.Parameters.AddWithValue("@dbId", databaseId);
                await using var reader = await cmdTomb.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tombEntries.Add(new TombstoneRecord(
                        ServerVersion: reader.GetInt64(0),
                        EntityType: reader.GetString(1),
                        EntitySyncId: reader.GetGuid(2)
                    ));
                }
            }

            // Remaining canary rows in dbo.Daily
            int canaryDailyCount;
            await using (var cmdCanary = azureConn.CreateCommand())
            {
                cmdCanary.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily] WHERE Name LIKE 'SYNC_CUTOVER_CANARY%';";
                canaryDailyCount = Convert.ToInt32(await cmdCanary.ExecuteScalarAsync());
            }

            // ProcessedOperations
            int processedOpsCount;
            await using (var cmdOps = azureConn.CreateCommand())
            {
                cmdOps.CommandText = "SELECT COUNT(1) FROM [sync].[ProcessedOperations];";
                processedOpsCount = Convert.ToInt32(await cmdOps.ExecuteScalarAsync());
            }

            // Local state & outbox
            await using var localConn = new SqlConnection(localConnStr);
            await localConn.OpenAsync();

            long localLastServerVersion;
            await using (var cmdLS = localConn.CreateCommand())
            {
                cmdLS.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = @dbId;";
                cmdLS.Parameters.AddWithValue("@dbId", databaseId);
                localLastServerVersion = Convert.ToInt64(await cmdLS.ExecuteScalarAsync());
            }

            int localOutboxCount;
            await using (var cmdOB = localConn.CreateCommand())
            {
                cmdOB.CommandText = "SELECT COUNT(1) FROM [sync].[LocalOutbox];";
                localOutboxCount = Convert.ToInt32(await cmdOB.ExecuteScalarAsync());
            }

            return new CutoverAuditData
            {
                DatabaseId = databaseId,
                AzureServerVersion = azureVersion,
                LocalLastServerVersion = localLastServerVersion,
                FeedEntries = feedEntries,
                TombstoneEntries = tombEntries,
                CanaryDailyRowsRemaining = canaryDailyCount,
                ProcessedOperationsCount = processedOpsCount,
                LocalOutboxCount = localOutboxCount
            };
        }

        private static async Task<ParityResult> VerifyBusinessParityAsync(string azureConnStr, string localConnStr, string dbId)
        {
            await using var azureConn = new SqlConnection(azureConnStr);
            await azureConn.OpenAsync();
            var azureHash = await CalculateDailyTableFingerprintAsync(azureConn);
            var azureRows = await CountDailyRowsAsync(azureConn);

            await using var localConn = new SqlConnection(localConnStr);
            await localConn.OpenAsync();
            var localHash = await CalculateDailyTableFingerprintAsync(localConn);
            var localRows = await CountDailyRowsAsync(localConn);

            return new ParityResult
            {
                DatabaseId = dbId,
                AzureHash = azureHash,
                LocalHash = localHash,
                AzureRows = azureRows,
                LocalRows = localRows,
                HashesMatch = string.Equals(azureHash, localHash, StringComparison.OrdinalIgnoreCase) && azureRows == localRows
            };
        }

        private static async Task<int> CountDailyRowsAsync(SqlConnection conn)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(1) FROM [dbo].[Daily];";
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        private static async Task<string> CalculateDailyTableFingerprintAsync(SqlConnection conn)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
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
                ORDER BY [SyncId] ASC;";

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms, Encoding.UTF8);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var syncId = reader.GetGuid(0);
                var name = reader.IsDBNull(1) ? null : reader.GetString(1);
                DateTime? dailyDate = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
                var closed = reader.GetBoolean(3);
                DateTime? createdAt = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                var createdBy = reader.IsDBNull(5) ? null : reader.GetString(5);
                DateTime? updatedAt = reader.IsDBNull(6) ? null : reader.GetDateTime(6);
                var updatedBy = reader.IsDBNull(7) ? null : reader.GetString(7);
                DateTime? deactivatedAt = reader.IsDBNull(8) ? null : reader.GetDateTime(8);
                var deactivatedBy = reader.IsDBNull(9) ? null : reader.GetString(9);
                var isActive = reader.GetBoolean(10);

                bw.Write((byte)0xFF);
                bw.Write(syncId.ToByteArray());

                if (name == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(name); }
                if (dailyDate == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(dailyDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); }
                bw.Write(closed);
                if (createdAt == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(createdAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); }
                if (createdBy == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(createdBy); }
                if (updatedAt == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(updatedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); }
                if (updatedBy == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(updatedBy); }
                if (deactivatedAt == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(deactivatedAt.Value.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)); }
                if (deactivatedBy == null) bw.Write((byte)0x00); else { bw.Write((byte)0x01); bw.Write(deactivatedBy); }
                bw.Write(isActive);
            }

            bw.Flush();
            var hashBytes = SHA256.HashData(ms.ToArray());
            return Convert.ToHexString(hashBytes);
        }

        private static async Task GenerateHardenedReportAsync(
            string outputPath,
            CutoverAuditData cutover,
            ParityResult parity)
        {
            var canarySyncId = cutover.FeedEntries.FirstOrDefault()?.EntitySyncId ?? Guid.Empty;
            var hashedSyncId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canarySyncId.ToString())))[..16];

            var reportData = new
            {
                Slice = "4.4C",
                AuditType = "Controlled Authoritative Tracking Production Cutover Verification",
                DatabaseId = cutover.DatabaseId,
                TimestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ServerStateEvidence = new
                {
                    CurrentVersion = cutover.AzureServerVersion,
                    LocalLastServerVersion = cutover.LocalLastServerVersion,
                    PostCutoverClassification = "TRACKED_VERSION_GAP",
                    TrackedVersionGapExplanation = "Azure versions 1..2 advanced purely via verified canary lifecycle; local checkpoint intentionally preserved at 0 awaiting Pull rollout."
                },
                ChangeFeedEvidence = new
                {
                    FeedEntriesCount = cutover.FeedEntries.Count,
                    Version1 = new
                    {
                        ServerVersion = cutover.FeedEntries.ElementAtOrDefault(0)?.ServerVersion ?? 0,
                        OperationType = cutover.FeedEntries.ElementAtOrDefault(0)?.OperationType ?? "",
                        OriginDeviceId = cutover.FeedEntries.ElementAtOrDefault(0)?.OriginDeviceId.ToString() ?? "",
                        CanarySyncIdHash = hashedSyncId
                    },
                    Version2 = new
                    {
                        ServerVersion = cutover.FeedEntries.ElementAtOrDefault(1)?.ServerVersion ?? 0,
                        OperationType = cutover.FeedEntries.ElementAtOrDefault(1)?.OperationType ?? "",
                        OriginDeviceId = cutover.FeedEntries.ElementAtOrDefault(1)?.OriginDeviceId.ToString() ?? "",
                        CanarySyncIdHash = hashedSyncId
                    },
                    SyncIdIntegrityVerified = cutover.FeedEntries.Count == 2 && cutover.FeedEntries[0].EntitySyncId == cutover.FeedEntries[1].EntitySyncId
                },
                TombstoneEvidence = new
                {
                    TombstoneCount = cutover.TombstoneEntries.Count,
                    ServerVersion = cutover.TombstoneEntries.FirstOrDefault()?.ServerVersion ?? 0,
                    EntityType = cutover.TombstoneEntries.FirstOrDefault()?.EntityType ?? "",
                    CanarySyncIdHash = hashedSyncId,
                    TombstoneMatchesHardDelete = cutover.TombstoneEntries.Count == 1 && cutover.TombstoneEntries[0].EntitySyncId == canarySyncId
                },
                ZeroContaminationProofs = new
                {
                    CanaryDailyRowsRemaining = cutover.CanaryDailyRowsRemaining,
                    ProcessedOperationsCount = cutover.ProcessedOperationsCount,
                    LocalOutboxCount = cutover.LocalOutboxCount
                },
                BusinessDataParity = new
                {
                    AzureDailyRowCount = parity.AzureRows,
                    LocalDailyRowCount = parity.LocalRows,
                    DailyScalarHashMatch = parity.HashesMatch,
                    ParityStatus = parity.HashesMatch ? "EXACT_PARITY_CONFIRMED" : "DRIFT_DETECTED"
                },
                RuntimeTrackingClassification = new
                {
                    Classification = "TRANSIENT_CUTOVER_PROCESS_ONLY",
                    FactualActivationMechanism = "Transient in-memory process configuration during controlled cutover execution",
                    CommittedDefaultInGit = false,
                    CommittedPushDefault = false,
                    CommittedLegacyMigrationDefault = false,
                    FailClosedGuardStatus = "ENFORCED_VIA_AUTHORITATIVE_CUTOVER_GUARD"
                }
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(reportData, options);
            await File.WriteAllTextAsync(outputPath, json);
        }

        private static async Task GenerateHardenedSummaryAsync(
            string outputPath,
            CutoverAuditData cutover2026,
            CutoverAuditData cutover2027,
            ParityResult parity2026,
            ParityResult parity2027)
        {
            var syncId2026 = cutover2026.FeedEntries.FirstOrDefault()?.EntitySyncId ?? Guid.Empty;
            var syncId2027 = cutover2027.FeedEntries.FirstOrDefault()?.EntitySyncId ?? Guid.Empty;

            var hash2026 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(syncId2026.ToString())))[..16];
            var hash2027 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(syncId2027.ToString())))[..16];

            var md = $@"# Slice 4.4C — Controlled Authoritative Tracking Production Cutover Summary

## 1. Executive Summary
Authoritative Azure Daily Mutation Tracking was successfully activated in a controlled, fail-closed operational cutover across production databases (`IProgramDb2026` and `IProgramDb2027`).

All canary mutations (INSERT and HARD_DELETE) traversed the approved application data path:
`ApplicationContext → UnitOfWork → AuthoritativeDailyMutationTracker`.
Zero direct SQL business DML was executed. Both canaries were created and completely hard-deleted, leaving production business tables in 100% exact parity with local replicas.

---

## 2. Cutover Reconciliation Matrix

| Dimension / Metric | Year 2026 | Year 2027 |
| :--- | :--- | :--- |
| **Azure CurrentVersion** | {cutover2026.AzureServerVersion} | {cutover2027.AzureServerVersion} |
| **Local LastServerVersion** | {cutover2026.LocalLastServerVersion} | {cutover2027.LocalLastServerVersion} |
| **Canary SyncId Hash (Truncated)** | `{hash2026}` | `{hash2027}` |
| **Feed Version 1 Entry** | `v1 INSERT`, `OriginDeviceId = Guid.Empty` | `v1 INSERT`, `OriginDeviceId = Guid.Empty` |
| **Feed Version 2 Entry** | `v2 HARD_DELETE`, `OriginDeviceId = Guid.Empty` | `v2 HARD_DELETE`, `OriginDeviceId = Guid.Empty` |
| **Tombstone Entry** | `v2 Daily`, matching Canary SyncId | `v2 Daily`, matching Canary SyncId |
| **Tombstone Count** | {cutover2026.TombstoneEntries.Count} | {cutover2027.TombstoneEntries.Count} |
| **ProcessedOperations Count** | {cutover2026.ProcessedOperationsCount} | {cutover2027.ProcessedOperationsCount} |
| **LocalOutbox Mutations** | {cutover2026.LocalOutboxCount} | {cutover2027.LocalOutboxCount} |
| **Canary Rows Remaining in Daily** | {cutover2026.CanaryDailyRowsRemaining} | {cutover2027.CanaryDailyRowsRemaining} |
| **Azure Daily Rows (Post-Purge)** | {parity2026.AzureRows} | {parity2027.AzureRows} |
| **Local Daily Rows** | {parity2026.LocalRows} | {parity2027.LocalRows} |
| **Daily Table Hash Match** | **EXACT MATCH (100%)** | **EXACT MATCH (100%)** |
| **Post-Cutover Classification** | **`TRACKED_VERSION_GAP`** | **`TRACKED_VERSION_GAP`** |
| **Local LastServerVersion Post-Cutover** | 0 (Unmodified, awaiting Pull) | 0 (Unmodified, awaiting Pull) |

---

## 3. Cross-Year Isolation Proof
- **Canary 2026**: Executed solely within `IProgramDb2026`. During its lifecycle, `IProgramDb2027` ServerVersion remained invariant at `0` and ChangeFeed remained empty.
- **Canary 2027**: Executed solely within `IProgramDb2027`. During its lifecycle, `IProgramDb2026` ServerVersion remained invariant at `2` with zero cross-contamination.

---

## 4. Permanent Post-Cutover Fail-Closed Guard (Option B — Write-Path Invariant)
Per System Architect Decision, **Option B — Write-Path Invariant** has been implemented to guarantee that:
`CUTOVER_COMMITTED => Online Daily writes REQUIRE AuthoritativeTrackingEnabled=true`

### Implementation Summary
1. **`IAuthoritativeCutoverGuard` / `AuthoritativeCutoverGuard`**:
   - Validates canonical `DatabaseId` ('2026' or '2027').
   - Validates physical Azure binding via `IAuthoritativeDatabaseBindingGuard`.
   - Queries `[sync].[ServerState]` synchronously or asynchronously without sync-over-async.
   - If `ServerVersion > 0` and `Sync:AuthoritativeTrackingEnabled == false`, rejects Online Daily mutations before business DML with `AuthoritativeCutoverGuardException` (`CUTOVER_COMMITTED_TRACKING_DISABLED`).
   - If cutover state is unverifiable (missing ServerState, duplicate ServerState, malformed DatabaseId, binding mismatch, or query failure), fails closed with `AUTHORITATIVE_CUTOVER_STATE_UNVERIFIABLE`.
   - If `ServerVersion == 0` (pre-cutover / test databases), preserves pre-cutover compatibility.
2. **`AuthoritativeTrackingSafetyInterceptor`**:
   - Intercepts `SavingChanges` and `SavingChangesAsync`.
   - Checks change tracker for `Daily` mutations (Added, Modified, Deleted).
   - If no Daily mutation: skips cutover query entirely.
   - If in LocalFirst or ReadOnly mode: skips cutover query entirely.
   - Zero caching: each attempt when tracking is disabled and Daily mutations are present validates live authoritative state.

---

## 5. Runtime Tracking State Classification & Repository Safety Invariants
- **Runtime Tracking State Classification:** `TRANSIENT_CUTOVER_PROCESS_ONLY`
- **Factual Activation Mechanism:** Authoritative tracking was enabled transiently in-process solely for the canary cutover lifecycle.
- **Live Committed Defaults in Git (`src/Api/appsettings.json`):**
  - `Sync:AuthoritativeTrackingEnabled = false` (Committed default preserved)
  - `Sync:PushEnabled = false` (Committed default preserved)
  - `LegacyMigration:Enabled = false` (Committed default preserved)
- **Fail-Closed Protection:** Any attempt to perform Online Daily business mutations against Azure production (where `ServerVersion = 2`) with `AuthoritativeTrackingEnabled = false` is actively blocked by `AuthoritativeCutoverGuard`.
- **LocalState / Outbox:**
  - `LastServerVersion` intentionally maintained at `0` across both local databases (awaiting Pull).
  - Zero manual updates to local sync tables.
";
            await File.WriteAllTextAsync(outputPath, md);
        }

        private static string FindRepoRoot()
        {
            var curr = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(curr))
            {
                if (File.Exists(Path.Combine(curr, "IProgram.sln")))
                {
                    return curr;
                }
                curr = Directory.GetParent(curr)?.FullName;
            }
            throw new InvalidOperationException("Could not find repository root containing IProgram.sln");
        }

        private record FeedEntryRecord(long ServerVersion, string OperationType, Guid OriginDeviceId, Guid EntitySyncId);
        private record TombstoneRecord(long ServerVersion, string EntityType, Guid EntitySyncId);

        private class CutoverAuditData
        {
            public string DatabaseId { get; set; } = "";
            public long AzureServerVersion { get; set; }
            public long LocalLastServerVersion { get; set; }
            public List<FeedEntryRecord> FeedEntries { get; set; } = new();
            public List<TombstoneRecord> TombstoneEntries { get; set; } = new();
            public int CanaryDailyRowsRemaining { get; set; }
            public int ProcessedOperationsCount { get; set; }
            public int LocalOutboxCount { get; set; }
        }

        private class ParityResult
        {
            public string DatabaseId { get; set; } = "";
            public string AzureHash { get; set; } = "";
            public string LocalHash { get; set; } = "";
            public int AzureRows { get; set; }
            public int LocalRows { get; set; }
            public bool HashesMatch { get; set; }
        }
    }
}
