#nullable enable
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Persistence.Repository
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ApplicationContext _context;
        private readonly IDbConnectionProvider? _dbConnectionProvider;

        public UnitOfWork(ApplicationContext context, IDbConnectionProvider? dbConnectionProvider = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbConnectionProvider = dbConnectionProvider;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // If LocalFirst is not active, or in ReadOnlyMode, proceed through standard EF Core save pipeline
            if (_dbConnectionProvider is not ISyncConnectionProvider syncProvider ||
                !syncProvider.IsLocalFirstEnabled ||
                syncProvider.IsReadOnlyMode)
            {
                return await _context.SaveChangesAsync(cancellationToken);
            }

            // OfflineReadWritePilot mode: coordinate transactional outbox
            return await SaveChangesInOfflineWritePilotAsync(syncProvider, cancellationToken);
        }

        private async Task<int> SaveChangesInOfflineWritePilotAsync(
            ISyncConnectionProvider syncProvider,
            CancellationToken cancellationToken)
        {
            // 1. Detect pending modifications
            if (!_context.ChangeTracker.HasChanges())
            {
                return 0;
            }

            // 2. Validate scope and guard against non-Daily entities and hard deletes
            var entries = _context.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();

            var capturedMutations = new List<CapturedDailyMutation>();

            foreach (var entry in entries)
            {
                if (entry.Entity is not Daily daily)
                {
                    throw new OfflineWriteScopeException(
                        $"الكيان من نوع '{entry.Metadata.ClrType.Name}' غير مصرح بتعديله في وضع Offline Read-Write Pilot. العمليات المصرح بها محصورة في Daily فقط.");
                }

                if (entry.State == EntityState.Deleted)
                {
                    throw new OfflineWriteScopeException(
                        "الحذف الفعلي (Hard Delete) غير مسموح به في وضع Offline Read-Write Pilot. يجب استخدام الحذف المنطقي (Soft Delete) فقط.");
                }

                if (entry.State == EntityState.Added)
                {
                    if (daily.SyncId == Guid.Empty)
                    {
                        daily.SyncId = Guid.NewGuid();
                    }

                    capturedMutations.Add(new CapturedDailyMutation
                    {
                        Daily = daily,
                        OperationType = "INSERT",
                        CommandName = "Daily.Insert",
                        ClientOperationId = Guid.NewGuid(),
                        EntitySyncId = daily.SyncId
                    });
                }
                else if (entry.State == EntityState.Modified)
                {
                    var syncIdProperty = entry.Property(nameof(Daily.SyncId));
                    if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                    {
                        throw new OfflineWriteScopeException("تعديل SyncId لسجل موجود غير مسموح به.");
                    }

                    var isActiveProperty = entry.Property(nameof(Daily.IsActive));
                    bool isSoftDelete = isActiveProperty.OriginalValue is true && daily.IsActive == false;

                    string operationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE";
                    string commandName = isSoftDelete ? "Daily.SoftDelete" : "Daily.Update";

                    capturedMutations.Add(new CapturedDailyMutation
                    {
                        Daily = daily,
                        OperationType = operationType,
                        CommandName = commandName,
                        ClientOperationId = Guid.NewGuid(),
                        EntitySyncId = daily.SyncId
                    });
                }
            }

            // 3. Coordinate atomic transaction using EF Core execution strategy (compatible with EnableRetryOnFailure)
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var scope = LocalWriteScopeContext.BeginScope();
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    // Step A: Save business changes without accepting changes yet
                    var saveResult = await _context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

                    var dbConnection = _context.Database.GetDbConnection();
                    if (dbConnection.State != ConnectionState.Open)
                    {
                        await dbConnection.OpenAsync(cancellationToken);
                    }

                    var dbTransaction = transaction.GetDbTransaction();
                    var databaseId = syncProvider.GetSelectedDatabaseId();
                    if (string.IsNullOrWhiteSpace(databaseId))
                    {
                        throw new InvalidDatabaseSelectionException("Canonical database ID is missing for outbox coordination.");
                    }

                    // Step B: Query LocalState on the exact same connection and transaction
                    var (deviceId, lastServerVersion) = await FetchLocalStateAsync(
                        dbConnection, dbTransaction, databaseId, cancellationToken);

                    // Step C: Insert corresponding [sync].[LocalOutbox] records on the exact same connection and transaction
                    var operationTimestamp = DateTime.UtcNow;
                    foreach (var mutation in capturedMutations)
                    {
                        var payloadJson = BuildDeterministicPayloadJson(
                            operationType: mutation.OperationType,
                            databaseId: databaseId,
                            deviceId: deviceId,
                            baseServerVersion: lastServerVersion,
                            daily: mutation.Daily,
                            timestampUtc: operationTimestamp);

                        await InsertOutboxRecordAsync(
                            dbConnection: dbConnection,
                            dbTransaction: dbTransaction,
                            clientOperationId: mutation.ClientOperationId,
                            databaseId: databaseId,
                            aggregateType: "Daily",
                            commandName: mutation.CommandName,
                            entitySyncId: mutation.EntitySyncId,
                            payloadJson: payloadJson,
                            createdAtUtc: operationTimestamp,
                            cancellationToken: cancellationToken);
                    }

                    // Step D: Commit transaction atomically (both business write and outbox write succeed together)
                    await transaction.CommitAsync(cancellationToken);

                    // Step E: Accept tracked changes only after successful commit
                    _context.ChangeTracker.AcceptAllChanges();

                    return saveResult;
                }
                catch
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw;
                }
            });
        }

        private static async Task<(Guid DeviceId, long LastServerVersion)> FetchLocalStateAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            CancellationToken cancellationToken)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT TOP (1) [DeviceId], [LastServerVersion] FROM [sync].[LocalState] WHERE [DatabaseId] = @DatabaseId";

            var param = cmd.CreateParameter();
            param.ParameterName = "@DatabaseId";
            param.Value = databaseId;
            cmd.Parameters.Add(param);

            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"LocalState record for DatabaseId '{databaseId}' was not found. Write operation failed-closed.");
            }

            var deviceId = reader.GetGuid(0);
            var lastServerVersion = reader.GetInt64(1);

            if (deviceId == Guid.Empty)
            {
                throw new InvalidOperationException($"LocalState record for DatabaseId '{databaseId}' has empty DeviceId. Write operation failed-closed.");
            }

            return (deviceId, lastServerVersion);
        }

        private static async Task InsertOutboxRecordAsync(
            DbConnection dbConnection,
            DbTransaction dbTransaction,
            Guid clientOperationId,
            string databaseId,
            string aggregateType,
            string commandName,
            Guid entitySyncId,
            string payloadJson,
            DateTime createdAtUtc,
            CancellationToken cancellationToken)
        {
            using var cmd = dbConnection.CreateCommand();
            cmd.Transaction = dbTransaction;
            cmd.CommandText = @"
                INSERT INTO [sync].[LocalOutbox] (
                    [ClientOperationId],
                    [DatabaseId],
                    [AggregateType],
                    [CommandName],
                    [EntitySyncId],
                    [PayloadJson],
                    [CreatedAtUtc],
                    [Status],
                    [RetryCount],
                    [LastError],
                    [CompletedAtUtc],
                    [LockedUntilUtc],
                    [LockToken]
                ) VALUES (
                    @ClientOperationId,
                    @DatabaseId,
                    @AggregateType,
                    @CommandName,
                    @EntitySyncId,
                    @PayloadJson,
                    @CreatedAtUtc,
                    @Status,
                    @RetryCount,
                    NULL,
                    NULL,
                    NULL,
                    NULL
                )";

            AddParam(cmd, "@ClientOperationId", clientOperationId);
            AddParam(cmd, "@DatabaseId", databaseId);
            AddParam(cmd, "@AggregateType", aggregateType);
            AddParam(cmd, "@CommandName", commandName);
            AddParam(cmd, "@EntitySyncId", entitySyncId);
            AddParam(cmd, "@PayloadJson", payloadJson);
            AddParam(cmd, "@CreatedAtUtc", createdAtUtc);
            AddParam(cmd, "@Status", "PENDING");
            AddParam(cmd, "@RetryCount", 0);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void AddParam(DbCommand cmd, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        public static string BuildDeterministicPayloadJson(
            string operationType,
            string databaseId,
            Guid deviceId,
            long baseServerVersion,
            Daily daily,
            DateTime timestampUtc)
        {
            var scalarData = new SortedDictionary<string, object?>
            {
                ["Closed"] = daily.Closed,
                ["CreatedAt"] = daily.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["CreatedBy"] = daily.CreatedBy,
                ["DailyDate"] = daily.DailyDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["DeactivatedAt"] = daily.DeactivatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["DeactivatedBy"] = daily.DeactivatedBy,
                ["Id"] = daily.Id,
                ["IsActive"] = daily.IsActive,
                ["Name"] = daily.Name,
                ["SyncId"] = daily.SyncId.ToString(),
                ["UpdatedAt"] = daily.UpdatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["UpdatedBy"] = daily.UpdatedBy
            };

            var envelope = new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = baseServerVersion,
                ["createdAtUtc"] = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ["databaseId"] = databaseId,
                ["deviceId"] = deviceId.ToString(),
                ["entityData"] = scalarData,
                ["entitySyncId"] = daily.SyncId.ToString(),
                ["entityType"] = "Daily",
                ["operationType"] = operationType,
                ["schemaVersion"] = 1
            };

            return JsonSerializer.Serialize(envelope);
        }

        private sealed class CapturedDailyMutation
        {
            public required Daily Daily { get; init; }
            public required string OperationType { get; init; }
            public required string CommandName { get; init; }
            public required Guid ClientOperationId { get; init; }
            public required Guid EntitySyncId { get; init; }
        }
    }
}