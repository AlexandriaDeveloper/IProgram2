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
using Core.Sync.Registry;
using Core.Sync.Serialization;
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
        private readonly ILocalScopeBaselineService? _scopeBaselineService;
        public static readonly TimeSpan MonotonicQueueIncrement = TimeSpan.FromTicks(10);

        public UnitOfWork(
            ApplicationContext context,
            IDbConnectionProvider? dbConnectionProvider = null,
            IAuthoritativeDailyMutationTracker? authoritativeTracker = null,
            IAuthoritativeDatabaseBindingGuard? bindingGuard = null,
            IConfiguration? configuration = null,
            ILocalScopeBaselineService? scopeBaselineService = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _dbConnectionProvider = dbConnectionProvider;
            _authoritativeTracker = authoritativeTracker;
            _bindingGuard = bindingGuard;
            _configuration = configuration;
            _scopeBaselineService = scopeBaselineService;
        }

        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // 0. LocalOnlyProduction mode: direct local persistence without Outbox or Azure tracking
            if (_dbConnectionProvider is ISyncConnectionProvider localProdProvider &&
                localProdProvider.IsLocalOnlyProduction)
            {
                var isChangeCaptureEnabled = localProdProvider.IsLocalOnlyChangeCaptureEnabled ||
                                             _configuration?.GetValue<bool>("Sync:LocalOnlyChangeCaptureEnabled", false) == true ||
                                             _configuration?.GetValue<bool>("LocalFirst:LocalOnlyChangeCaptureEnabled", false) == true;

                if (!isChangeCaptureEnabled)
                {
                    return await _context.SaveChangesAsync(cancellationToken);
                }

                return await SaveChangesInLocalOnlyCaptureAsync(localProdProvider, cancellationToken);
            }

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
                var isLocalOnlyProd = (_dbConnectionProvider as ISyncConnectionProvider)?.IsLocalOnlyProduction == true;

                if (!isLocalFirst && !isReadOnly && !isLocalOnlyProd)
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

            var capturedMutations = new List<CapturedOfflineMutation>();

            foreach (var entry in entries)
            {
                if (entry.State == EntityState.Deleted)
                {
                    throw new OfflineWriteScopeException(
                        "الحذف الفعلي (Hard Delete) غير مسموح به في وضع Offline Read-Write Pilot. يجب استخدام الحذف المنطقي (Soft Delete) فقط.");
                }

                if (entry.Entity is Daily daily)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (daily.SyncId == Guid.Empty) daily.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = daily,
                            EntityType = "Daily",
                            AggregateType = "Daily",
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

                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = daily,
                            EntityType = "Daily",
                            AggregateType = "Daily",
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            CommandName = isSoftDelete ? "Daily.SoftDelete" : "Daily.Update",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = daily.SyncId
                        });
                    }
                }
                else if (entry.Entity is Form form)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (form.SyncId == Guid.Empty) form.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = form,
                            EntityType = "Form",
                            AggregateType = "Form",
                            OperationType = "INSERT",
                            CommandName = "Form.Insert",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = form.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(Form.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new OfflineWriteScopeException("تعديل SyncId لسجل موجود غير مسموح به.");
                        }

                        var isActiveProperty = entry.Property(nameof(Form.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && form.IsActive == false;

                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = form,
                            EntityType = "Form",
                            AggregateType = "Form",
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            CommandName = isSoftDelete ? "Form.SoftDelete" : "Form.Update",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = form.SyncId
                        });
                    }
                }
                else if (entry.Entity is FormDetails formDetails)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (formDetails.SyncId == Guid.Empty) formDetails.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = formDetails,
                            EntityType = "FormDetails",
                            AggregateType = "FormDetails",
                            OperationType = "INSERT",
                            CommandName = "FormDetails.Insert",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = formDetails.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(FormDetails.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new OfflineWriteScopeException("تعديل SyncId لسجل موجود غير مسموح به.");
                        }

                        var isActiveProperty = entry.Property(nameof(FormDetails.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && formDetails.IsActive == false;

                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = formDetails,
                            EntityType = "FormDetails",
                            AggregateType = "FormDetails",
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            CommandName = isSoftDelete ? "FormDetails.SoftDelete" : "FormDetails.Update",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = formDetails.SyncId
                        });
                    }
                }
                else
                {
                    throw new OfflineWriteScopeException(
                        $"الكيان من نوع '{entry.Metadata.ClrType.Name}' غير مصرح بتعديله في وضع Offline Read-Write Pilot. العمليات المصرح بها محصورة في Daily و Form و FormDetails فقط.");
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

                    // Step 1: Query and Lock LocalState with UPDLOCK, HOLDLOCK and validate no active sync lease exists
                    var (deviceId, lastServerVersion) = await LockAndValidateLocalStateForOfflineWriteAsync(
                        dbConnection, dbTransaction, databaseId, cancellationToken);

                    // Step 1b: Verify Scope Baseline for Forms if Form/FormDetails are present (FAIL CLOSED)
                    if (capturedMutations.Any(m => m.EntityType == "Form" || m.EntityType == "FormDetails"))
                    {
                        var baselineService = _scopeBaselineService ?? new Auth.Infrastructure.Sync.LocalScopeBaselineService(
                            Microsoft.Extensions.Logging.Abstractions.NullLogger<Auth.Infrastructure.Sync.LocalScopeBaselineService>.Instance);
                        await baselineService.EnsureScopeBaselinedAsync(
                            dbConnection, dbTransaction, databaseId, "Forms", cancellationToken);
                    }

                    // Step 2: Save business changes without accepting changes yet
                    var saveResult = await _context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

                    // Step 3: Insert corresponding [sync].[LocalOutbox] records in deterministic topological dependency order
                    var orderedMutations = capturedMutations
                        .OrderBy(GetOfflineMutationTopologicalRank)
                        .ToList();

                    var previousMaxCreatedAtUtc = await GetMaxOutboxCreatedAtUtcAsync(
                        dbConnection, dbTransaction, databaseId, cancellationToken);
                    var now = DateTime.UtcNow;
                    var minAllowed = previousMaxCreatedAtUtc.HasValue
                        ? previousMaxCreatedAtUtc.Value.Add(MonotonicQueueIncrement)
                        : DateTime.MinValue;
                    var baseTimestamp = now > minAllowed ? now : minAllowed;

                    for (int i = 0; i < orderedMutations.Count; i++)
                    {
                        var mutation = orderedMutations[i];
                        // Strictly monotonic timestamp ensuring topological FIFO ordering in SQL DATETIME2(7)
                        var operationTimestamp = baseTimestamp.AddTicks(i * MonotonicQueueIncrement.Ticks);

                        string payloadJson;
                        if (mutation.Entity is Daily d)
                        {
                            payloadJson = BuildDeterministicPayloadJson(
                                operationType: mutation.OperationType,
                                databaseId: databaseId,
                                deviceId: deviceId,
                                baseServerVersion: lastServerVersion,
                                daily: d,
                                timestampUtc: operationTimestamp);
                        }
                        else if (mutation.Entity is Form f)
                        {
                            var dailySyncId = await ResolveDailySyncIdAsync(f, dbConnection, dbTransaction, cancellationToken);
                            payloadJson = BuildDeterministicFormPayloadJson(
                                operationType: mutation.OperationType,
                                databaseId: databaseId,
                                deviceId: deviceId,
                                baseServerVersion: lastServerVersion,
                                form: f,
                                dailySyncId: dailySyncId,
                                timestampUtc: operationTimestamp);
                        }
                        else if (mutation.Entity is FormDetails fd)
                        {
                            var formSyncId = await ResolveFormSyncIdAsync(fd, dbConnection, dbTransaction, cancellationToken);
                            payloadJson = BuildDeterministicFormDetailsPayloadJson(
                                operationType: mutation.OperationType,
                                databaseId: databaseId,
                                deviceId: deviceId,
                                baseServerVersion: lastServerVersion,
                                formDetails: fd,
                                formSyncId: formSyncId,
                                timestampUtc: operationTimestamp);
                        }
                        else
                        {
                            throw new OfflineWriteScopeException($"Unsupported entity type '{mutation.EntityType}' for outbox payload.");
                        }

                        await InsertOutboxRecordAsync(
                            dbConnection: dbConnection,
                            dbTransaction: dbTransaction,
                            clientOperationId: mutation.ClientOperationId,
                            databaseId: databaseId,
                            aggregateType: mutation.AggregateType,
                            commandName: mutation.CommandName,
                            entitySyncId: mutation.EntitySyncId,
                            payloadJson: payloadJson,
                            createdAtUtc: operationTimestamp,
                            cancellationToken: cancellationToken);
                    }

                    // Step 4: Commit transaction atomically (both business write and outbox write succeed together)
                    await transaction.CommitAsync(cancellationToken);

                    // Step 5: Accept tracked changes only after successful commit
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
                        if (daily.SyncId == Guid.Empty) daily.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Daily",
                            Daily = daily,
                            Entity = daily,
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

                        if (daily.SyncId == Guid.Empty) throw new AuthoritativeTrackingException("Daily entity has empty SyncId on modification.");

                        var isActiveProperty = entry.Property(nameof(Daily.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && daily.IsActive == false;

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Daily",
                            Daily = daily,
                            Entity = daily,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            EntitySyncId = daily.SyncId,
                            OriginalSnapshot = CaptureDailyOriginalSnapshot(entry)
                        });
                    }
                    else if (entry.State == EntityState.Deleted)
                    {
                        var syncIdProperty = entry.Property(nameof(Daily.SyncId));
                        var syncId = (Guid)(syncIdProperty.OriginalValue ?? daily.SyncId);
                        if (syncId == Guid.Empty) throw new AuthoritativeTrackingException("Daily entity has empty SyncId on hard delete.");

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Daily",
                            Daily = daily,
                            Entity = daily,
                            OperationType = "HARD_DELETE",
                            EntitySyncId = syncId,
                            OriginalSnapshot = CaptureDailyOriginalSnapshot(entry)
                        });
                    }
                }
                else if (entry.Entity is Form form)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (form.SyncId == Guid.Empty) form.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Form",
                            Entity = form,
                            OperationType = "INSERT",
                            EntitySyncId = form.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(Form.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new AuthoritativeTrackingException("تعديل SyncId لسجل ملف موجود محظور تماماً (SyncId is immutable).");
                        }

                        if (form.SyncId == Guid.Empty) throw new AuthoritativeTrackingException("Form entity has empty SyncId on modification.");

                        var isActiveProperty = entry.Property(nameof(Form.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && form.IsActive == false;

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Form",
                            Entity = form,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            EntitySyncId = form.SyncId,
                            FormOriginalSnapshot = CaptureFormOriginalSnapshot(entry)
                        });
                    }
                    else if (entry.State == EntityState.Deleted)
                    {
                        var syncIdProperty = entry.Property(nameof(Form.SyncId));
                        var syncId = (Guid)(syncIdProperty.OriginalValue ?? form.SyncId);
                        if (syncId == Guid.Empty) throw new AuthoritativeTrackingException("Form entity has empty SyncId on hard delete.");

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "Form",
                            Entity = form,
                            OperationType = "HARD_DELETE",
                            EntitySyncId = syncId,
                            FormOriginalSnapshot = CaptureFormOriginalSnapshot(entry)
                        });
                    }
                }
                else if (entry.Entity is FormDetails formDetails)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (formDetails.SyncId == Guid.Empty) formDetails.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormDetails",
                            Entity = formDetails,
                            OperationType = "INSERT",
                            EntitySyncId = formDetails.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(FormDetails.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new AuthoritativeTrackingException("تعديل SyncId لتفاصيل ملف موجود محظور تماماً (SyncId is immutable).");
                        }

                        if (formDetails.SyncId == Guid.Empty) throw new AuthoritativeTrackingException("FormDetails entity has empty SyncId on modification.");

                        var isActiveProperty = entry.Property(nameof(FormDetails.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && formDetails.IsActive == false;

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormDetails",
                            Entity = formDetails,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            EntitySyncId = formDetails.SyncId,
                            FormDetailsOriginalSnapshot = CaptureFormDetailsOriginalSnapshot(entry)
                        });
                    }
                    else if (entry.State == EntityState.Deleted)
                    {
                        var syncIdProperty = entry.Property(nameof(FormDetails.SyncId));
                        var syncId = (Guid)(syncIdProperty.OriginalValue ?? formDetails.SyncId);
                        if (syncId == Guid.Empty) throw new AuthoritativeTrackingException("FormDetails entity has empty SyncId on hard delete.");

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormDetails",
                            Entity = formDetails,
                            OperationType = "HARD_DELETE",
                            EntitySyncId = syncId,
                            FormDetailsOriginalSnapshot = CaptureFormDetailsOriginalSnapshot(entry)
                        });
                    }
                }
                else if (entry.Entity is FormRefernce formRefernce)
                {
                    if (entry.State == EntityState.Added)
                    {
                        if (formRefernce.SyncId == Guid.Empty) formRefernce.SyncId = Guid.NewGuid();
                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormRefernce",
                            Entity = formRefernce,
                            OperationType = "INSERT",
                            EntitySyncId = formRefernce.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProperty = entry.Property(nameof(FormRefernce.SyncId));
                        if (syncIdProperty.IsModified && !Equals(syncIdProperty.OriginalValue, syncIdProperty.CurrentValue))
                        {
                            throw new AuthoritativeTrackingException("تعديل SyncId لمرجع ملف موجود محظور تماماً (SyncId is immutable).");
                        }

                        if (formRefernce.SyncId == Guid.Empty) throw new AuthoritativeTrackingException("FormRefernce entity has empty SyncId on modification.");

                        var isActiveProperty = entry.Property(nameof(FormRefernce.IsActive));
                        bool isSoftDelete = isActiveProperty.OriginalValue is true && formRefernce.IsActive == false;

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormRefernce",
                            Entity = formRefernce,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            EntitySyncId = formRefernce.SyncId,
                            FormRefernceOriginalSnapshot = CaptureFormRefernceOriginalSnapshot(entry)
                        });
                    }
                    else if (entry.State == EntityState.Deleted)
                    {
                        var syncIdProperty = entry.Property(nameof(FormRefernce.SyncId));
                        var syncId = (Guid)(syncIdProperty.OriginalValue ?? formRefernce.SyncId);
                        if (syncId == Guid.Empty) throw new AuthoritativeTrackingException("FormRefernce entity has empty SyncId on hard delete.");

                        capturedMutations.Add(new CapturedAuthoritativeDailyMutation
                        {
                            EntityType = "FormRefernce",
                            Entity = formRefernce,
                            OperationType = "HARD_DELETE",
                            EntitySyncId = syncId,
                            FormRefernceOriginalSnapshot = CaptureFormRefernceOriginalSnapshot(entry)
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

        private static async Task<(Guid DeviceId, long LastServerVersion)> LockAndValidateLocalStateForOfflineWriteAsync(
            DbConnection connection,
            DbTransaction transaction,
            string databaseId,
            CancellationToken cancellationToken)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                SELECT TOP (1) 
                    [DeviceId], 
                    [LastServerVersion], 
                    [ActiveLeaseToken], 
                    [LeaseExpiresAtUtc],
                    CASE 
                        WHEN [ActiveLeaseToken] IS NOT NULL AND [LeaseExpiresAtUtc] >= SYSUTCDATETIME() 
                        THEN 1 
                        ELSE 0 
                    END AS [IsActiveSyncLease]
                FROM [sync].[LocalState] WITH (UPDLOCK, HOLDLOCK)
                WHERE [DatabaseId] = @DatabaseId;";

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
            var isActiveSyncLease = reader.GetInt32(4) == 1;

            if (isActiveSyncLease)
            {
                throw new SyncLocalWriteBlockedActiveSyncException(
                    $"Offline write for database '{databaseId}' is blocked because an active sync operation (Push or Pull) is currently in progress under lease fence.");
            }

            if (deviceId == Guid.Empty)
            {
                throw new InvalidOperationException($"LocalState record for DatabaseId '{databaseId}' has empty DeviceId. Write operation failed-closed.");
            }

            return (deviceId, lastServerVersion);
        }

        private static async Task<DateTime?> GetMaxOutboxCreatedAtUtcAsync(
            DbConnection dbConnection,
            DbTransaction dbTransaction,
            string databaseId,
            CancellationToken cancellationToken)
        {
            using var cmd = dbConnection.CreateCommand();
            cmd.Transaction = dbTransaction;
            cmd.CommandText = @"
                SELECT MAX(CreatedAtUtc)
                FROM [sync].[LocalOutbox] WITH (UPDLOCK, HOLDLOCK)
                WHERE DatabaseId = @DatabaseId;";

            var param = cmd.CreateParameter();
            param.ParameterName = "@DatabaseId";
            param.Value = databaseId;
            cmd.Parameters.Add(param);

            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result == null || result == DBNull.Value)
            {
                return null;
            }

            var maxUtc = (DateTime)result;
            return DateTime.SpecifyKind(maxUtc, DateTimeKind.Utc);
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
            if (value is DateTime)
            {
                p.DbType = System.Data.DbType.DateTime2;
            }
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

        internal static AuthoritativeFormOriginalSnapshot CaptureFormOriginalSnapshot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var originalValues = entry.OriginalValues;
            return new AuthoritativeFormOriginalSnapshot
            {
                SyncId = (Guid)originalValues[nameof(Form.SyncId)]!,
                Name = (string?)originalValues[nameof(Form.Name)],
                DailyId = (int?)originalValues[nameof(Form.DailyId)],
                Index = (int?)originalValues[nameof(Form.Index)],
                Description = (string?)originalValues[nameof(Form.Description)],
                CreatedAt = (DateTime)originalValues[nameof(Form.CreatedAt)]!,
                CreatedBy = (string?)originalValues[nameof(Form.CreatedBy)],
                UpdatedAt = (DateTime?)originalValues[nameof(Form.UpdatedAt)],
                UpdatedBy = (string?)originalValues[nameof(Form.UpdatedBy)],
                DeactivatedAt = (DateTime?)originalValues[nameof(Form.DeactivatedAt)],
                DeactivatedBy = (string?)originalValues[nameof(Form.DeactivatedBy)],
                IsActive = (bool)originalValues[nameof(Form.IsActive)]!
            };
        }

        internal static AuthoritativeFormDetailsOriginalSnapshot CaptureFormDetailsOriginalSnapshot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var originalValues = entry.OriginalValues;
            return new AuthoritativeFormDetailsOriginalSnapshot
            {
                SyncId = (Guid)originalValues[nameof(FormDetails.SyncId)]!,
                FormId = (int)originalValues[nameof(FormDetails.FormId)]!,
                EmployeeId = (string)originalValues[nameof(FormDetails.EmployeeId)]!,
                Amount = Convert.ToDouble(originalValues[nameof(FormDetails.Amount)]!),
                OrderNum = (int)originalValues[nameof(FormDetails.OrderNum)]!,
                IsReviewed = (bool)originalValues[nameof(FormDetails.IsReviewed)]!,
                IsReviewedBy = (string?)originalValues[nameof(FormDetails.IsReviewedBy)],
                ReviewedAt = (DateTime?)originalValues[nameof(FormDetails.ReviewedAt)],
                ReviewComments = (string?)originalValues[nameof(FormDetails.ReviewComments)],
                IsSummaryReviewed = (bool)originalValues[nameof(FormDetails.IsSummaryReviewed)]!,
                IsSummaryReviewedBy = (string?)originalValues[nameof(FormDetails.IsSummaryReviewedBy)],
                SummaryReviewedAt = (DateTime?)originalValues[nameof(FormDetails.SummaryReviewedAt)],
                SummaryComments = (string?)originalValues[nameof(FormDetails.SummaryComments)],
                SummaryReviewMethod = (string?)originalValues[nameof(FormDetails.SummaryReviewMethod)],
                CreatedAt = (DateTime)originalValues[nameof(FormDetails.CreatedAt)]!,
                CreatedBy = (string?)originalValues[nameof(FormDetails.CreatedBy)],
                UpdatedAt = (DateTime?)originalValues[nameof(FormDetails.UpdatedAt)],
                UpdatedBy = (string?)originalValues[nameof(FormDetails.UpdatedBy)],
                DeactivatedAt = (DateTime?)originalValues[nameof(FormDetails.DeactivatedAt)],
                DeactivatedBy = (string?)originalValues[nameof(FormDetails.DeactivatedBy)],
                IsActive = (bool)originalValues[nameof(FormDetails.IsActive)]!
            };
        }

        internal static AuthoritativeFormRefernceOriginalSnapshot CaptureFormRefernceOriginalSnapshot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
        {
            var originalValues = entry.OriginalValues;
            return new AuthoritativeFormRefernceOriginalSnapshot
            {
                SyncId = (Guid)originalValues[nameof(FormRefernce.SyncId)]!,
                FormId = (int)originalValues[nameof(FormRefernce.FormId)]!,
                ReferencePath = (string?)originalValues[nameof(FormRefernce.ReferencePath)],
                CreatedAt = (DateTime)originalValues[nameof(FormRefernce.CreatedAt)]!,
                CreatedBy = (string?)originalValues[nameof(FormRefernce.CreatedBy)],
                UpdatedAt = (DateTime?)originalValues[nameof(FormRefernce.UpdatedAt)],
                UpdatedBy = (string?)originalValues[nameof(FormRefernce.UpdatedBy)],
                DeactivatedAt = (DateTime?)originalValues[nameof(FormRefernce.DeactivatedAt)],
                DeactivatedBy = (string?)originalValues[nameof(FormRefernce.DeactivatedBy)],
                IsActive = (bool)originalValues[nameof(FormRefernce.IsActive)]!
            };
        }

        public static string BuildDeterministicFormPayloadJson(
            string operationType,
            string databaseId,
            Guid deviceId,
            long baseServerVersion,
            Form form,
            Guid? dailySyncId,
            DateTime timestampUtc)
        {
            var scalarData = new SortedDictionary<string, object?>
            {
                ["CreatedAt"] = form.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["CreatedBy"] = form.CreatedBy,
                ["DailySyncId"] = dailySyncId?.ToString(),
                ["DeactivatedAt"] = form.DeactivatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["DeactivatedBy"] = form.DeactivatedBy,
                ["Description"] = form.Description,
                ["Id"] = form.Id,
                ["Index"] = form.Index,
                ["IsActive"] = form.IsActive,
                ["Name"] = form.Name,
                ["SyncId"] = form.SyncId.ToString(),
                ["UpdatedAt"] = form.UpdatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["UpdatedBy"] = form.UpdatedBy
            };

            var envelope = new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = baseServerVersion,
                ["createdAtUtc"] = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ["databaseId"] = databaseId,
                ["deviceId"] = deviceId.ToString(),
                ["entityData"] = scalarData,
                ["entitySyncId"] = form.SyncId.ToString(),
                ["entityType"] = "Form",
                ["operationType"] = operationType,
                ["schemaVersion"] = 1
            };

            return JsonSerializer.Serialize(envelope);
        }

        public static string BuildDeterministicFormDetailsPayloadJson(
            string operationType,
            string databaseId,
            Guid deviceId,
            long baseServerVersion,
            FormDetails formDetails,
            Guid formSyncId,
            DateTime timestampUtc)
        {
            var scalarData = new SortedDictionary<string, object?>
            {
                ["Amount"] = formDetails.Amount,
                ["CreatedAt"] = formDetails.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["CreatedBy"] = formDetails.CreatedBy,
                ["DeactivatedAt"] = formDetails.DeactivatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["DeactivatedBy"] = formDetails.DeactivatedBy,
                ["EmployeeId"] = formDetails.EmployeeId,
                ["FormSyncId"] = formSyncId.ToString(),
                ["Id"] = formDetails.Id,
                ["IsActive"] = formDetails.IsActive,
                ["IsReviewed"] = formDetails.IsReviewed,
                ["IsReviewedBy"] = formDetails.IsReviewedBy,
                ["IsSummaryReviewed"] = formDetails.IsSummaryReviewed,
                ["IsSummaryReviewedBy"] = formDetails.IsSummaryReviewedBy,
                ["OrderNum"] = formDetails.OrderNum,
                ["ReviewComments"] = formDetails.ReviewComments,
                ["ReviewedAt"] = formDetails.ReviewedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["SummaryComments"] = formDetails.SummaryComments,
                ["SummaryReviewMethod"] = formDetails.SummaryReviewMethod,
                ["SummaryReviewedAt"] = formDetails.SummaryReviewedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["SyncId"] = formDetails.SyncId.ToString(),
                ["UpdatedAt"] = formDetails.UpdatedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffffff"),
                ["UpdatedBy"] = formDetails.UpdatedBy
            };

            var envelope = new SortedDictionary<string, object?>
            {
                ["baseServerVersion"] = baseServerVersion,
                ["createdAtUtc"] = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ["databaseId"] = databaseId,
                ["deviceId"] = deviceId.ToString(),
                ["entityData"] = scalarData,
                ["entitySyncId"] = formDetails.SyncId.ToString(),
                ["entityType"] = "FormDetails",
                ["operationType"] = operationType,
                ["schemaVersion"] = 1
            };

            return JsonSerializer.Serialize(envelope);
        }

        private async Task<Guid?> ResolveDailySyncIdAsync(Form form, DbConnection connection, DbTransaction transaction, CancellationToken ct)
        {
            if (form.Daily != null && form.Daily.SyncId != Guid.Empty) return form.Daily.SyncId;

            var trackedDailyByNav = _context.ChangeTracker.Entries<Daily>().FirstOrDefault(e => form.Daily != null && e.Entity == form.Daily);
            if (trackedDailyByNav != null && trackedDailyByNav.Entity.SyncId != Guid.Empty)
            {
                return trackedDailyByNav.Entity.SyncId;
            }

            if (!form.DailyId.HasValue) return null;

            var trackedDaily = _context.ChangeTracker.Entries<Daily>().FirstOrDefault(e => e.Entity.Id == form.DailyId.Value);
            if (trackedDaily != null && trackedDaily.Entity.SyncId != Guid.Empty)
            {
                return trackedDaily.Entity.SyncId;
            }

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT SyncId FROM [dbo].[Daily] WHERE Id = @Id;";
            AddParam(cmd, "@Id", form.DailyId.Value);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar != null && scalar != DBNull.Value && scalar is Guid g && g != Guid.Empty)
            {
                return g;
            }
            throw new OfflineWriteScopeException($"Daily with Id {form.DailyId.Value} could not be resolved for Form {form.SyncId}.");
        }

        private async Task<Guid> ResolveFormSyncIdAsync(FormDetails formDetails, DbConnection connection, DbTransaction transaction, CancellationToken ct)
        {
            if (formDetails.Form != null && formDetails.Form.SyncId != Guid.Empty) return formDetails.Form.SyncId;

            var trackedForm = _context.ChangeTracker.Entries<Form>().FirstOrDefault(e => (formDetails.FormId > 0 && e.Entity.Id == formDetails.FormId) || (formDetails.Form != null && e.Entity == formDetails.Form));
            if (trackedForm != null && trackedForm.Entity.SyncId != Guid.Empty)
            {
                return trackedForm.Entity.SyncId;
            }

            if (formDetails.FormId > 0)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "SELECT SyncId FROM [dbo].[Form] WHERE Id = @Id;";
                AddParam(cmd, "@Id", formDetails.FormId);
                var scalar = await cmd.ExecuteScalarAsync(ct);
                if (scalar != null && scalar != DBNull.Value && scalar is Guid g && g != Guid.Empty)
                {
                    return g;
                }
            }

            throw new OfflineWriteScopeException($"Form with Id {formDetails.FormId} could not be resolved for FormDetails {formDetails.SyncId}.");
        }

        private static int GetOfflineMutationTopologicalRank(CapturedOfflineMutation m)
        {
            bool isDelete = m.OperationType == "SOFT_DELETE" || m.OperationType == "HARD_DELETE";
            if (isDelete)
            {
                if (SyncEntityRegistry.Instance.TryGetDescriptor(m.EntityType, out var desc) && desc != null)
                {
                    return desc.ReverseDeleteRank;
                }
                return m.EntityType switch
                {
                    "FormDetails" => 10,
                    "Form" => 20,
                    "Daily" => 30,
                    _ => 40
                };
            }
            if (SyncEntityRegistry.Instance.TryGetDescriptor(m.EntityType, out var d) && d != null)
            {
                return d.TopologicalRank;
            }
            return m.EntityType switch
            {
                "Daily" => 100,
                "Form" => 110,
                "FormDetails" => 120,
                _ => 130
            };
        }

        private async Task<int> SaveChangesInLocalOnlyCaptureAsync(
            ISyncConnectionProvider syncProvider,
            CancellationToken cancellationToken)
        {
            var registry = SyncEntityRegistry.Instance;
            var serializer = GenericOutboxPayloadSerializer.Default;

            // 1. Detect and validate all tracked entries
            var trackedEntries = _context.ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified || e.State == EntityState.Deleted)
                .ToList();

            if (!trackedEntries.Any())
            {
                return 0;
            }

            var capturedMutations = new List<CapturedOfflineMutation>();

            foreach (var entry in trackedEntries)
            {
                // Hard delete is strictly forbidden when change capture is active
                if (entry.State == EntityState.Deleted)
                {
                    throw new InvalidOperationException(
                        $"HARD_DELETE_FORBIDDEN: Hard delete is forbidden for '{entry.Metadata.ClrType.Name}' when change capture is active. Use soft-delete (IsActive = false).");
                }

                if (entry.Entity is ISyncableEntity syncableEntity)
                {
                    if (!registry.TryGetDescriptor(syncableEntity.GetType(), out var descriptor) || descriptor == null)
                    {
                        throw new InvalidOperationException(
                            $"UNREGISTERED_SYNC_ENTITY: Entity type '{syncableEntity.GetType().Name}' is not registered in SyncEntityRegistry.");
                    }

                    if (entry.State == EntityState.Added)
                    {
                        if (syncableEntity.SyncId == Guid.Empty)
                        {
                            syncableEntity.SyncId = Guid.NewGuid();
                        }

                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = syncableEntity,
                            EntityType = descriptor.EntityType,
                            AggregateType = descriptor.EntityType,
                            OperationType = "INSERT",
                            CommandName = $"{descriptor.EntityType}.Insert",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = syncableEntity.SyncId
                        });
                    }
                    else if (entry.State == EntityState.Modified)
                    {
                        var syncIdProp = entry.Property(nameof(ISyncableEntity.SyncId));
                        if (syncIdProp.IsModified && !Equals(syncIdProp.OriginalValue, syncIdProp.CurrentValue))
                        {
                            throw new InvalidOperationException(
                                $"SYNC_ID_IMMUTABLE: SyncId is immutable and cannot be modified on '{descriptor.EntityType}'.");
                        }

                        bool isSoftDelete = false;
                        var isActiveProp = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "IsActive");
                        if (isActiveProp != null && isActiveProp.OriginalValue is true && Equals(isActiveProp.CurrentValue, false))
                        {
                            isSoftDelete = true;
                        }

                        capturedMutations.Add(new CapturedOfflineMutation
                        {
                            Entity = syncableEntity,
                            EntityType = descriptor.EntityType,
                            AggregateType = descriptor.EntityType,
                            OperationType = isSoftDelete ? "SOFT_DELETE" : "UPDATE",
                            CommandName = isSoftDelete ? $"{descriptor.EntityType}.SoftDelete" : $"{descriptor.EntityType}.Update",
                            ClientOperationId = Guid.NewGuid(),
                            EntitySyncId = syncableEntity.SyncId
                        });
                    }
                }
            }

            // 2. Coordinate atomic transaction: business save + LocalOutbox inserts
            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var scope = LocalWriteScopeContext.BeginScope();
                await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                try
                {
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

                    // Query LocalState for deviceId and lastServerVersion
                    var (deviceId, lastServerVersion) = await LockAndValidateLocalStateForOfflineWriteAsync(
                        dbConnection, dbTransaction, databaseId, cancellationToken);

                    // Step 1: Save business changes without accepting changes yet
                    var saveResult = await _context.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);

                    // Step 2: Sort mutations in deterministic topological dependency order
                    var orderedMutations = capturedMutations
                        .OrderBy(GetOfflineMutationTopologicalRank)
                        .ToList();

                    var previousMaxCreatedAtUtc = await GetMaxOutboxCreatedAtUtcAsync(
                        dbConnection, dbTransaction, databaseId, cancellationToken);
                    var now = DateTime.UtcNow;
                    var minAllowed = previousMaxCreatedAtUtc.HasValue
                        ? previousMaxCreatedAtUtc.Value.Add(MonotonicQueueIncrement)
                        : DateTime.MinValue;
                    var baseTimestamp = now > minAllowed ? now : minAllowed;

                    for (int i = 0; i < orderedMutations.Count; i++)
                    {
                        var mutation = orderedMutations[i];
                        var operationTimestamp = baseTimestamp.AddTicks(i * MonotonicQueueIncrement.Ticks);
                        var descriptor = registry.GetDescriptor(mutation.EntityType);

                        // Resolve parent SyncIds for any foreign keys
                        var resolvedParents = new Dictionary<string, Guid?>(StringComparer.Ordinal);
                        foreach (var dep in descriptor.ParentDependencies)
                        {
                            var parentSyncId = await ResolveParentSyncIdAsync(
                                mutation.Entity, dep, dbConnection, dbTransaction, cancellationToken);
                            resolvedParents[dep.ParentSyncIdPropertyName] = parentSyncId;
                        }

                        var payloadJson = serializer.SerializeDeterministicEnvelope(
                            operationType: mutation.OperationType,
                            databaseId: databaseId,
                            deviceId: deviceId,
                            baseServerVersion: lastServerVersion,
                            entity: (ISyncableEntity)mutation.Entity,
                            parentSyncIds: resolvedParents,
                            timestampUtc: operationTimestamp);

                        await InsertOutboxRecordAsync(
                            dbConnection: dbConnection,
                            dbTransaction: dbTransaction,
                            clientOperationId: mutation.ClientOperationId,
                            databaseId: databaseId,
                            aggregateType: mutation.AggregateType,
                            commandName: mutation.CommandName,
                            entitySyncId: mutation.EntitySyncId,
                            payloadJson: payloadJson,
                            createdAtUtc: operationTimestamp,
                            cancellationToken: cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
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

        private async Task<Guid?> ResolveParentSyncIdAsync(
            object entity,
            SyncParentDependency dep,
            DbConnection connection,
            DbTransaction transaction,
            CancellationToken ct)
        {
            var entityType = entity.GetType();
            var fkProp = entityType.GetProperty(dep.ForeignKeyPropertyName);
            if (fkProp == null) return null;

            var fkValue = fkProp.GetValue(entity);
            if (fkValue == null) return null;

            var parentDescriptor = SyncEntityRegistry.Instance.GetDescriptor(dep.ParentEntityType);

            // 1. Check if parent entity is tracked in ChangeTracker
            foreach (var entry in _context.ChangeTracker.Entries())
            {
                if (entry.Entity.GetType() == parentDescriptor.ClrType && entry.Entity is ISyncableEntity parentSyncable)
                {
                    if (dep.UsesNaturalKey && !string.IsNullOrWhiteSpace(dep.NaturalKeyPropertyName))
                    {
                        var naturalProp = entry.Entity.GetType().GetProperty(dep.NaturalKeyPropertyName);
                        if (naturalProp != null && Equals(naturalProp.GetValue(entry.Entity), fkValue))
                        {
                            return parentSyncable.SyncId;
                        }
                    }
                    else
                    {
                        var idProp = entry.Entity.GetType().GetProperty("Id");
                        if (idProp != null && Equals(idProp.GetValue(entry.Entity), fkValue))
                        {
                            return parentSyncable.SyncId;
                        }
                    }
                }
            }

            // 2. Query parent table in database
            await using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            var keyCol = dep.UsesNaturalKey && !string.IsNullOrWhiteSpace(dep.NaturalKeyPropertyName)
                ? dep.NaturalKeyPropertyName
                : "Id";
            cmd.CommandText = $"SELECT SyncId FROM [{parentDescriptor.SchemaName}].[{parentDescriptor.TableName}] WHERE [{keyCol}] = @FkVal;";
            AddParam(cmd, "@FkVal", fkValue);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            if (scalar != null && scalar != DBNull.Value && scalar is Guid g && g != Guid.Empty)
            {
                return g;
            }

            if (dep.IsRequired)
            {
                throw new InvalidOperationException(
                    $"PARENT_SYNC_ID_UNRESOLVABLE: Required parent '{dep.ParentEntityType}' with key '{fkValue}' could not be resolved for child entity '{entity.GetType().Name}'.");
            }

            return null;
        }

        private sealed class CapturedOfflineMutation
        {
            public required object Entity { get; init; }
            public required string EntityType { get; init; }
            public required string AggregateType { get; init; }
            public required string OperationType { get; init; }
            public required string CommandName { get; init; }
            public required Guid ClientOperationId { get; init; }
            public required Guid EntitySyncId { get; init; }
            public Guid? ParentSyncId { get; set; }
        }
    }
}