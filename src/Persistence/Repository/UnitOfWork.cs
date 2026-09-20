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
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;

namespace Persistence.Repository
{
    public class UnitOfWork : IUnitOfWork
    {
        private readonly ApplicationContext _context;
        private readonly IDbConnectionProvider? _dbConnectionProvider;
        private readonly IAuthoritativeDailyMutationTracker? _authoritativeTracker;
        private readonly IAuthoritativeDatabaseBindingGuard? _bindingGuard;
        private readonly IConfiguration? _configuration;

        public UnitOfWork(
            ApplicationContext context,
            IDbConnectionProvider? dbConnectionProvider = null,
            IAuthoritativeDailyMutationTracker? authoritativeTracker = null,
            IAuthoritativeDatabaseBindingGuard? bindingGuard = null,
            IConfiguration? configuration = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbConnectionProvider = dbConnectionProvider;
            _authoritativeTracker = authoritativeTracker;
            _bindingGuard = bindingGuard;
            _configuration = configuration;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // 1. OfflineReadWritePilot mode: coordinate transactional outbox
            if (_dbConnectionProvider is ISyncConnectionProvider syncProvider &&
                syncProvider.IsLocalFirstEnabled &&
                !syncProvider.IsReadOnlyMode)
            {
                return await SaveChangesInOfflineWritePilotAsync(syncProvider, cancellationToken);
            }

            // 2. Online mode with Authoritative Tracking enabled: coordinate authoritative Azure sync tracking
            var isAuthoritativeTrackingEnabled = _configuration?.GetValue<bool>("Sync:AuthoritativeTrackingEnabled", false) == true;
            if (isAuthoritativeTrackingEnabled)
            {
                var isLocalFirst = (_dbConnectionProvider as ISyncConnectionProvider)?.IsLocalFirstEnabled == true;
                var isReadOnly = (_dbConnectionProvider as ISyncConnectionProvider)?.IsReadOnlyMode == true;

                if (!isLocalFirst && !isReadOnly)
                {
                    // Mandatory dependency validation for Authoritative Tracking: FAIL CLOSED
                    if (_dbConnectionProvider is not ISyncConnectionProvider onlineSyncProvider)
                    {
                        throw new AuthoritativeTrackingConfigurationException(
                            "ISyncConnectionProvider dependency is missing while Sync:AuthoritativeTrackingEnabled is true.");
                    }

                    if (_authoritativeTracker == null)
                    {
                        throw new AuthoritativeTrackingConfigurationException(
                            "IAuthoritativeDailyMutationTracker dependency is missing while Sync:AuthoritativeTrackingEnabled is true.");
                    }

                    if (_bindingGuard == null)
                    {
                        throw new AuthoritativeTrackingConfigurationException(
                            "IAuthoritativeDatabaseBindingGuard dependency is missing while Sync:AuthoritativeTrackingEnabled is true.");
                    }

                    return await SaveChangesInAuthoritativeOnlineAsync(onlineSyncProvider, cancellationToken);
                }
            }

            // 3. Otherwise (ReadOnlyMode, or Online with gate off, or standard provider): standard EF Core save pipeline
            return await _context.SaveChangesAsync(cancellationToken);
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

        private async Task<int> SaveChangesInAuthoritativeOnlineAsync(
            ISyncConnectionProvider syncProvider,
            CancellationToken cancellationToken)
        {
            // 1. Detect pending modifications
            if (!_context.ChangeTracker.HasChanges())
            {
                return 0;
            }

            // 2. Capture and classify Daily mutations
            var entries = _context.ChangeTracker.Entries()
                .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                .ToList();

            var capturedMutations = new List<CapturedAuthoritativeDailyMutation>();

            foreach (var entry in entries)
            {
                if (entry.Entity is Daily daily)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (daily.SyncId == Guid.Empty)
                        {
                            daily.SyncId = Guid.NewGuid();
                        }

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            Daily = daily,
                            OperationType = "INSERT",
                            EntitySyncId = daily.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(Daily.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new AuthoritativeTrackingException("تعديل SyncId لسجل يومية موجود محظور تماماً (SyncId is immutable).");
                        }

                        if (daily.SyncId == Guid.Empty)
                        {
                            throw new AuthoritativeTrackingException("Daily entity has empty SyncId on modification.");
                        }

                        var isActiveProperty = entry.Property(nameof(Daily.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && daily.IsActive == false;

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            Daily = daily,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            EntitySyncId = daily.SyncId,
                            OriginalSnapshot = CaptureDailyOriginalSnapshot(entry)
                        });
                    }
                    else if (entry.State == EntityState.Deleted)
                    {
                        var syncIdProperty = entry.Property(nameof(Daily.SyncId));
                        var syncId = (Guid)(syncIdProperty.OriginalValue ?? daily.SyncId);
                        if (syncId == Guid.Empty)
                        {
                            throw new AuthoritativeTrackingException("Daily entity has empty SyncId on hard delete.");
                        }

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            Daily = daily,
                            OperationType = "HARD_DELETE",
                            EntitySyncId = syncId,
                            OriginalSnapshot = CaptureDailyOriginalSnapshot(entry)
                        });
                    }
                }
            }

            // If no Daily mutations are present, normal online SaveChanges proceeds (e.g. Employee, Form)
            if (capturedMutations.Count == 0)
            {
                return await _context.SaveChangesAsync(cancellationToken);
            }

            var databaseId = syncProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is missing for authoritative tracking.");
            }

            var dbConnection = _context.Database.GetDbConnection();

            // P0: Validate physical Azure binding BEFORE opening connection or beginning transaction
            string? preDataSource = null;
            string? preInitialCatalog = null;
            var connStr = dbConnection.ConnectionString;
            if (!string.IsNullOrWhiteSpace(connStr))
            {
                try
                {
                    var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connStr);
                    preDataSource = csb.DataSource;
                    preInitialCatalog = csb.InitialCatalog;
                }
                catch (ArgumentException)
                {
                    preDataSource = dbConnection.DataSource;
                    preInitialCatalog = dbConnection.Database;
                }
            }
            else
            {
                preDataSource = dbConnection.DataSource;
                preInitialCatalog = dbConnection.Database;
            }

            var tracker = _authoritativeTracker!;
            var bindingGuard = _bindingGuard!;

            bindingGuard.ValidateAuthoritativeAzureBinding(databaseId, preDataSource, preInitialCatalog);

            // 3. Coordinate atomic transaction using EF Core execution strategy
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var scope = AuthoritativeWriteScopeContext.BeginScope();
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    if (dbConnection.State != ConnectionState.Open)
                    {
                        await dbConnection.OpenAsync(cancellationToken);
                    }

                    // Defense-in-depth: Validate physical Azure binding on opened connection
                    bindingGuard.ValidateAuthoritativeAzureBinding(databaseId, dbConnection.DataSource, dbConnection.Database);

                    // P0: Lock ServerState & Prepare reservation BEFORE EF business SaveChanges (eliminates lock order inversion)
                    var reservation = await tracker.PrepareAuthoritativeBatchAsync(
                        dbConnection,
                        transaction.GetDbTransaction(),
                        databaseId,
                        capturedMutations,
                        cancellationToken);

                    // Step B: Save business changes without accepting changes yet (ServerState lock already held!)
                    var saveResult = await _context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

                    // Step C: Apply authoritative sync tracking metadata (Tombstones, ServerChangeFeed, update ServerState)
                    await tracker.CompleteAuthoritativeBatchAsync(
                        dbConnection,
                        transaction.GetDbTransaction(),
                        databaseId,
                        reservation,
                        cancellationToken);

                    // Step D: Commit transaction atomically
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

        internal static AuthoritativeDailyOriginalSnapshot CaptureDailyOriginalSnapshot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var originalValues = entry.OriginalValues;
            return new AuthoritativeDailyOriginalSnapshot
            {
                SyncId = (Guid)originalValues[nameof(Daily.SyncId)]!,
                Name = (string)originalValues[nameof(Daily.Name)]!,
                DailyDate = (DateTime)originalValues[nameof(Daily.DailyDate)]!,
                Closed = (bool)originalValues[nameof(Daily.Closed)]!,
                CreatedAt = (DateTime)originalValues[nameof(Daily.CreatedAt)]!,
                CreatedBy = (string?)originalValues[nameof(Daily.CreatedBy)],
                UpdatedAt = (DateTime?)originalValues[nameof(Daily.UpdatedAt)],
                UpdatedBy = (string?)originalValues[nameof(Daily.UpdatedBy)],
                DeactivatedAt = (DateTime?)originalValues[nameof(Daily.DeactivatedAt)],
                DeactivatedBy = (string?)originalValues[nameof(Daily.DeactivatedBy)],
                IsActive = (bool)originalValues[nameof(Daily.IsActive)]!
            };
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