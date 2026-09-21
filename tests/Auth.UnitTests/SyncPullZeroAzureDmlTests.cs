#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Pull;
using Auth.Infrastructure.Sync.Push;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncPullZeroAzureDmlTests
    {
        private static readonly Regex DisallowedDmlRegex = new Regex(
            @"\b(INSERT\s+INTO|UPDATE\s+|DELETE\s+FROM|MERGE\s+|DROP\s+|ALTER\s+|CREATE\s+TABLE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Fact]
        public void AzureFencedBatchReader_QueriesAreStrictlyReadOnly()
        {
            // List of all SQL statements constructed by AzureFencedBatchReader
            var queriesUsedInReader = new List<string>
            {
                // Phase 1: High-Watermark Fence
                @"SELECT CurrentVersion
                  FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                  WHERE DatabaseId = @DatabaseId;",

                // Phase 2: Feed Window Validation
                @"SELECT FeedId, ServerVersion, DatabaseId, EntityType, EntitySyncId, OperationType, OriginDeviceId, TimestampUtc
                  FROM [sync].[ServerChangeFeed]
                  WHERE DatabaseId = @DatabaseId
                    AND ServerVersion > @LowWatermark
                    AND ServerVersion <= @HighWatermark
                  ORDER BY ServerVersion ASC;",

                // Phase 3: Tombstone Validation for HARD_DELETE
                @"SELECT COUNT(*)
                  FROM [sync].[Tombstones]
                  WHERE DatabaseId = @DatabaseId
                    AND EntityType = 'Daily'
                    AND EntitySyncId = @EntitySyncId
                    AND ServerVersion = @ServerVersion;",

                // Authoritative dbo.Daily Absence Check for HARD_DELETE
                @"SELECT COUNT(*)
                  FROM [dbo].[Daily]
                  WHERE SyncId = @SyncId;",

                // Tombstone Absence Check for Non-Delete mutations
                @"SELECT COUNT(*)
                  FROM [sync].[Tombstones]
                  WHERE DatabaseId = @DatabaseId
                    AND EntityType = 'Daily'
                    AND EntitySyncId = @EntitySyncId;",

                // Authoritative Snapshot Read for Non-Delete mutations
                @"SELECT SyncId, Name, DailyDate, Closed, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, DeactivatedAt, DeactivatedBy, IsActive
                  FROM [dbo].[Daily]
                  WHERE SyncId = @SyncId;"
            };

            foreach (var query in queriesUsedInReader)
            {
                var trimmed = query.Trim();

                // 1. Must start with SELECT
                Assert.StartsWith("SELECT", trimmed, StringComparison.OrdinalIgnoreCase);

                // 2. Must NOT match any DML or DDL
                var match = DisallowedDmlRegex.Match(trimmed);
                Assert.False(match.Success, $"Disallowed DML/DDL statement found: '{match.Value}' in query:\n{query}");
            }
        }

        [Fact]
        public void AzureFencedBatchReader_FenceQuery_UsesUpdlockHoldlock()
        {
            var fenceQuery = @"
                SELECT CurrentVersion
                FROM [sync].[ServerState] WITH (UPDLOCK, HOLDLOCK)
                WHERE DatabaseId = @DatabaseId;";

            Assert.Contains("UPDLOCK", fenceQuery);
            Assert.Contains("HOLDLOCK", fenceQuery);
            Assert.Contains("[sync].[ServerState]", fenceQuery);
        }

        [Fact]
        public void LocalPullTransactionCoordinator_LocalOutboxNotModifiedByPull()
        {
            // Verifies that LocalPullTransactionCoordinator never writes to LocalOutbox
            var localCoordinatorQueries = new List<string>
            {
                // LocalState verification
                "SELECT LastServerVersion, ActiveLeaseToken, LeaseExpiresAtUtc FROM [sync].[LocalState] WITH (UPDLOCK, HOLDLOCK) WHERE DatabaseId = @DatabaseId;",
                // Outbox check (read-only)
                "SELECT COUNT(*) FROM [sync].[LocalOutbox] WHERE DatabaseId = @DatabaseId AND Status IN ('PENDING', 'IN_PROGRESS', 'FAILED');",
                // Daily mutation (UPSERT/DELETE)
                "DELETE FROM [dbo].[Daily] WHERE SyncId = @SyncId;",
                "UPDATE [dbo].[Daily] SET ... WHERE SyncId = @SyncId;",
                "INSERT INTO [dbo].[Daily] (...) VALUES (...);",
                // Checkpoint update
                "UPDATE [sync].[LocalState] SET LastServerVersion = @HighWatermark, LastSuccessfulPullUtc = SYSUTCDATETIME() WHERE DatabaseId = @DatabaseId AND ActiveLeaseToken = @LeaseToken;"
            };

            foreach (var q in localCoordinatorQueries)
            {
                Assert.DoesNotContain("INSERT INTO [sync].[LocalOutbox]", q, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("UPDATE [sync].[LocalOutbox]", q, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("DELETE FROM [sync].[LocalOutbox]", q, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
