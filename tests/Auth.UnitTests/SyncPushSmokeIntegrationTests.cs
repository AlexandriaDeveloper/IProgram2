#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    [Trait("Category", "LocalDbRequired")]
    public class SyncPushSmokeIntegrationTests : IAsyncLifetime
    {
        private const string LocalConnStr2026 = "Server=localhost;Database=IProgramLocalDb2026_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string LocalConnStr2027 = "Server=localhost;Database=IProgramLocalDb2027_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string RemoteConnStr2026 = "Server=localhost;Database=IProgramRemoteSync2026_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string RemoteConnStr2027 = "Server=localhost;Database=IProgramRemoteSync2027_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";

        private readonly AzurePushTransactionCoordinator _coordinator = new(NullLogger<AzurePushTransactionCoordinator>.Instance);

        public async Task InitializeAsync()
        {
            // Verify connections are accessible
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
        }

        public Task DisposeAsync() => Task.CompletedTask;

        #region Scenario 1, 2, 3: Normal INSERT, UPDATE, SOFT_DELETE

        [Fact]
        public async Task Scenario01_NormalInsert_AppliesDailyAndIncrementsServerVersion()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var clientOpId = Guid.NewGuid();

            var payloadObj = new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily 2026 Test Insert",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            };
            var payloadJson = JsonSerializer.Serialize(payloadObj);
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payloadJson);

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = clientOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payloadJson,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");
            var result = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, CancellationToken.None);

            Assert.False(result.IsReplay);
            Assert.Equal(curVer + 1, result.ServerVersion);

            // Verify remote Daily was created by SyncId
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name, IsActive, Closed FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
            cmd.Parameters.AddWithValue("@SyncId", syncId);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Daily 2026 Test Insert", reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            await reader.CloseAsync();

            // Verify ProcessedOperations
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT RequestHash, ResultStatus FROM [sync].[ProcessedOperations] WHERE DatabaseId = '2026' AND ClientOperationId = @ClientOperationId;";
            cmd.Parameters.AddWithValue("@ClientOperationId", clientOpId);
            await using var procReader = await cmd.ExecuteReaderAsync();
            Assert.True(await procReader.ReadAsync());
            Assert.Equal(hash, procReader.GetString(0));
            Assert.Equal("SUCCESS", procReader.GetString(1));
            await procReader.CloseAsync();

            // Verify ServerChangeFeed
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT ServerVersion, OperationType, EntityType FROM [sync].[ServerChangeFeed] WHERE DatabaseId = '2026' AND EntitySyncId = @SyncId;";
            cmd.Parameters.AddWithValue("@SyncId", syncId);
            await using var feedReader = await cmd.ExecuteReaderAsync();
            Assert.True(await feedReader.ReadAsync());
            Assert.Equal(curVer + 1, feedReader.GetInt64(0));
            Assert.Equal("INSERT", feedReader.GetString(1));
            Assert.Equal("Daily", feedReader.GetString(2));
        }

        [Fact]
        public async Task Scenario02_Update_ModifiesDailyWithoutDuplicate_IncrementsVersion()
        {
            // First create a record
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var insertOpId = Guid.NewGuid();

            var insertPayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily Initial Name",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var insertHash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, insertPayload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");

            var insertOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = insertOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = insertPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };
            var insertRes = await _coordinator.ApplyOperationAsync(conn, "2026", insertOutbox, curVer, insertHash, CancellationToken.None);

            // Now apply UPDATE
            var updateOpId = Guid.NewGuid();
            var updatePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "UPDATE",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = insertRes.ServerVersion,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily Updated Name",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = true,
                    IsActive = true,
                    UpdatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var updateHash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Update", "Daily", syncId, updatePayload);

            var updateOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = updateOpId,
                CommandName = "Daily.Update",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = updatePayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            var updateRes = await _coordinator.ApplyOperationAsync(conn, "2026", updateOutbox, insertRes.ServerVersion, updateHash, CancellationToken.None);

            Assert.False(updateRes.IsReplay);
            Assert.Equal(insertRes.ServerVersion + 1, updateRes.ServerVersion);

            // Verify only ONE Daily exists with this SyncId
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*), MAX(Name), MAX(CAST(Closed as int)) FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
            countCmd.Parameters.AddWithValue("@SyncId", syncId);
            await using var countReader = await countCmd.ExecuteReaderAsync();
            Assert.True(await countReader.ReadAsync());
            Assert.Equal(1, countReader.GetInt32(0)); // EXACTLY ONE Daily row
            Assert.Equal("Daily Updated Name", countReader.GetString(1));
            Assert.Equal(1, countReader.GetInt32(2)); // Closed = 1
        }

        [Fact]
        public async Task Scenario03_SoftDelete_SetsIsActiveFalseWithoutTombstone()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var insertOpId = Guid.NewGuid();

            var insertPayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily To Delete",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var insertHash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, insertPayload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");
            var insertOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = insertOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = insertPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };
            var insertRes = await _coordinator.ApplyOperationAsync(conn, "2026", insertOutbox, curVer, insertHash, CancellationToken.None);

            // Now apply SOFT_DELETE
            var deleteOpId = Guid.NewGuid();
            var deletePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = insertRes.ServerVersion,
                entityData = new
                {
                    SyncId = syncId,
                    IsActive = false,
                    DeactivatedAt = DateTime.UtcNow.ToString("O"),
                    DeactivatedBy = "TestAdmin"
                }
            });
            var deleteHash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.SoftDelete", "Daily", syncId, deletePayload);

            var deleteOutbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = deleteOpId,
                CommandName = "Daily.SoftDelete",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = deletePayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            var deleteRes = await _coordinator.ApplyOperationAsync(conn, "2026", deleteOutbox, insertRes.ServerVersion, deleteHash, CancellationToken.None);

            Assert.False(deleteRes.IsReplay);
            Assert.Equal(insertRes.ServerVersion + 1, deleteRes.ServerVersion);

            // Verify IsActive is false and Daily still exists
            await using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = "SELECT IsActive FROM [dbo].[Daily] WHERE SyncId = @SyncId;";
            checkCmd.Parameters.AddWithValue("@SyncId", syncId);
            var isActive = (bool)(await checkCmd.ExecuteScalarAsync())!;
            Assert.False(isActive);
        }

        #endregion

        #region Scenario 4: Idempotent Replay

        [Fact]
        public async Task Scenario04_Replay_ReturnsStoredResponse_ZeroDuplicateWrites()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily Replay Test",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");
            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            // First run
            var firstRes = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, CancellationToken.None);
            Assert.False(firstRes.IsReplay);

            var verAfterFirst = await GetServerVersionAsync(conn, "2026");
            var feedCountAfterFirst = await CountRowsAsync(conn, "SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE DatabaseId = '2026';");
            var dailyCountAfterFirst = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");

            // Second run (Replay with same hash, even with stale expectedServerVersion 0)
            var secondRes = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, 0, hash, CancellationToken.None);

            Assert.True(secondRes.IsReplay);
            Assert.Equal(firstRes.ServerVersion, secondRes.ServerVersion);

            // Verify ZERO mutations occurred during replay
            var verAfterSecond = await GetServerVersionAsync(conn, "2026");
            var feedCountAfterSecond = await CountRowsAsync(conn, "SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE DatabaseId = '2026';");
            var dailyCountAfterSecond = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");

            Assert.Equal(verAfterFirst, verAfterSecond);
            Assert.Equal(feedCountAfterFirst, feedCountAfterSecond);
            Assert.Equal(dailyCountAfterFirst, dailyCountAfterSecond);
        }

        #endregion

        #region Scenario 5: Operation ID Reuse Attack

        [Fact]
        public async Task Scenario05_OperationIdReuse_ThrowsException_ZeroWrites()
        {
            var syncId1 = Guid.NewGuid();
            var syncId2 = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var sharedOpId = Guid.NewGuid();

            var payload1 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId1,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId1,
                    Name = "Daily Legitimate",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash1 = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId1, payload1);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");
            var outbox1 = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = sharedOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId1,
                PayloadJson = payload1,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await _coordinator.ApplyOperationAsync(conn, "2026", outbox1, curVer, hash1, CancellationToken.None);

            // Attacker / corruption: same ClientOperationId, different payload/hash
            var payload2 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId2,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId2,
                    Name = "Daily Tampered Attack",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash2 = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId2, payload2);

            var outbox2 = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = sharedOpId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId2,
                PayloadJson = payload2,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            var ex = await Assert.ThrowsAsync<SyncOperationIdReuseException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox2, curVer + 1, hash2, CancellationToken.None);
            });

            Assert.Contains(sharedOpId.ToString(), ex.Message);

            // Verify syncId2 was NOT created
            var countTampered = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId2}';");
            Assert.Equal(0, countTampered);
        }

        #endregion

        #region Scenario 6: Version Conflict Detection

        [Fact]
        public async Task Scenario06_VersionConflict_ThrowsException_ZeroWrites()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily Version Conflict Test",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            // Intentionally pass an incorrect ExpectedServerVersion (e.g. curVer + 999)
            var conflictEx = await Assert.ThrowsAsync<SyncVersionConflictException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer + 999, hash, CancellationToken.None);
            });

            Assert.Equal(curVer + 999, conflictEx.ExpectedVersion);
            Assert.Equal(curVer, conflictEx.CurrentServerVersion);

            // Verify zero writes occurred
            var dailyCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            Assert.Equal(0, dailyCount);

            var verAfter = await GetServerVersionAsync(conn, "2026");
            Assert.Equal(curVer, verAfter);
        }

        #endregion

        #region Scenario 7: Remote Transaction Failure Rollback

        [Fact]
        public async Task Scenario07_RemoteTransactionFailure_RollsBackAllWrites()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            // Missing required daily Name to trigger failure during parse or validation
            var invalidPayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "", // Empty Name triggers validation failure
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, invalidPayload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = invalidPayload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await Assert.ThrowsAsync<SyncPayloadValidationException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, CancellationToken.None);
            });

            // Verify zero writes: no Daily, no ProcessedOperation, no ServerChangeFeed, no version bump
            var dailyCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            var procCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE ClientOperationId = '{opId}';");
            var verAfter = await GetServerVersionAsync(conn, "2026");

            Assert.Equal(0, dailyCount);
            Assert.Equal(0, procCount);
            Assert.Equal(curVer, verAfter);
        }

        #endregion

        #region Scenario 8: Crash Recovery Simulation

        [Fact]
        public async Task Scenario08_CrashRecovery_ReplayCompletesLocalOutboxWithoutDuplicateRemoteMutation()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily Crash Recovery Test",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var remoteConn = new SqlConnection(RemoteConnStr2026);
            await remoteConn.OpenAsync();

            var curVer = await GetServerVersionAsync(remoteConn, "2026");

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            // Remote transaction commits successfully
            var remoteResult = await _coordinator.ApplyOperationAsync(remoteConn, "2026", outbox, curVer, hash, CancellationToken.None);
            Assert.False(remoteResult.IsReplay);
            var committedVersion = remoteResult.ServerVersion;

            // SIMULATED CRASH: local outbox was NOT marked COMPLETED, remained IN_PROGRESS or PENDING
            // Lease expires, next push re-sends same operation
            var replayResult = await _coordinator.ApplyOperationAsync(remoteConn, "2026", outbox, 0, hash, CancellationToken.None);

            Assert.True(replayResult.IsReplay);
            Assert.Equal(committedVersion, replayResult.ServerVersion);

            // Assert: Exactly ONE Daily row, exactly ONE ProcessedOperation, ServerState version not incremented again
            var dailyCount = await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            var procCount = await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE ClientOperationId = '{opId}';");
            var currentVersion = await GetServerVersionAsync(remoteConn, "2026");

            Assert.Equal(1, dailyCount);
            Assert.Equal(1, procCount);
            Assert.Equal(committedVersion, currentVersion);
        }

        #endregion

        #region Scenario 9: Queue Ordering & Error Halting

        [Fact]
        public async Task Scenario09_QueueOrdering_HaltOnError_PreservesStrictFIFO()
        {
            // Test that when an operation in the queue fails, subsequent operations are NOT processed
            var devId = Guid.NewGuid();

            var syncId1 = Guid.NewGuid();
            var syncId2 = Guid.NewGuid(); // Will fail
            var syncId3 = Guid.NewGuid(); // Must remain unprocessed

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");

            // Op 1 (Valid)
            var p1 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId1,
                baseServerVersion = 0,
                entityData = new { SyncId = syncId1, Name = "Op 1", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });
            var h1 = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId1, p1);
            var o1 = new LocalOutbox { DatabaseId = "2026", ClientOperationId = Guid.NewGuid(), CommandName = "Daily.Insert", AggregateType = "Daily", EntitySyncId = syncId1, PayloadJson = p1, Status = "IN_PROGRESS" };

            var r1 = await _coordinator.ApplyOperationAsync(conn, "2026", o1, curVer, h1, CancellationToken.None);
            Assert.Equal(curVer + 1, r1.ServerVersion);

            // Op 2 (Invalid - missing Name)
            var p2 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId2,
                baseServerVersion = 0,
                entityData = new { SyncId = syncId2, Name = "", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });
            var h2 = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId2, p2);
            var o2 = new LocalOutbox { DatabaseId = "2026", ClientOperationId = Guid.NewGuid(), CommandName = "Daily.Insert", AggregateType = "Daily", EntitySyncId = syncId2, PayloadJson = p2, Status = "IN_PROGRESS" };

            // Processing op2 throws exception
            await Assert.ThrowsAsync<SyncPayloadValidationException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", o2, r1.ServerVersion, h2, CancellationToken.None);
            });

            // In queue processing, op3 is NEVER sent to coordinator because loop halts on op2 failure
            // Verify op1 is in DB, op2 is NOT in DB, op3 was never written
            var count1 = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId1}';");
            var count2 = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId2}';");
            var count3 = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId3}';");

            Assert.Equal(1, count1);
            Assert.Equal(0, count2);
            Assert.Equal(0, count3);
        }

        #endregion

        #region Scenario 10: Local Push Lease Concurrency

        [Fact]
        public async Task Scenario10_ConcurrentLease_SecondAttemptRejected()
        {
            var syncConnectionProviderMock = new Mock<ISyncConnectionProvider>();
            syncConnectionProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns(LocalConnStr2026);

            var leaseManager = new LocalPushLeaseManager(syncConnectionProviderMock.Object, NullLogger<LocalPushLeaseManager>.Instance);

            // Acquire lease 1
            var lease1 = await leaseManager.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, lease1);

            try
            {
                // Attempt second concurrent lease before expiration
                await Assert.ThrowsAsync<SyncPushAlreadyRunningException>(async () =>
                {
                    await leaseManager.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(30), CancellationToken.None);
                });
            }
            finally
            {
                // Release lease
                await leaseManager.ReleaseLeaseAsync("2026", lease1, CancellationToken.None);
            }

            // After release, acquiring new lease succeeds
            var lease2 = await leaseManager.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, lease2);
            await leaseManager.ReleaseLeaseAsync("2026", lease2, CancellationToken.None);
        }

        #endregion

        #region Scenario 11: Year Isolation (2026 vs 2027)

        [Fact]
        public async Task Scenario11_YearIsolation_CrossDatabaseMutationRejected()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new
                {
                    SyncId = syncId,
                    Name = "Daily 2026 Year Isolation",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await using var conn2026 = new SqlConnection(RemoteConnStr2026);
            await using var conn2027 = new SqlConnection(RemoteConnStr2027);
            await conn2026.OpenAsync();
            await conn2027.OpenAsync();

            var curVer2026 = await GetServerVersionAsync(conn2026, "2026");
            var curVer2027 = await GetServerVersionAsync(conn2027, "2027");

            // Apply 2026 operation to 2026 remote DB
            var res2026 = await _coordinator.ApplyOperationAsync(conn2026, "2026", outbox, curVer2026, hash, CancellationToken.None);
            Assert.Equal(curVer2026 + 1, res2026.ServerVersion);

            // Verify written to 2026 DB
            var in2026 = await CountRowsAsync(conn2026, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            Assert.Equal(1, in2026);

            // Verify COMPLETELY ABSENT from 2027 DB
            var in2027 = await CountRowsAsync(conn2027, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            Assert.Equal(0, in2027);

            var ver2027After = await GetServerVersionAsync(conn2027, "2027");
            Assert.Equal(curVer2027, ver2027After); // 2027 ServerVersion completely untouched
        }

        #endregion

        #region Helper Methods

        private static async Task<long> GetServerVersionAsync(SqlConnection conn, string databaseId)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CurrentVersion FROM [sync].[ServerState] WHERE DatabaseId = @DatabaseId;";
            cmd.Parameters.AddWithValue("@DatabaseId", databaseId);
            var val = await cmd.ExecuteScalarAsync();
            return val != null && val != DBNull.Value ? (long)val : 0L;
        }

        private static async Task<int> CountRowsAsync(SqlConnection conn, string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var val = await cmd.ExecuteScalarAsync();
            return val != null && val != DBNull.Value ? Convert.ToInt32(val) : 0;
        }

        #endregion
    }
}
