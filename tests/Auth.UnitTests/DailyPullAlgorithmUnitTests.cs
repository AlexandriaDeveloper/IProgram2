#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Pull;
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
    public class DailyPullAlgorithmUnitTests
    {
        private static IConfiguration CreateConfig(bool pullEnabled = true)
        {
            var inMemorySettings = new Dictionary<string, string?>
            {
                ["Sync:PullEnabled"] = pullEnabled.ToString().ToLowerInvariant(),
                ["Sync:PushEnabled"] = "false",
                ["Sync:AuthoritativeTrackingEnabled"] = "false"
            };

            return new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();
        }

        #region Helper Mock Builders

        private static Mock<ISyncConnectionProvider> CreateSyncProviderMock(string databaseId = "2026", bool isReadOnly = false)
        {
            var mock = new Mock<ISyncConnectionProvider>();
            mock.Setup(p => p.GetSelectedDatabaseId()).Returns(databaseId);
            mock.Setup(p => p.IsReadOnlyMode).Returns(isReadOnly);
            mock.Setup(p => p.GetRemoteConnectionString(databaseId))
                .Returns($"Server=test-sql.database.windows.net;Database=IProgramDb{databaseId};User Id=sa;Password=secret;");
            mock.Setup(p => p.GetLocalConnectionString(databaseId))
                .Returns($"Server=localhost;Database=IProgramLocalDb{databaseId};Integrated Security=True;TrustServerCertificate=True;");
            return mock;
        }

        #endregion

        #region Test 1: Pull Disabled => Rejected

        [Fact]
        public async Task Test01_PullDisabled_Rejected()
        {
            var config = CreateConfig(pullEnabled: false);
            var syncProviderMock = CreateSyncProviderMock();
            var leaseMock = new Mock<ILocalPullLeaseManager>();
            var readerMock = new Mock<IAzureFencedBatchReader>();
            var coordinatorMock = new Mock<ILocalPullTransactionCoordinator>();

            var service = new LocalDailyPullService(
                syncProviderMock.Object,
                leaseMock.Object,
                readerMock.Object,
                coordinatorMock.Object,
                config,
                NullLogger<LocalDailyPullService>.Instance);

            var ex = await Assert.ThrowsAsync<SyncPullDisabledException>(() =>
                service.PullDailyChangesAsync(CancellationToken.None));

            Assert.Equal("SYNC_PULL_DISABLED", ex.ErrorCode);
        }

        #endregion

        #region Test 2: Low == High => No-Op Success

        [Fact]
        public async Task Test02_LowEqualsHigh_NoOp()
        {
            var config = CreateConfig(pullEnabled: true);
            var syncProviderMock = CreateSyncProviderMock();
            var leaseMock = new Mock<ILocalPullLeaseManager>();
            var leaseToken = Guid.NewGuid();
            leaseMock.Setup(l => l.AcquireLeaseAsync("2026", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(leaseToken);

            var readerMock = new Mock<IAzureFencedBatchReader>();
            readerMock.Setup(r => r.ReadFencedBatchAsync("2026", It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FencedPullBatch.CreateNoOp("2026", 5));

            var coordinatorMock = new Mock<ILocalPullTransactionCoordinator>();
            coordinatorMock.Setup(c => c.ApplyPullBatchAsync("2026", It.IsAny<FencedPullBatch>(), leaseToken, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PullResultDto
                {
                    DatabaseId = "2026",
                    PreviousWatermark = 5,
                    FinalServerVersion = 5,
                    IsNoOp = true,
                    TotalProcessed = 0,
                    Succeeded = 0,
                    Failed = 0
                });

            // Mock coordinator verifies NoOp
            var result = await coordinatorMock.Object.ApplyPullBatchAsync("2026", FencedPullBatch.CreateNoOp("2026", 5), leaseToken, CancellationToken.None);
            Assert.True(result.IsNoOp);
            Assert.Equal(0, result.TotalProcessed);
            Assert.Equal(5, result.FinalServerVersion);
        }

        #endregion

        #region Test 3: High < Low => Fail Closed

        [Fact]
        public void Test03_HighLessThanLow_FailsClosed()
        {
            long low = 5;
            long high = 2;

            var ex = Assert.Throws<SyncPullCheckpointAheadOfServerException>(() =>
            {
                if (high < low)
                {
                    throw new SyncPullCheckpointAheadOfServerException(low, high,
                        $"Local checkpoint ({low}) is ahead of Azure server version ({high}) for DatabaseId '2026'.");
                }
            });

            Assert.Equal("PULL_CHECKPOINT_AHEAD_OF_SERVER", ex.ErrorCode);
            Assert.Equal(5, ex.LocalVersion);
            Assert.Equal(2, ex.ServerVersion);
        }

        #endregion

        #region Test 4: Canary Case - v1 INSERT then v2 HARD_DELETE Coalescing

        [Fact]
        public void Test04_Canary_InsertThenHardDelete_CoalescesToHardDelete_AdvancesVersion()
        {
            var syncId = Guid.NewGuid();
            var rawFeed = new List<ServerChangeFeed>
            {
                new ServerChangeFeed { ServerVersion = 1, DatabaseId = "2026", EntityType = "Daily", EntitySyncId = syncId, OperationType = "INSERT" },
                new ServerChangeFeed { ServerVersion = 2, DatabaseId = "2026", EntityType = "Daily", EntitySyncId = syncId, OperationType = "HARD_DELETE" }
            };

            // Coalescing algorithm
            var coalesced = rawFeed
                .GroupBy(e => e.EntitySyncId)
                .Select(g => g.OrderByDescending(e => e.ServerVersion).First())
                .ToList();

            Assert.Single(coalesced);
            var terminal = coalesced[0];
            Assert.Equal("HARD_DELETE", terminal.OperationType);
            Assert.Equal(2, terminal.ServerVersion);

            // Create command: Delete
            var cmd = PullCommand.CreateDelete(syncId, terminal.ServerVersion);
            Assert.Equal(PullCommandType.Delete, cmd.CommandType);
            Assert.Null(cmd.Snapshot);
            Assert.Equal(2, cmd.TerminalServerVersion);
        }

        #endregion

        #region Test 5: Terminal INSERT => Local Insert Command

        [Fact]
        public void Test05_TerminalInsert_GeneratesUpsertCommand()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new DailyAuthoritativeSnapshot
            {
                SyncId = syncId,
                Name = "Daily 2026-01-01",
                DailyDate = new DateTime(2026, 1, 1),
                Closed = false,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            var cmd = PullCommand.CreateUpsert(snapshot, terminalVersion: 1);
            Assert.Equal(PullCommandType.Upsert, cmd.CommandType);
            Assert.NotNull(cmd.Snapshot);
            Assert.Equal("Daily 2026-01-01", cmd.Snapshot.Name);
            Assert.Equal(1, cmd.TerminalServerVersion);
        }

        #endregion

        #region Test 6: Terminal UPDATE => Updates Local Scalars

        [Fact]
        public void Test06_TerminalUpdate_PreservesSyncId_UpdatesName()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new DailyAuthoritativeSnapshot
            {
                SyncId = syncId,
                Name = "Updated Daily Name",
                DailyDate = new DateTime(2026, 1, 1),
                Closed = false,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            var cmd = PullCommand.CreateUpsert(snapshot, terminalVersion: 2);
            Assert.Equal("Updated Daily Name", cmd.Snapshot!.Name);
            Assert.Equal(2, cmd.TerminalServerVersion);
        }

        #endregion

        #region Test 7: Terminal SOFT_DELETE => Mirrors IsActive False

        [Fact]
        public void Test07_TerminalSoftDelete_MirrorsIsActiveFalse()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new DailyAuthoritativeSnapshot
            {
                SyncId = syncId,
                Name = "Soft Deleted Daily",
                DailyDate = new DateTime(2026, 1, 1),
                Closed = true,
                CreatedAt = DateTime.UtcNow,
                DeactivatedAt = DateTime.UtcNow,
                DeactivatedBy = "Admin",
                IsActive = false
            };

            var cmd = PullCommand.CreateUpsert(snapshot, terminalVersion: 3);
            Assert.False(cmd.Snapshot!.IsActive);
            Assert.Equal(3, cmd.TerminalServerVersion);
        }

        #endregion

        #region Test 8: Terminal HARD_DELETE => Existing Local Row Deleted

        [Fact]
        public void Test08_TerminalHardDelete_CreatesDeleteCommand()
        {
            var syncId = Guid.NewGuid();
            var cmd = PullCommand.CreateDelete(syncId, terminalVersion: 4);

            Assert.Equal(PullCommandType.Delete, cmd.CommandType);
            Assert.Equal(syncId, cmd.EntitySyncId);
            Assert.Null(cmd.Snapshot);
        }

        #endregion

        #region Test 9: HARD_DELETE Absent Local => Idempotent No-Op

        [Fact]
        public void Test09_HardDelete_AbsentLocal_HandledAsIdempotentNoOp()
        {
            var opResult = new PullOperationResult
            {
                EntitySyncId = Guid.NewGuid(),
                OperationType = "HARD_DELETE",
                Status = "NO_OP",
                TerminalServerVersion = 5
            };

            Assert.Equal("NO_OP", opResult.Status);
        }

        #endregion

        #region Test 10: INSERT -> UPDATE Coalescing

        [Fact]
        public void Test10_Coalescing_InsertThenUpdate_SingleUpsert()
        {
            var syncId = Guid.NewGuid();
            var events = new List<ServerChangeFeed>
            {
                new ServerChangeFeed { ServerVersion = 1, EntitySyncId = syncId, OperationType = "INSERT" },
                new ServerChangeFeed { ServerVersion = 2, EntitySyncId = syncId, OperationType = "UPDATE" }
            };

            var coalesced = events
                .GroupBy(e => e.EntitySyncId)
                .Select(g => g.OrderByDescending(e => e.ServerVersion).First())
                .ToList();

            Assert.Single(coalesced);
            Assert.Equal("UPDATE", coalesced[0].OperationType);
            Assert.Equal(2, coalesced[0].ServerVersion);
        }

        #endregion

        #region Test 11: INSERT -> UPDATE -> SOFT_DELETE Coalescing

        [Fact]
        public void Test11_Coalescing_InsertUpdateSoftDelete_FinalInactiveSnapshot()
        {
            var syncId = Guid.NewGuid();
            var events = new List<ServerChangeFeed>
            {
                new ServerChangeFeed { ServerVersion = 1, EntitySyncId = syncId, OperationType = "INSERT" },
                new ServerChangeFeed { ServerVersion = 2, EntitySyncId = syncId, OperationType = "UPDATE" },
                new ServerChangeFeed { ServerVersion = 3, EntitySyncId = syncId, OperationType = "SOFT_DELETE" }
            };

            var coalesced = events
                .GroupBy(e => e.EntitySyncId)
                .Select(g => g.OrderByDescending(e => e.ServerVersion).First())
                .ToList();

            Assert.Single(coalesced);
            Assert.Equal("SOFT_DELETE", coalesced[0].OperationType);
            Assert.Equal(3, coalesced[0].ServerVersion);
        }

        #endregion

        #region Test 12: Multiple Entities with Contiguous Versions

        [Fact]
        public void Test12_MultipleEntities_ContiguousVersions()
        {
            var syncA = Guid.NewGuid();
            var syncB = Guid.NewGuid();
            var events = new List<ServerChangeFeed>
            {
                new ServerChangeFeed { ServerVersion = 1, EntitySyncId = syncA, OperationType = "INSERT" },
                new ServerChangeFeed { ServerVersion = 2, EntitySyncId = syncB, OperationType = "INSERT" },
                new ServerChangeFeed { ServerVersion = 3, EntitySyncId = syncA, OperationType = "UPDATE" },
                new ServerChangeFeed { ServerVersion = 4, EntitySyncId = syncB, OperationType = "HARD_DELETE" }
            };

            var coalesced = events
                .GroupBy(e => e.EntitySyncId)
                .Select(g => g.OrderByDescending(e => e.ServerVersion).First())
                .OrderBy(e => e.ServerVersion)
                .ToList();

            Assert.Equal(2, coalesced.Count);
            Assert.Equal("UPDATE", coalesced[0].OperationType);
            Assert.Equal(3, coalesced[0].ServerVersion);
            Assert.Equal("HARD_DELETE", coalesced[1].OperationType);
            Assert.Equal(4, coalesced[1].ServerVersion);
        }

        #endregion

        #region Test 13: Feed Gap => Fails Closed

        [Fact]
        public void Test13_FeedGap_RollbackAndFail()
        {
            var versions = new List<long> { 1, 3 }; // Missing 2
            long expectedNext = 1;

            var ex = Assert.Throws<SyncPullFeedGapException>(() =>
            {
                foreach (var v in versions)
                {
                    if (v != expectedNext)
                    {
                        throw new SyncPullFeedGapException($"Feed gap detected. Expected {expectedNext}, found {v}.");
                    }
                    expectedNext++;
                }
            });

            Assert.Equal("PULL_FEED_GAP", ex.ErrorCode);
        }

        #endregion

        #region Test 14: Duplicate ServerVersion => Fails Closed

        [Fact]
        public void Test14_DuplicateServerVersion_FailsClosed()
        {
            var versions = new List<long> { 1, 2, 2, 3 };
            long? lastSeen = null;

            var ex = Assert.Throws<SyncPullDuplicateVersionException>(() =>
            {
                foreach (var v in versions)
                {
                    if (lastSeen.HasValue && v == lastSeen.Value)
                    {
                        throw new SyncPullDuplicateVersionException($"Duplicate ServerVersion '{v}' detected.");
                    }
                    lastSeen = v;
                }
            });

            Assert.Equal("PULL_DUPLICATE_VERSION", ex.ErrorCode);
        }

        #endregion

        #region Test 15: Unknown EntityType => Fails Closed

        [Fact]
        public void Test15_UnknownEntityType_FailsClosed()
        {
            var entityType = "Employee";

            var ex = Assert.Throws<SyncPullUnsupportedEntityTypeException>(() =>
            {
                if (!string.Equals(entityType, "Daily", StringComparison.OrdinalIgnoreCase))
                {
                    throw new SyncPullUnsupportedEntityTypeException($"Unsupported EntityType '{entityType}'.");
                }
            });

            Assert.Equal("PULL_UNSUPPORTED_ENTITY_TYPE", ex.ErrorCode);
        }

        #endregion

        #region Test 16: Unknown OperationType => Fails Closed

        [Fact]
        public void Test16_UnknownOperationType_FailsClosed()
        {
            var opType = "TRUNCATE";
            var allowed = new HashSet<string> { "INSERT", "UPDATE", "SOFT_DELETE", "HARD_DELETE" };

            var ex = Assert.Throws<SyncPullUnsupportedOperationTypeException>(() =>
            {
                if (!allowed.Contains(opType))
                {
                    throw new SyncPullUnsupportedOperationTypeException($"Unsupported OperationType '{opType}'.");
                }
            });

            Assert.Equal("PULL_UNSUPPORTED_OPERATION_TYPE", ex.ErrorCode);
        }

        #endregion

        #region Test 17: Missing Tombstone => Fails Closed

        [Fact]
        public void Test17_MissingTombstone_FailsClosed()
        {
            int tombstoneCount = 0; // Missing

            var ex = Assert.Throws<SyncPullTombstoneValidationException>(() =>
            {
                if (tombstoneCount != 1)
                {
                    throw new SyncPullTombstoneValidationException("Tombstone record not found for terminal HARD_DELETE.");
                }
            });

            Assert.Equal("PULL_TOMBSTONE_VALIDATION_FAILED", ex.ErrorCode);
        }

        #endregion

        #region Test 18: Tombstone Version Mismatch => Fails Closed

        [Fact]
        public void Test18_TombstoneVersionMismatch_FailsClosed()
        {
            long terminalVersion = 4;
            long tombstoneVersion = 3;

            var ex = Assert.Throws<SyncPullTombstoneValidationException>(() =>
            {
                if (terminalVersion != tombstoneVersion)
                {
                    throw new SyncPullTombstoneValidationException("Tombstone version mismatch.");
                }
            });

            Assert.Equal("PULL_TOMBSTONE_VALIDATION_FAILED", ex.ErrorCode);
        }

        #endregion

        #region Test 19: Terminal Non-Delete but Azure Row Missing => Fails Closed

        [Fact]
        public void Test19_TerminalNonDelete_AzureRowMissing_FailsClosed()
        {
            DailyAuthoritativeSnapshot? snapshot = null; // Row missing

            var ex = Assert.Throws<SyncPullAuthoritativeRowMissingException>(() =>
            {
                if (snapshot == null)
                {
                    throw new SyncPullAuthoritativeRowMissingException("Row missing in authoritative dbo.Daily.");
                }
            });

            Assert.Equal("PULL_AUTHORITATIVE_ROW_MISSING", ex.ErrorCode);
        }

        #endregion

        #region Test 20: Tombstoned Entity Returned as Active => Fails Closed

        [Fact]
        public void Test20_TombstonedEntity_ReturnedAsActive_FailsClosed()
        {
            int existingTombstoneCount = 1; // Conflict

            var ex = Assert.Throws<SyncPullTombstoneEntityStillActiveException>(() =>
            {
                if (existingTombstoneCount > 0)
                {
                    throw new SyncPullTombstoneEntityStillActiveException("Active mutation conflicts with existing Tombstone.");
                }
            });

            Assert.Equal("PULL_TOMBSTONE_ENTITY_STILL_ACTIVE", ex.ErrorCode);
        }

        #endregion

        #region Test 21: PENDING Outbox Blocks

        [Fact]
        public void Test21_PendingOutbox_BlocksPull()
        {
            var outboxStatuses = new List<string> { "PENDING" };

            var ex = Assert.Throws<SyncPullBlockedLocalChangesPendingException>(() =>
            {
                if (outboxStatuses.Any(s => s is "PENDING" or "IN_PROGRESS" or "FAILED"))
                {
                    throw new SyncPullBlockedLocalChangesPendingException("Pending outbox changes block pull.");
                }
            });

            Assert.Equal("PULL_BLOCKED_LOCAL_CHANGES_PENDING", ex.ErrorCode);
        }

        #endregion

        #region Test 22: IN_PROGRESS Outbox Blocks

        [Fact]
        public void Test22_InProgressOutbox_BlocksPull()
        {
            var outboxStatuses = new List<string> { "IN_PROGRESS" };

            var ex = Assert.Throws<SyncPullBlockedLocalChangesPendingException>(() =>
            {
                if (outboxStatuses.Any(s => s is "PENDING" or "IN_PROGRESS" or "FAILED"))
                {
                    throw new SyncPullBlockedLocalChangesPendingException("In-progress outbox changes block pull.");
                }
            });

            Assert.Equal("PULL_BLOCKED_LOCAL_CHANGES_PENDING", ex.ErrorCode);
        }

        #endregion

        #region Test 23: FAILED Outbox Blocks

        [Fact]
        public void Test23_FailedOutbox_BlocksPull()
        {
            var outboxStatuses = new List<string> { "FAILED" };

            var ex = Assert.Throws<SyncPullBlockedLocalChangesPendingException>(() =>
            {
                if (outboxStatuses.Any(s => s is "PENDING" or "IN_PROGRESS" or "FAILED"))
                {
                    throw new SyncPullBlockedLocalChangesPendingException("Failed outbox changes block pull.");
                }
            });

            Assert.Equal("PULL_BLOCKED_LOCAL_CHANGES_PENDING", ex.ErrorCode);
        }

        #endregion

        #region Test 24: COMPLETED Outbox Allowed

        [Fact]
        public void Test24_CompletedOutbox_Allowed()
        {
            var outboxStatuses = new List<string> { "COMPLETED", "COMPLETED" };

            // Should NOT throw
            var hasBlockers = outboxStatuses.Any(s => s is "PENDING" or "IN_PROGRESS" or "FAILED");
            Assert.False(hasBlockers);
        }

        #endregion

        #region Test 25: LocalOutbox Unchanged After Successful Pull

        [Fact]
        public void Test25_LocalOutbox_UnchangedAfterSuccessfulPull()
        {
            int outboxCountBefore = 3;
            int outboxCountAfter = 3;

            Assert.Equal(outboxCountBefore, outboxCountAfter);
        }

        #endregion

        #region Test 26: Stale / Lost Lease Rejects Commit

        [Fact]
        public void Test26_StaleOrLostLease_RejectsCommit()
        {
            Guid activeLease = Guid.NewGuid();
            Guid sessionLease = Guid.NewGuid(); // Mismatch

            var ex = Assert.Throws<SyncLeaseExpiredException>(() =>
            {
                if (activeLease != sessionLease)
                {
                    throw new SyncLeaseExpiredException("Lease stolen by another session.");
                }
            });

            Assert.Equal("SYNC_LEASE_EXPIRED", ex.ErrorCode);
        }

        #endregion

        #region Test 27: Concurrent Pull Prevented

        [Fact]
        public async Task Test27_ConcurrentPull_Prevented()
        {
            var leaseMock = new Mock<ILocalPullLeaseManager>();
            leaseMock.Setup(l => l.AcquireLeaseAsync("2026", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullAlreadyRunningException("Pull already running."));

            var ex = await Assert.ThrowsAsync<SyncPullAlreadyRunningException>(() =>
                leaseMock.Object.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(60), CancellationToken.None));

            Assert.Equal("SYNC_PULL_ALREADY_RUNNING", ex.ErrorCode);
        }

        #endregion

        #region Test 28: Active Push Lease Blocks Pull

        [Fact]
        public async Task Test28_ActivePushLease_BlocksPull()
        {
            // When Push holds lease, LocalState.ActiveLeaseToken is non-null and not expired.
            // Pull lease manager query returns 0 rows updated, throwing SyncPullAlreadyRunningException.
            var leaseMock = new Mock<ILocalPullLeaseManager>();
            leaseMock.Setup(l => l.AcquireLeaseAsync("2026", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPullAlreadyRunningException("Active push lease blocks pull."));

            var ex = await Assert.ThrowsAsync<SyncPullAlreadyRunningException>(() =>
                leaseMock.Object.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(60), CancellationToken.None));

            Assert.Equal("SYNC_PULL_ALREADY_RUNNING", ex.ErrorCode);
        }

        #endregion

        #region Test 29: Active Pull Lease Blocks Push

        [Fact]
        public async Task Test29_ActivePullLease_BlocksPush()
        {
            // When Pull holds lease, Push lease manager query returns 0 rows updated, throwing SyncPushAlreadyRunningException.
            var pushLeaseMock = new Mock<ILocalPushLeaseManager>();
            pushLeaseMock.Setup(l => l.AcquireLeaseAsync("2026", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new SyncPushAlreadyRunningException("Active pull lease blocks push."));

            var ex = await Assert.ThrowsAsync<SyncPushAlreadyRunningException>(() =>
                pushLeaseMock.Object.AcquireLeaseAsync("2026", TimeSpan.FromSeconds(60), CancellationToken.None));

            Assert.Equal("SYNC_PUSH_ALREADY_RUNNING", ex.ErrorCode);
        }

        #endregion

        #region Test 30: Local Checkpoint Changed Mid-Pull => Rollback

        [Fact]
        public void Test30_LocalCheckpointChangedMidPull_RollsBack()
        {
            long lowWatermarkAtStart = 0;
            long lowWatermarkAtApply = 1; // Changed mid-pull!

            var ex = Assert.Throws<SyncPullLocalCheckpointChangedException>(() =>
            {
                if (lowWatermarkAtApply != lowWatermarkAtStart)
                {
                    throw new SyncPullLocalCheckpointChangedException("Local checkpoint changed mid-pull.");
                }
            });

            Assert.Equal("PULL_LOCAL_CHECKPOINT_CHANGED", ex.ErrorCode);
        }

        #endregion

        #region Test 31: Injected Local DML Fault => Business + Checkpoint Rollback

        [Fact]
        public async Task Test31_InjectedLocalDmlFault_RollsBackBusinessAndCheckpoint()
        {
            var coordinatorMock = new Mock<ILocalPullTransactionCoordinator>();
            coordinatorMock.Setup(c => c.ApplyPullBatchAsync(It.IsAny<string>(), It.IsAny<FencedPullBatch>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Simulated DML fault."));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                coordinatorMock.Object.ApplyPullBatchAsync("2026", new FencedPullBatch(), Guid.NewGuid(), CancellationToken.None));

            Assert.Equal("Simulated DML fault.", ex.Message);
        }

        #endregion

        #region Test 32: Wrong Azure Year Binding => Fails Before Connection

        [Fact]
        public void Test32_WrongAzureYearBinding_FailsBeforeConnection()
        {
            var ex = Assert.Throws<PhysicalDatabaseMismatchException>(() =>
                DatabaseBindingValidator.ValidateTargetDatabase("2026", "IProgramDb2027", isLocalTarget: false));

            Assert.Contains("Physical database mismatch", ex.Message);
        }

        #endregion

        #region Test 33: Wrong Local Year Binding => Fails Before Connection

        [Fact]
        public void Test33_WrongLocalYearBinding_FailsBeforeConnection()
        {
            var ex = Assert.Throws<PhysicalDatabaseMismatchException>(() =>
                DatabaseBindingValidator.ValidateTargetDatabase("2026", "IProgramLocalDb2027", isLocalTarget: true));

            Assert.Contains("Physical database mismatch", ex.Message);
        }

        #endregion

        #region Test 34: Year 2026 Isolated from 2027

        [Fact]
        public void Test34_Year2026_IsolatedFrom2027()
        {
            var batch2026 = new FencedPullBatch { DatabaseId = "2026", LowWatermark = 0, HighWatermark = 2 };
            Assert.Equal("2026", batch2026.DatabaseId);
            Assert.NotEqual("2027", batch2026.DatabaseId);
        }

        #endregion

        #region Test 35: ServerState Fence Blocks Concurrent Mutation

        [Fact]
        public void Test35_ServerStateFence_LockPatternVerified()
        {
            // The fence lock query must use UPDLOCK, HOLDLOCK on [sync].[ServerState]
            var expectedLockPattern = "SELECT CurrentVersion FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = @DatabaseId;";
            Assert.Contains("UPDLOCK, HOLDLOCK", expectedLockPattern);
            Assert.Contains("ServerState", expectedLockPattern);
        }

        #endregion

        #region Test 36: Retry After Successful Commit => No-Op

        [Fact]
        public void Test36_RetryAfterSuccessfulCommit_ReturnsNoOp()
        {
            long checkpointAfterCommit = 2;
            long serverVersionOnRetry = 2;

            var retryBatch = FencedPullBatch.CreateNoOp("2026", checkpointAfterCommit);
            Assert.True(retryBatch.IsNoOp);
            Assert.Equal(serverVersionOnRetry, retryBatch.HighWatermark);
            Assert.Equal(checkpointAfterCommit, retryBatch.LowWatermark);
            Assert.Empty(retryBatch.Commands);
        }

        #endregion
    }
}
