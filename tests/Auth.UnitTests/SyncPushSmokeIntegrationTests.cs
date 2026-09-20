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

            var validPayload = JsonSerializer.Serialize(new
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
                    Name = "Daily Rollback Test",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, validPayload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            var curVer = await GetServerVersionAsync(conn, "2026");

            // Install fault injection trigger on sync.ProcessedOperations to fail AFTER Daily mutation has succeeded
            await using (var triggerCmd = conn.CreateCommand())
            {
                triggerCmd.CommandText = @"
                    IF OBJECT_ID('[sync].[trg_FaultInjection_Fail]', 'TR') IS NOT NULL DROP TRIGGER [sync].[trg_FaultInjection_Fail];
                    EXEC('
                    CREATE TRIGGER [sync].[trg_FaultInjection_Fail]
                    ON [sync].[ProcessedOperations]
                    INSTEAD OF INSERT
                    AS
                    BEGIN
                        RAISERROR(''FAULT_INJECTION_TRIGGER_FAIL: Intentional rollback test'', 16, 1);
                        ROLLBACK TRANSACTION;
                    END;');";
                await triggerCmd.ExecuteNonQueryAsync();
            }

            try
            {
                var outbox = new LocalOutbox
                {
                    DatabaseId = "2026",
                    ClientOperationId = opId,
                    CommandName = "Daily.Insert",
                    AggregateType = "Daily",
                    EntitySyncId = syncId,
                    PayloadJson = validPayload,
                    CreatedAtUtc = DateTime.UtcNow,
                    Status = "IN_PROGRESS"
                };

                await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, CancellationToken.None);
                });

                // PROOF: Daily mutation was rolled back, ServerChangeFeed was rolled back, ProcessedOperations was rolled back, ServerState unchanged
                var dailyCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
                var changeFeedCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE EntitySyncId = '{syncId}';");
                var procCount = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE ClientOperationId = '{opId}';");
                var verAfter = await GetServerVersionAsync(conn, "2026");

                Assert.Equal(0, dailyCount);
                Assert.Equal(0, changeFeedCount);
                Assert.Equal(0, procCount);
                Assert.Equal(curVer, verAfter);
            }
            finally
            {
                // Always clean up the test trigger
                await using var dropCmd = conn.CreateCommand();
                dropCmd.CommandText = "IF OBJECT_ID('[sync].[trg_FaultInjection_Fail]', 'TR') IS NOT NULL DROP TRIGGER [sync].[trg_FaultInjection_Fail];";
                await dropCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 8: Crash Recovery Simulation (Real LocalOutboxPushService)

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

            // Setup Local DB: LocalState initialized with devId and curVer
            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();
            await using (var initCmd = localConn.CreateCommand())
            {
                initCmd.CommandText = @"
                    DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                    UPDATE [sync].[LocalState]
                    SET LastServerVersion = @CurVer,
                        DeviceId = @DevId,
                        ActiveLeaseToken = NULL,
                        LeaseExpiresAtUtc = NULL
                    WHERE DatabaseId = '2026';";
                initCmd.Parameters.AddWithValue("@CurVer", curVer);
                initCmd.Parameters.AddWithValue("@DevId", devId);
                await initCmd.ExecuteNonQueryAsync();
            }

            var outbox = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Insert",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                Status = "IN_PROGRESS"
            };

            // PRE-CONDITION: Remote transaction committed successfully (first attempt succeeded on remote)
            var remoteResult = await _coordinator.ApplyOperationAsync(remoteConn, "2026", outbox, curVer, hash, CancellationToken.None);
            Assert.False(remoteResult.IsReplay);
            var committedVer = remoteResult.ServerVersion;

            // SIMULATED CRASH: Local application crashed before local acknowledgement!
            // In local DB, outbox remains IN_PROGRESS with an EXPIRED LockedUntilUtc, and LocalState.LastServerVersion is still at curVer!
            await using (var insertCmd = localConn.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO [sync].[LocalOutbox]
                    (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount, LockToken, LockedUntilUtc)
                    VALUES
                    (@OpId, '2026', 'Daily', 'Daily.Insert', @SyncId, @Payload, DATEADD(MINUTE, -10, SYSUTCDATETIME()), 'IN_PROGRESS', 0, NEWID(), DATEADD(MINUTE, -5, SYSUTCDATETIME()));";
                insertCmd.Parameters.AddWithValue("@OpId", opId);
                insertCmd.Parameters.AddWithValue("@SyncId", syncId);
                insertCmd.Parameters.AddWithValue("@Payload", payload);
                await insertCmd.ExecuteNonQueryAsync();
            }

            // Setup PushService dependencies pointing to our isolated smoke test databases
            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns(LocalConnStr2026);

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var c = new SqlConnection(RemoteConnStr2026);
                    c.Open();
                    return c;
                });

            var inMemoryConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:PushEnabled"] = "true" })
                .Build();

            var leaseManager = new LocalPushLeaseManager(syncProviderMock.Object, NullLogger<LocalPushLeaseManager>.Instance);
            var pushService = new LocalOutboxPushService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                _coordinator,
                leaseManager,
                inMemoryConfig,
                NullLogger<LocalOutboxPushService>.Instance);

            // ACT: Execute PushPendingOutboxAsync through the REAL LocalOutboxPushService
            var batchResult = await pushService.PushPendingOutboxAsync(CancellationToken.None);

            // ASSERT:
            // 1. Service reclaimed the expired row and processed it as a REPLAY
            Assert.Equal(1, batchResult.TotalProcessed);
            Assert.Equal(1, batchResult.Succeeded);
            Assert.Equal("REPLAY", batchResult.Operations[0].Status);
            Assert.Equal(committedVer, batchResult.FinalServerVersion);

            // 2. Local outbox row was atomically marked COMPLETED
            await using (var checkCmd = localConn.CreateCommand())
            {
                checkCmd.CommandText = "SELECT Status, CompletedAtUtc FROM [sync].[LocalOutbox] WHERE ClientOperationId = @OpId;";
                checkCmd.Parameters.AddWithValue("@OpId", opId);
                await using var reader = await checkCmd.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("COMPLETED", reader.GetString(0));
                Assert.False(reader.IsDBNull(1));
            }

            // 3. LocalState.LastServerVersion was atomically advanced to committedVer
            await using (var checkVerCmd = localConn.CreateCommand())
            {
                checkVerCmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';";
                var localVer = Convert.ToInt64(await checkVerCmd.ExecuteScalarAsync());
                Assert.Equal(committedVer, localVer);
            }

            // 4. Remote Daily was NOT duplicated (Count == 1)
            var dailyCount = await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            var procCount = await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE ClientOperationId = '{opId}';");
            var changeFeedCount = await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE EntitySyncId = '{syncId}';");
            var serverVer = await GetServerVersionAsync(remoteConn, "2026");

            Assert.Equal(1, dailyCount);
            Assert.Equal(1, procCount);
            Assert.Equal(1, changeFeedCount);
            Assert.Equal(committedVer, serverVer);
        }

        #endregion

        #region Scenario 9: Queue Ordering & Error Halting (Real LocalOutboxPushService)

        [Fact]
        public async Task Scenario09_QueueOrdering_HaltOnError_PreservesStrictFIFO()
        {
            var devId = Guid.NewGuid();
            var syncId1 = Guid.NewGuid();
            var syncId2 = Guid.NewGuid();
            var syncId3 = Guid.NewGuid();

            await using var remoteConn = new SqlConnection(RemoteConnStr2026);
            await remoteConn.OpenAsync();
            var curVer = await GetServerVersionAsync(remoteConn, "2026");

            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();

            // Initialize LocalState
            await using (var initCmd = localConn.CreateCommand())
            {
                initCmd.CommandText = @"
                    DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                    UPDATE [sync].[LocalState]
                    SET LastServerVersion = @CurVer,
                        DeviceId = @DevId,
                        ActiveLeaseToken = NULL,
                        LeaseExpiresAtUtc = NULL
                    WHERE DatabaseId = '2026';";
                initCmd.Parameters.AddWithValue("@CurVer", curVer);
                initCmd.Parameters.AddWithValue("@DevId", devId);
                await initCmd.ExecuteNonQueryAsync();
            }

            var now = DateTime.UtcNow;

            // Op 1: Valid
            var p1 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId1,
                baseServerVersion = curVer,
                entityData = new { SyncId = syncId1, Name = "Op 1 Valid", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = now.ToString("O") }
            });

            // Op 2: Invalid (empty Name triggers payload validation failure)
            var p2 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId2,
                baseServerVersion = curVer,
                entityData = new { SyncId = syncId2, Name = "", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = now.ToString("O") }
            });

            // Op 3: Valid
            var p3 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                deviceId = devId,
                entitySyncId = syncId3,
                baseServerVersion = curVer,
                entityData = new { SyncId = syncId3, Name = "Op 3 Valid", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = now.ToString("O") }
            });

            var op1Id = Guid.NewGuid();
            var op2Id = Guid.NewGuid();
            var op3Id = Guid.NewGuid();

            // Insert 3 rows strictly ordered in time
            await using (var insertCmd = localConn.CreateCommand())
            {
                insertCmd.CommandText = @"
                    INSERT INTO [sync].[LocalOutbox] (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount)
                    VALUES
                    (@Op1, '2026', 'Daily', 'Daily.Insert', @S1, @P1, DATEADD(MINUTE, -3, SYSUTCDATETIME()), 'PENDING', 0),
                    (@Op2, '2026', 'Daily', 'Daily.Insert', @S2, @P2, DATEADD(MINUTE, -2, SYSUTCDATETIME()), 'PENDING', 0),
                    (@Op3, '2026', 'Daily', 'Daily.Insert', @S3, @P3, DATEADD(MINUTE, -1, SYSUTCDATETIME()), 'PENDING', 0);";
                insertCmd.Parameters.AddWithValue("@Op1", op1Id);
                insertCmd.Parameters.AddWithValue("@S1", syncId1);
                insertCmd.Parameters.AddWithValue("@P1", p1);
                insertCmd.Parameters.AddWithValue("@Op2", op2Id);
                insertCmd.Parameters.AddWithValue("@S2", syncId2);
                insertCmd.Parameters.AddWithValue("@P2", p2);
                insertCmd.Parameters.AddWithValue("@Op3", op3Id);
                insertCmd.Parameters.AddWithValue("@S3", syncId3);
                insertCmd.Parameters.AddWithValue("@P3", p3);
                await insertCmd.ExecuteNonQueryAsync();
            }

            var syncProviderMock = new Mock<ISyncConnectionProvider>();
            syncProviderMock.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            syncProviderMock.Setup(p => p.IsReadOnlyMode).Returns(false);
            syncProviderMock.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            syncProviderMock.Setup(p => p.GetLocalConnectionString("2026")).Returns(LocalConnStr2026);

            var remoteFactoryMock = new Mock<IRemoteDatabaseConnectionFactory>();
            remoteFactoryMock.Setup(f => f.CreateOpenConnectionAsync("2026", It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var c = new SqlConnection(RemoteConnStr2026);
                    c.Open();
                    return c;
                });

            var inMemoryConfig = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Sync:PushEnabled"] = "true" })
                .Build();

            var leaseManager = new LocalPushLeaseManager(syncProviderMock.Object, NullLogger<LocalPushLeaseManager>.Instance);
            var pushService = new LocalOutboxPushService(
                syncProviderMock.Object,
                remoteFactoryMock.Object,
                _coordinator,
                leaseManager,
                inMemoryConfig,
                NullLogger<LocalOutboxPushService>.Instance);

            // ACT 1: Execute Push. Op 1 succeeds, Op 2 fails, queue halts and re-throws exception
            await Assert.ThrowsAsync<SyncPayloadValidationException>(async () =>
            {
                await pushService.PushPendingOutboxAsync(CancellationToken.None);
            });

            // ASSERT 1:
            // Op 1 is COMPLETED
            // Op 2 is PENDING with RetryCount = 1 and LastError != null
            // Op 3 is UNTOUCHED (PENDING, RetryCount = 0, LastError = null)
            var op1Status = await GetOutboxStatusAsync(localConn, op1Id);
            var op2Status = await GetOutboxStatusAsync(localConn, op2Id);
            var op3Status = await GetOutboxStatusAsync(localConn, op3Id);

            Assert.Equal("COMPLETED", op1Status.status);
            Assert.Equal("PENDING", op2Status.status);
            Assert.Equal(1, op2Status.retryCount);
            Assert.NotNull(op2Status.lastError);
            Assert.Equal("PENDING", op3Status.status);
            Assert.Equal(0, op3Status.retryCount);
            Assert.Null(op3Status.lastError);

            // Remote DB has Op 1 only. Op 2 and Op 3 do NOT exist!
            Assert.Equal(1, await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId1}';"));
            Assert.Equal(0, await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId2}';"));
            Assert.Equal(0, await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId3}';"));

            // ACT 2: Subsequent push run MUST NOT jump over Op 2 to process Op 3!
            await Assert.ThrowsAsync<SyncPayloadValidationException>(async () =>
            {
                await pushService.PushPendingOutboxAsync(CancellationToken.None);
            });

            // Op 2 retry count increments to 2
            op2Status = await GetOutboxStatusAsync(localConn, op2Id);
            Assert.Equal(2, op2Status.retryCount);

            // Op 3 STILL untouched and NEVER written to remote
            op3Status = await GetOutboxStatusAsync(localConn, op3Id);
            Assert.Equal(0, op3Status.retryCount);
            Assert.Equal(0, await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId3}';"));
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

        #region Scenario 12: Concurrent Idempotency Race (UPDLOCK, HOLDLOCK)

        [Fact]
        public async Task Scenario12_ConcurrentIdempotency_OneOriginalOneReplay_ZeroDuplicateWrites()
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
                    Name = "Concurrent Idempotency Test",
                    DailyDate = "2026-06-01T00:00:00Z",
                    Closed = false,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow.ToString("O")
                }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var conn1 = new SqlConnection(RemoteConnStr2026);
            await using var conn2 = new SqlConnection(RemoteConnStr2026);
            await conn1.OpenAsync();
            await conn2.OpenAsync();

            var curVer = await GetServerVersionAsync(conn1, "2026");

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

            // Run both operations concurrently with same ClientOperationId and same RequestHash
            var task1 = Task.Run(() => _coordinator.ApplyOperationAsync(conn1, "2026", outbox, curVer, hash, CancellationToken.None));
            var task2 = Task.Run(() => _coordinator.ApplyOperationAsync(conn2, "2026", outbox, curVer, hash, CancellationToken.None));

            var results = await Task.WhenAll(task1, task2);

            // One MUST be original, one MUST be replay
            var originalCount = 0;
            var replayCount = 0;
            foreach (var r in results)
            {
                if (r.IsReplay) replayCount++;
                else originalCount++;
                Assert.Equal(curVer + 1, r.ServerVersion);
            }

            Assert.Equal(1, originalCount);
            Assert.Equal(1, replayCount);

            // Verify remote DB state: exactly 1 Daily, 1 ProcessedOperation, 1 ServerChangeFeed, version incremented once
            var dailyCount = await CountRowsAsync(conn1, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");
            var procCount = await CountRowsAsync(conn1, $"SELECT COUNT(*) FROM [sync].[ProcessedOperations] WHERE ClientOperationId = '{opId}';");
            var changeFeedCount = await CountRowsAsync(conn1, $"SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE EntitySyncId = '{syncId}';");
            var finalVer = await GetServerVersionAsync(conn1, "2026");

            Assert.Equal(1, dailyCount);
            Assert.Equal(1, procCount);
            Assert.Equal(1, changeFeedCount);
            Assert.Equal(curVer + 1, finalVer);
        }

        #endregion

        #region Scenario 13: Metadata Mismatch (Fail-Closed)

        [Fact]
        public async Task Scenario13_MetadataMismatch_ThrowsException_ZeroWrites()
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
                entityData = new { SyncId = syncId, Name = "Metadata Test", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            var curVer = await GetServerVersionAsync(conn, "2026");

            // DatabaseId mismatch: outbox has 2026 but called with target 2027
            var outboxMismatchDb = new LocalOutbox
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

            await Assert.ThrowsAsync<SyncMetadataMismatchException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2027", outboxMismatchDb, curVer, hash, CancellationToken.None);
            });

            // CommandName mismatch: CommandName is Daily.Update but payload is INSERT
            var outboxMismatchCmd = new LocalOutbox
            {
                DatabaseId = "2026",
                ClientOperationId = opId,
                CommandName = "Daily.Update",
                AggregateType = "Daily",
                EntitySyncId = syncId,
                PayloadJson = payload,
                CreatedAtUtc = DateTime.UtcNow,
                Status = "IN_PROGRESS"
            };

            await Assert.ThrowsAsync<SyncMetadataMismatchException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outboxMismatchCmd, curVer, hash, CancellationToken.None);
            });
        }

        #endregion

        #region Scenario 14: Corrupt ProcessedOperation ResponseJson (Fail-Closed)

        [Fact]
        public async Task Scenario14_CorruptResponseJson_FailClosed()
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
                entityData = new { SyncId = syncId, Name = "Corrupt Test", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();
            var curVer = await GetServerVersionAsync(conn, "2026");

            // Pre-seed a corrupted ProcessedOperation with invalid unparseable ResponseJson
            await using (var seedCmd = conn.CreateCommand())
            {
                seedCmd.CommandText = @"
                    INSERT INTO [sync].[ProcessedOperations]
                    (DatabaseId, ClientOperationId, DeviceId, CommandName, RequestHash, EntityType, EntitySyncId, ProcessedAtUtc, ResultStatus, ResponseJson)
                    VALUES
                    ('2026', @OpId, @DevId, 'Daily.Insert', @Hash, 'Daily', @SyncId, SYSUTCDATETIME(), 'SUCCESS', 'NOT_VALID_JSON{:::');";
                seedCmd.Parameters.AddWithValue("@OpId", opId);
                seedCmd.Parameters.AddWithValue("@DevId", devId);
                seedCmd.Parameters.AddWithValue("@Hash", hash);
                seedCmd.Parameters.AddWithValue("@SyncId", syncId);
                await seedCmd.ExecuteNonQueryAsync();
            }

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

            // Attempting to process this replay MUST fail closed with SyncCorruptResponseJsonException
            await Assert.ThrowsAsync<SyncCorruptResponseJsonException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, CancellationToken.None);
            });
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

        private static async Task<(string status, int retryCount, string? lastError)> GetOutboxStatusAsync(SqlConnection conn, Guid opId)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Status, RetryCount, LastError FROM [sync].[LocalOutbox] WHERE ClientOperationId = @OpId;";
            cmd.Parameters.AddWithValue("@OpId", opId);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)
                );
            }
            throw new InvalidOperationException($"Outbox row '{opId}' not found.");
        }

        #endregion
    }
}
