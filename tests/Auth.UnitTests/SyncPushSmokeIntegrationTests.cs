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
    [Collection("SyncPushSmokeIntegration")]
    public class SyncPushSmokeIntegrationTests : IAsyncLifetime
    {
        private const string LocalConnStr2026 = "Server=localhost;Database=IProgramLocalDb2026_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string LocalConnStr2027 = "Server=localhost;Database=IProgramLocalDb2027_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string RemoteConnStr2026 = "Server=localhost;Database=IProgramRemoteSync2026_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";
        private const string RemoteConnStr2027 = "Server=localhost;Database=IProgramRemoteSync2027_SmokeTest;Integrated Security=True;TrustServerCertificate=True;";

        private readonly AzurePushTransactionCoordinator _coordinator = new(NullLogger<AzurePushTransactionCoordinator>.Instance);

        public async Task InitializeAsync()
        {
            // Verify connections are accessible and clear active lease
            await using var conn = new SqlConnection(RemoteConnStr2026);
            await conn.OpenAsync();

            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();
            await using var cmd = localConn.CreateCommand();
            cmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task DisposeAsync()
        {
            try
            {
                await using var localConn = new SqlConnection(LocalConnStr2026);
                await localConn.OpenAsync();
                await using var cmd = localConn.CreateCommand();
                cmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await cmd.ExecuteNonQueryAsync();
            }
            catch { }
        }

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
                databaseId = "2026",
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
            var result = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);

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
                databaseId = "2026",
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
            var insertRes = await _coordinator.ApplyOperationAsync(conn, "2026", insertOutbox, curVer, insertHash, devId, CancellationToken.None);

            // Now apply UPDATE
            var updateOpId = Guid.NewGuid();
            var updatePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "UPDATE",
                databaseId = "2026",
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

            var updateRes = await _coordinator.ApplyOperationAsync(conn, "2026", updateOutbox, insertRes.ServerVersion, updateHash, devId, CancellationToken.None);

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
                databaseId = "2026",
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
            var insertRes = await _coordinator.ApplyOperationAsync(conn, "2026", insertOutbox, curVer, insertHash, devId, CancellationToken.None);

            // Now apply SOFT_DELETE
            var deleteOpId = Guid.NewGuid();
            var deletePayload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "SOFT_DELETE",
                databaseId = "2026",
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

            var deleteRes = await _coordinator.ApplyOperationAsync(conn, "2026", deleteOutbox, insertRes.ServerVersion, deleteHash, devId, CancellationToken.None);

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
                databaseId = "2026",
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
            var firstRes = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            Assert.False(firstRes.IsReplay);

            var verAfterFirst = await GetServerVersionAsync(conn, "2026");
            var feedCountAfterFirst = await CountRowsAsync(conn, "SELECT COUNT(*) FROM [sync].[ServerChangeFeed] WHERE DatabaseId = '2026';");
            var dailyCountAfterFirst = await CountRowsAsync(conn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId = '{syncId}';");

            // Second run (Replay with same hash, even with stale expectedServerVersion 0)
            var secondRes = await _coordinator.ApplyOperationAsync(conn, "2026", outbox, 0, hash, devId, CancellationToken.None);

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
                databaseId = "2026",
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

            await _coordinator.ApplyOperationAsync(conn, "2026", outbox1, curVer, hash1, devId, CancellationToken.None);

            // Attacker / corruption: same ClientOperationId, different payload/hash
            var payload2 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
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
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox2, curVer + 1, hash2, devId, CancellationToken.None);
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
                databaseId = "2026",
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
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer + 999, hash, devId, CancellationToken.None);
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
                databaseId = "2026",
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
                    await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
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
                databaseId = "2026",
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
            var remoteResult = await _coordinator.ApplyOperationAsync(remoteConn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
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
                databaseId = "2026",
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
                databaseId = "2026",
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
                databaseId = "2026",
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
            await using (var localConn = new SqlConnection(LocalConnStr2026))
            {
                await localConn.OpenAsync();
                await using var resetCmd = localConn.CreateCommand();
                resetCmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await resetCmd.ExecuteNonQueryAsync();
            }

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
                databaseId = "2026",
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
            var res2026 = await _coordinator.ApplyOperationAsync(conn2026, "2026", outbox, curVer2026, hash, devId, CancellationToken.None);
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
                databaseId = "2026",
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
            var task1 = Task.Run(() => _coordinator.ApplyOperationAsync(conn1, "2026", outbox, curVer, hash, devId, CancellationToken.None));
            var task2 = Task.Run(() => _coordinator.ApplyOperationAsync(conn2, "2026", outbox, curVer, hash, devId, CancellationToken.None));

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
                databaseId = "2026",
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
                await _coordinator.ApplyOperationAsync(conn, "2027", outboxMismatchDb, curVer, hash, devId, CancellationToken.None);
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
                await _coordinator.ApplyOperationAsync(conn, "2026", outboxMismatchCmd, curVer, hash, devId, CancellationToken.None);
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
                databaseId = "2026",
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
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            });
        }

        #endregion

        #region Scenario 15: Lease Lost After Remote Commit (P0 Proof)

        [Fact]
        public async Task Scenario15_LeaseLostAfterRemoteCommit_OldSessionFailsAck_NoMutationOrOutboxReset()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            var oldLeaseToken = Guid.NewGuid();
            var newLeaseToken = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new { SyncId = syncId, Name = "Lease Lost Test", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });
            var hash = LocalOutboxPushService.ComputeRequestHash("2026", devId, "Daily.Insert", "Daily", syncId, payload);

            await using var remoteConn = new SqlConnection(RemoteConnStr2026);
            await remoteConn.OpenAsync();
            var curVer = await GetServerVersionAsync(remoteConn, "2026");

            // 1. Remote commit succeeds
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
            var remoteResult = await _coordinator.ApplyOperationAsync(remoteConn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            Assert.Equal(curVer + 1, remoteResult.ServerVersion);

            // 2. Local DB setup: row is IN_PROGRESS with oldLeaseToken
            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();
            await using (var initCmd = localConn.CreateCommand())
            {
                initCmd.CommandText = @"
                    DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                    UPDATE [sync].[LocalState]
                    SET LastServerVersion = @CurVer,
                        DeviceId = @DevId,
                        ActiveLeaseToken = @NewLeaseToken,
                        LeaseExpiresAtUtc = DATEADD(MINUTE, 5, SYSUTCDATETIME())
                    WHERE DatabaseId = '2026';

                    INSERT INTO [sync].[LocalOutbox]
                    (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount, LockToken, LockedUntilUtc)
                    VALUES
                    (@OpId, '2026', 'Daily', 'Daily.Insert', @SyncId, @Payload, SYSUTCDATETIME(), 'IN_PROGRESS', 0, @OldLeaseToken, DATEADD(MINUTE, 5, SYSUTCDATETIME()));";
                initCmd.Parameters.AddWithValue("@CurVer", curVer);
                initCmd.Parameters.AddWithValue("@DevId", devId);
                initCmd.Parameters.AddWithValue("@NewLeaseToken", newLeaseToken);
                initCmd.Parameters.AddWithValue("@OldLeaseToken", oldLeaseToken);
                initCmd.Parameters.AddWithValue("@OpId", opId);
                initCmd.Parameters.AddWithValue("@SyncId", syncId);
                initCmd.Parameters.AddWithValue("@Payload", payload);
                await initCmd.ExecuteNonQueryAsync();
            }

            try
            {
                // 3. Old session attempts local ack with oldLeaseToken -> MUST FAIL with SyncLeaseExpiredException
                await Assert.ThrowsAsync<SyncLeaseExpiredException>(async () =>
                {
                    await LocalOutboxPushService.AcknowledgeSuccessLocallyAsync(
                        LocalConnStr2026,
                        "2026",
                        opId,
                        remoteResult.ServerVersion,
                        oldLeaseToken,
                        CancellationToken.None);
                });

                // 4. PROOF: Old session MUST NOT reset outbox, clear lock, or advance version
                await using (var verifyCmd = localConn.CreateCommand())
                {
                    verifyCmd.CommandText = "SELECT Status, LockToken FROM [sync].[LocalOutbox] WHERE ClientOperationId = @OpId;";
                    verifyCmd.Parameters.AddWithValue("@OpId", opId);
                    await using var reader = await verifyCmd.ExecuteReaderAsync();
                    Assert.True(await reader.ReadAsync());
                    Assert.Equal("IN_PROGRESS", reader.GetString(0));
                    Assert.Equal(oldLeaseToken, reader.GetGuid(1)); // Lock remains unchanged!
                    await reader.CloseAsync();

                    verifyCmd.Parameters.Clear();
                    verifyCmd.CommandText = "SELECT LastServerVersion, ActiveLeaseToken FROM [sync].[LocalState] WHERE DatabaseId = '2026';";
                    await using var stateReader = await verifyCmd.ExecuteReaderAsync();
                    Assert.True(await stateReader.ReadAsync());
                    Assert.Equal(curVer, stateReader.GetInt64(0)); // Version unchanged!
                    Assert.Equal(newLeaseToken, stateReader.GetGuid(1)); // New owner lease intact!
                }
            }
            finally
            {
                await using var resetCmd = localConn.CreateCommand();
                resetCmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await resetCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 16: Local Ack AffectedRows=0 Rollback (P0 Proof)

        [Fact]
        public async Task Scenario16_LocalAck_AffectedRowsZero_RollsBackLocalState()
        {
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();
            var leaseToken = Guid.NewGuid();
            var differentToken = Guid.NewGuid();

            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();

            try
            {
                // LocalState at version 500, active lease = leaseToken
                await using (var initCmd = localConn.CreateCommand())
                {
                    initCmd.CommandText = @"
                        DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                        UPDATE [sync].[LocalState]
                        SET LastServerVersion = 500,
                            DeviceId = @DevId,
                            ActiveLeaseToken = @LeaseToken,
                            LeaseExpiresAtUtc = DATEADD(MINUTE, 5, SYSUTCDATETIME())
                        WHERE DatabaseId = '2026';

                        -- Outbox row has a DIFFERENT LockToken (or is PENDING)
                        INSERT INTO [sync].[LocalOutbox]
                        (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount, LockToken, LockedUntilUtc)
                        VALUES
                        (@OpId, '2026', 'Daily', 'Daily.Insert', NEWID(), '{}', SYSUTCDATETIME(), 'IN_PROGRESS', 0, @DifferentToken, DATEADD(MINUTE, 5, SYSUTCDATETIME()));";
                    initCmd.Parameters.AddWithValue("@DevId", devId);
                    initCmd.Parameters.AddWithValue("@LeaseToken", leaseToken);
                    initCmd.Parameters.AddWithValue("@DifferentToken", differentToken);
                    initCmd.Parameters.AddWithValue("@OpId", opId);
                    await initCmd.ExecuteNonQueryAsync();
                }

                // Acknowledge attempt with leaseToken must find affectedRows = 0 on outbox and throw
                var ex = await Assert.ThrowsAsync<SyncLeaseExpiredException>(async () =>
                {
                    await LocalOutboxPushService.AcknowledgeSuccessLocallyAsync(
                        LocalConnStr2026,
                        "2026",
                        opId,
                        501,
                        leaseToken,
                        CancellationToken.None);
                });

                Assert.Contains("Affected rows: 0", ex.Message);

                // PROOF OF TRANSACTION ROLLBACK: LocalState.LastServerVersion MUST still be 500, NOT 501
                await using (var checkCmd = localConn.CreateCommand())
                {
                    checkCmd.CommandText = "SELECT LastServerVersion FROM [sync].[LocalState] WHERE DatabaseId = '2026';";
                    var versionAfter = (long)(await checkCmd.ExecuteScalarAsync())!;
                    Assert.Equal(500, versionAfter);
                }
            }
            finally
            {
                await using var resetCmd = localConn.CreateCommand();
                resetCmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await resetCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 17: Claim With Stale or Expired Lease (P0 Proof)

        [Fact]
        public async Task Scenario17_ClaimWithStaleOrExpiredLease_CannotClaimRow()
        {
            var opId = Guid.NewGuid();
            var activeLease = Guid.NewGuid();
            var staleLease = Guid.NewGuid();

            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();

            try
            {
                await using (var initCmd = localConn.CreateCommand())
                {
                    initCmd.CommandText = @"
                        DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                        UPDATE [sync].[LocalState]
                        SET ActiveLeaseToken = @ActiveLease,
                            LeaseExpiresAtUtc = DATEADD(MINUTE, 5, SYSUTCDATETIME())
                        WHERE DatabaseId = '2026';

                        INSERT INTO [sync].[LocalOutbox]
                        (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount)
                        VALUES
                        (@OpId, '2026', 'Daily', 'Daily.Insert', NEWID(), '{}', SYSUTCDATETIME(), 'PENDING', 0);";
                    initCmd.Parameters.AddWithValue("@ActiveLease", activeLease);
                    initCmd.Parameters.AddWithValue("@OpId", opId);
                    await initCmd.ExecuteNonQueryAsync();
                }

                // 1. Claim with stale lease token MUST return false
                var claimedWithStale = await LocalOutboxPushService.TryClaimOutboxInProgressAsync(
                    LocalConnStr2026,
                    "2026",
                    opId,
                    staleLease,
                    CancellationToken.None);
                Assert.False(claimedWithStale);

                // 2. Set lease to expired in the past
                await using (var expireCmd = localConn.CreateCommand())
                {
                    expireCmd.CommandText = @"
                        UPDATE [sync].[LocalState]
                        SET LeaseExpiresAtUtc = DATEADD(MINUTE, -1, SYSUTCDATETIME())
                        WHERE DatabaseId = '2026';";
                    await expireCmd.ExecuteNonQueryAsync();
                }

                // 3. Claim with expired active lease token MUST return false
                var claimedWithExpired = await LocalOutboxPushService.TryClaimOutboxInProgressAsync(
                    LocalConnStr2026,
                    "2026",
                    opId,
                    activeLease,
                    CancellationToken.None);
                Assert.False(claimedWithExpired);

                // Status must remain PENDING
                var status = await GetOutboxStatusAsync(localConn, opId);
                Assert.Equal("PENDING", status.status);
            }
            finally
            {
                await using var resetCmd = localConn.CreateCommand();
                resetCmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await resetCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 18: Queue Head Claim Failure Halts Processing (P0 Proof)

        [Fact]
        public async Task Scenario18_ClaimFailureOnQueueHead_HaltsQueueProcessing_PreservesFIFO()
        {
            var devId = Guid.NewGuid();
            var op1Id = Guid.NewGuid();
            var op2Id = Guid.NewGuid();
            var syncId1 = Guid.NewGuid();
            var syncId2 = Guid.NewGuid();

            await using var remoteConn = new SqlConnection(RemoteConnStr2026);
            await remoteConn.OpenAsync();
            var curVer = await GetServerVersionAsync(remoteConn, "2026");

            var p1 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = devId,
                entitySyncId = syncId1,
                baseServerVersion = curVer,
                entityData = new { SyncId = syncId1, Name = "Op 1", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });

            var p2 = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = devId,
                entitySyncId = syncId2,
                baseServerVersion = curVer,
                entityData = new { SyncId = syncId2, Name = "Op 2", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
            });

            await using var localConn = new SqlConnection(LocalConnStr2026);
            await localConn.OpenAsync();

            try
            {
                // Initialize LocalState with matching DevId and curVer, but with a different lease token so TryClaim fails
                var actualDbLease = Guid.NewGuid();
                await using (var initCmd = localConn.CreateCommand())
                {
                    initCmd.CommandText = @"
                        DELETE FROM [sync].[LocalOutbox] WHERE DatabaseId = '2026';
                        UPDATE [sync].[LocalState]
                        SET LastServerVersion = @CurVer,
                            DeviceId = @DevId,
                            ActiveLeaseToken = @DbLease,
                            LeaseExpiresAtUtc = DATEADD(MINUTE, 5, SYSUTCDATETIME())
                        WHERE DatabaseId = '2026';

                        INSERT INTO [sync].[LocalOutbox]
                        (ClientOperationId, DatabaseId, AggregateType, CommandName, EntitySyncId, PayloadJson, CreatedAtUtc, Status, RetryCount)
                        VALUES
                        (@Op1, '2026', 'Daily', 'Daily.Insert', @S1, @P1, DATEADD(MINUTE, -2, SYSUTCDATETIME()), 'PENDING', 0),
                        (@Op2, '2026', 'Daily', 'Daily.Insert', @S2, @P2, DATEADD(MINUTE, -1, SYSUTCDATETIME()), 'PENDING', 0);";
                    initCmd.Parameters.AddWithValue("@CurVer", curVer);
                    initCmd.Parameters.AddWithValue("@DevId", devId);
                    initCmd.Parameters.AddWithValue("@DbLease", actualDbLease);
                    initCmd.Parameters.AddWithValue("@Op1", op1Id);
                    initCmd.Parameters.AddWithValue("@S1", syncId1);
                    initCmd.Parameters.AddWithValue("@P1", p1);
                    initCmd.Parameters.AddWithValue("@Op2", op2Id);
                    initCmd.Parameters.AddWithValue("@S2", syncId2);
                    initCmd.Parameters.AddWithValue("@P2", p2);
                    await initCmd.ExecuteNonQueryAsync();
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

                // Mock lease manager returns an unowned lease token, but claims to renew/validate successfully
                var unownedLease = Guid.NewGuid();
                var leaseManagerMock = new Mock<ILocalPushLeaseManager>();
                leaseManagerMock.Setup(m => m.AcquireLeaseAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(unownedLease);
                leaseManagerMock.Setup(m => m.RenewLeaseAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                leaseManagerMock.Setup(m => m.ValidateLeaseOwnershipAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                leaseManagerMock.Setup(m => m.ReleaseLeaseAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

                var pushService = new LocalOutboxPushService(
                    syncProviderMock.Object,
                    remoteFactoryMock.Object,
                    _coordinator,
                    leaseManagerMock.Object,
                    inMemoryConfig,
                    NullLogger<LocalOutboxPushService>.Instance);

                // Queue processing MUST HALT on Op1 claim failure and throw SyncLeaseExpiredException
                var ex = await Assert.ThrowsAsync<SyncLeaseExpiredException>(async () =>
                {
                    await pushService.PushPendingOutboxAsync(CancellationToken.None);
                });

                Assert.Contains("Queue processing halted", ex.Message);

                // STRICT FIFO ASSERTION: Op1 and Op2 were NEVER claimed, modified, or processed!
                var op1Status = await GetOutboxStatusAsync(localConn, op1Id);
                Assert.Equal("PENDING", op1Status.status);
                Assert.Equal(0, op1Status.retryCount);

                var op2Status = await GetOutboxStatusAsync(localConn, op2Id);
                Assert.Equal("PENDING", op2Status.status);
                Assert.Equal(0, op2Status.retryCount);

                // Remote DB has ZERO writes
                Assert.Equal(0, await CountRowsAsync(remoteConn, $"SELECT COUNT(*) FROM [dbo].[Daily] WHERE SyncId IN ('{syncId1}', '{syncId2}');"));
            }
            finally
            {
                await using var resetCmd = localConn.CreateCommand();
                resetCmd.CommandText = "UPDATE [sync].[LocalState] SET ActiveLeaseToken = NULL, LeaseExpiresAtUtc = NULL WHERE DatabaseId = '2026';";
                await resetCmd.ExecuteNonQueryAsync();
            }
        }

        #endregion

        #region Scenario 19: Corrupt Replay ResponseJson Hardened Validation (P1 Proof)

        [Fact]
        public async Task Scenario19_CorruptedReplayResponseJson_Variants_FailClosed()
        {
            var syncId = Guid.NewGuid();
            var devId = Guid.NewGuid();
            var opId = Guid.NewGuid();

            var payload = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                entityType = "Daily",
                operationType = "INSERT",
                databaseId = "2026",
                deviceId = devId,
                entitySyncId = syncId,
                baseServerVersion = 0,
                entityData = new { SyncId = syncId, Name = "Replay Hardening", DailyDate = "2026-06-01T00:00:00Z", Closed = false, IsActive = true, CreatedAt = DateTime.UtcNow.ToString("O") }
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

            // Test Case 1: Wrong ClientOperationId in ResponseJson
            await SetProcessedOperationResponseJsonAsync(conn, opId, devId, hash, syncId,
                $"{{\"clientOperationId\":\"{Guid.NewGuid()}\",\"entitySyncId\":\"{syncId}\",\"result\":\"SUCCESS\",\"serverVersion\":{curVer + 1}}}");

            var ex1 = await Assert.ThrowsAsync<SyncCorruptResponseJsonException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            });
            Assert.Contains("clientOperationId", ex1.Message);

            // Test Case 2: Wrong EntitySyncId in ResponseJson
            await SetProcessedOperationResponseJsonAsync(conn, opId, devId, hash, syncId,
                $"{{\"clientOperationId\":\"{opId}\",\"entitySyncId\":\"{Guid.NewGuid()}\",\"result\":\"SUCCESS\",\"serverVersion\":{curVer + 1}}}");

            var ex2 = await Assert.ThrowsAsync<SyncCorruptResponseJsonException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            });
            Assert.Contains("entitySyncId", ex2.Message);

            // Test Case 3: Result is NOT 'SUCCESS'
            await SetProcessedOperationResponseJsonAsync(conn, opId, devId, hash, syncId,
                $"{{\"clientOperationId\":\"{opId}\",\"entitySyncId\":\"{syncId}\",\"result\":\"FAILED\",\"serverVersion\":{curVer + 1}}}");

            var ex3 = await Assert.ThrowsAsync<SyncCorruptResponseJsonException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            });
            Assert.Contains("result", ex3.Message);

            // Test Case 4: ServerVersion is <= 0
            await SetProcessedOperationResponseJsonAsync(conn, opId, devId, hash, syncId,
                $"{{\"clientOperationId\":\"{opId}\",\"entitySyncId\":\"{syncId}\",\"result\":\"SUCCESS\",\"serverVersion\":0}}");

            var ex4 = await Assert.ThrowsAsync<SyncCorruptResponseJsonException>(async () =>
            {
                await _coordinator.ApplyOperationAsync(conn, "2026", outbox, curVer, hash, devId, CancellationToken.None);
            });
            Assert.Contains("serverVersion", ex4.Message);
        }

        private static async Task SetProcessedOperationResponseJsonAsync(
            SqlConnection conn,
            Guid opId,
            Guid devId,
            string hash,
            Guid syncId,
            string responseJson)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM [sync].[ProcessedOperations] WHERE ClientOperationId = @OpId;
                INSERT INTO [sync].[ProcessedOperations]
                (DatabaseId, ClientOperationId, DeviceId, CommandName, RequestHash, EntityType, EntitySyncId, ProcessedAtUtc, ResultStatus, ResponseJson)
                VALUES
                ('2026', @OpId, @DevId, 'Daily.Insert', @Hash, 'Daily', @SyncId, SYSUTCDATETIME(), 'SUCCESS', @ResponseJson);";
            cmd.Parameters.AddWithValue("@OpId", opId);
            cmd.Parameters.AddWithValue("@DevId", devId);
            cmd.Parameters.AddWithValue("@Hash", hash);
            cmd.Parameters.AddWithValue("@SyncId", syncId);
            cmd.Parameters.AddWithValue("@ResponseJson", responseJson);
            await cmd.ExecuteNonQueryAsync();
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
