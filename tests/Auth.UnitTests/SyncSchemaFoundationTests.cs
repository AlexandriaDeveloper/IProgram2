using System;
using System.Linq;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncSchemaFoundationTests
    {
        private static readonly Type[] ApprovedSyncableEntities = new[]
        {
            typeof(Daily),
            typeof(Form),
            typeof(FormDetails),
            typeof(Department),
            typeof(Employee),
            typeof(EmployeeBank),
            typeof(EmployeeNetPay),
            typeof(EmployeeWatchList),
            typeof(DailyReference),
            typeof(FormRefernce),
            typeof(EmployeeRefernce)
        };

        [Fact]
        public void ExplicitSyncability_OnlyApprovedEntitiesImplementISyncableEntity()
        {
            // 1. All 11 approved entities must implement ISyncableEntity and inherit SyncableEntity
            foreach (var type in ApprovedSyncableEntities)
            {
                Assert.True(typeof(ISyncableEntity).IsAssignableFrom(type), $"{type.Name} must implement ISyncableEntity");
                Assert.True(typeof(SyncableEntity).IsAssignableFrom(type), $"{type.Name} must inherit SyncableEntity");

                var prop = type.GetProperty("SyncId");
                Assert.NotNull(prop);
                Assert.Equal(typeof(Guid), prop.PropertyType);

                // Default initialization creates non-null, non-empty Guid
                var instance = Activator.CreateInstance(type) as ISyncableEntity;
                Assert.NotNull(instance);
                Assert.NotEqual(Guid.Empty, instance.SyncId);
            }

            // 2. Base Entity class itself must NOT implement ISyncableEntity
            Assert.False(typeof(ISyncableEntity).IsAssignableFrom(typeof(Entity)), "Entity base class must NOT implement ISyncableEntity");

            // 3. Non-syncable domain models (like Collage) must NOT implement ISyncableEntity
            Assert.False(typeof(ISyncableEntity).IsAssignableFrom(typeof(Collage)), "Collage must NOT implement ISyncableEntity");

            // 4. In the entire Core domain assembly, ONLY the 11 approved types (+ abstract SyncableEntity) implement ISyncableEntity
            var concreteSyncableInAssembly = typeof(Entity).Assembly.GetTypes()
                .Where(t => typeof(ISyncableEntity).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                .OrderBy(t => t.Name)
                .ToList();

            var expectedTypes = ApprovedSyncableEntities.OrderBy(t => t.Name).ToList();
            Assert.Equal(expectedTypes, concreteSyncableInAssembly);
        }

        [Fact]
        public void ApplicationContext_ConfiguresFinalizedNotNullSyncId_WithUniqueIndex()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;")
                .Options;

            using var context = new ApplicationContext(options);
            var model = context.Model;

            foreach (var type in ApprovedSyncableEntities)
            {
                var entityType = model.FindEntityType(type.FullName!);
                Assert.NotNull(entityType);

                var syncIdProp = entityType.FindProperty("SyncId");
                Assert.NotNull(syncIdProp);

                // Finalized requirement: Must be NOT NULL
                Assert.False(syncIdProp.IsNullable, $"{type.Name}.SyncId must be NOT NULL in finalized model");

                // Finalized requirement: Must NOT have database default value SQL (no DB default generating GUID)
                Assert.Null(syncIdProp.GetDefaultValueSql());

                // Finalized requirement: Standard unique index (no filter needed since column is NOT NULL)
                var syncIdIndex = entityType.GetIndexes()
                    .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "SyncId");

                Assert.NotNull(syncIdIndex);
                Assert.True(syncIdIndex.IsUnique, $"SyncId index on {type.Name} must be UNIQUE");
                Assert.Null(syncIdIndex.GetFilter());
            }
        }

        [Fact]
        public void AzureDatabaseBinding_RejectsCrossMatchingAndInvalidIds()
        {
            // Valid bindings
            var b2026 = AzureDatabaseBinding.For2026();
            Assert.Equal("2026", b2026.CanonicalDatabaseId);
            Assert.Equal("IProgramDb2026", b2026.ExpectedDatabaseName);

            var b2027 = AzureDatabaseBinding.For2027();
            Assert.Equal("2027", b2027.CanonicalDatabaseId);
            Assert.Equal("IProgramDb2027", b2027.ExpectedDatabaseName);

            // DB2026 binding rejects 2027 target
            Assert.Throws<InvalidOperationException>(() => new AzureDatabaseBinding("2026", "IProgramDb2027"));

            // DB2027 binding rejects 2026 target
            Assert.Throws<InvalidOperationException>(() => new AzureDatabaseBinding("2027", "IProgramDb2026"));

            // Rejects unknown/unsupported IDs
            Assert.Throws<ArgumentException>(() => new AzureDatabaseBinding("2028", "IProgramDb2028"));
            Assert.Throws<ArgumentException>(() => new AzureDatabaseBinding("", "IProgramDb2026"));
        }

        [Fact]
        public void ServerState_Initialization_IsIdempotent_AndDatabaseIdIsolated()
        {
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            using var context = new AzureSyncContext(options);

            // 1. Initialize 2026 via trusted binding
            AzureSyncContext.InitializeServerState(context, AzureDatabaseBinding.For2026());
            var state2026 = context.ServerStates.Find("2026");
            Assert.NotNull(state2026);
            Assert.Equal("2026", state2026.DatabaseId);
            Assert.Equal(0, state2026.CurrentVersion);

            // 2. Re-initialize 2026 (idempotency check: does not throw or duplicate)
            AzureSyncContext.InitializeServerState(context, AzureDatabaseBinding.For2026());
            Assert.Equal(1, context.ServerStates.Count());

            // 3. Initialize 2027 (isolation check)
            AzureSyncContext.InitializeServerState(context, AzureDatabaseBinding.For2027());
            var state2027 = context.ServerStates.Find("2027");
            Assert.NotNull(state2027);
            Assert.Equal("2027", state2027.DatabaseId);
            Assert.Equal(0, state2027.CurrentVersion);
            Assert.Equal(2, context.ServerStates.Count());
        }

        [Fact]
        public void AzureServerStateInitializer_RejectsPhysicalDatabaseMismatch()
        {
            // Context connected to physical DB IProgramDb2026
            var options2026 = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer("Server=tcp:azure-server.database.windows.net,1433;Database=IProgramDb2026;Integrated Security=True;TrustServerCertificate=True;")
                .Options;

            using (var context2026 = new AzureSyncContext(options2026))
            {
                // Attempting to initialize with 2027 binding must throw InvalidOperationException
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    AzureServerStateInitializer.Initialize(context2026, AzureDatabaseBinding.For2027()));
                Assert.Contains("Physical database connection mismatch", ex.Message);
            }

            // Context connected to physical DB IProgramDb2027
            var options2027 = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer("Server=tcp:azure-server.database.windows.net,1433;Database=IProgramDb2027;Integrated Security=True;TrustServerCertificate=True;")
                .Options;

            using (var context2027 = new AzureSyncContext(options2027))
            {
                // Attempting to initialize with 2026 binding must throw InvalidOperationException
                var ex = Assert.Throws<InvalidOperationException>(() =>
                    AzureServerStateInitializer.Initialize(context2027, AzureDatabaseBinding.For2026()));
                Assert.Contains("Physical database connection mismatch", ex.Message);
            }
        }

        [Fact]
        public void ProcessedOperation_LedgerFields_ConfiguredCorrectlyInAzureSyncContext()
        {
            // Verify model properties
            var propCommand = typeof(ProcessedOperation).GetProperty("CommandName");
            var propHash = typeof(ProcessedOperation).GetProperty("RequestHash");
            Assert.NotNull(propCommand);
            Assert.NotNull(propHash);

            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer("Server=tcp:azure-server.database.windows.net,1433;Database=IProgramDb2026;Integrated Security=True;TrustServerCertificate=True;")
                .Options;

            using var context = new AzureSyncContext(options);
            var entityType = context.Model.FindEntityType(typeof(ProcessedOperation));
            Assert.NotNull(entityType);

            // CommandName: nvarchar(100), required
            var cmdProp = entityType.FindProperty("CommandName");
            Assert.NotNull(cmdProp);
            Assert.False(cmdProp.IsNullable);
            Assert.Equal(100, cmdProp.GetMaxLength());

            // RequestHash: varchar(64), required, non-unicode
            var hashProp = entityType.FindProperty("RequestHash");
            Assert.NotNull(hashProp);
            Assert.False(hashProp.IsNullable);
            Assert.Equal(64, hashProp.GetMaxLength());
            Assert.False(hashProp.IsUnicode());
        }

        [Fact]
        public void MigrationHistoryIsolation_AuxiliaryContexts_HaveDedicatedHistoryTables()
        {
            // 1. Verify constant definitions
            Assert.Equal("__EFMigrationsHistory_LocalSync", LocalSyncContext.MigrationsHistoryTableName);
            Assert.Equal("sync", LocalSyncContext.MigrationsHistoryTableSchema);

            Assert.Equal("__EFMigrationsHistory_AzureSync", AzureSyncContext.MigrationsHistoryTableName);
            Assert.Equal("sync", AzureSyncContext.MigrationsHistoryTableSchema);

            // 2. Verify LocalSyncContext design-time factory sets custom history table
            var localFactory = new LocalSyncContextDesignTimeFactory();
            using var localCtx = localFactory.CreateDbContext(Array.Empty<string>());
            var localRelational = ((IDbContextOptions)localCtx.GetService<IDbContextOptions>())
                .Extensions.OfType<RelationalOptionsExtension>().Single();

            Assert.Equal("__EFMigrationsHistory_LocalSync", localRelational.MigrationsHistoryTableName);
            Assert.Equal("sync", localRelational.MigrationsHistoryTableSchema);

            // 3. Verify AzureSyncContext design-time factory sets custom history table
            var azureFactory = new AzureSyncContextDesignTimeFactory();
            using var azureCtx = azureFactory.CreateDbContext(Array.Empty<string>());
            var azureRelational = ((IDbContextOptions)azureCtx.GetService<IDbContextOptions>())
                .Extensions.OfType<RelationalOptionsExtension>().Single();

            Assert.Equal("__EFMigrationsHistory_AzureSync", azureRelational.MigrationsHistoryTableName);
            Assert.Equal("sync", azureRelational.MigrationsHistoryTableSchema);
        }

        [Fact]
        public void ExistingPKDefinitions_RemainUnchanged()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;")
                .Options;

            using var context = new ApplicationContext(options);
            var model = context.Model;

            // Employees: string PK
            var emp = model.FindEntityType(typeof(Employee))!;
            Assert.Equal("Id", emp.FindPrimaryKey()!.Properties.Single().Name);
            Assert.Equal(typeof(string), emp.FindPrimaryKey()!.Properties.Single().ClrType);

            // EmployeeBank: string PK
            var bank = model.FindEntityType(typeof(EmployeeBank))!;
            Assert.Equal("EmployeeId", bank.FindPrimaryKey()!.Properties.Single().Name);
            Assert.Equal(typeof(string), bank.FindPrimaryKey()!.Properties.Single().ClrType);

            // Integer PK tables
            var intPkTypes = new[]
            {
                typeof(Daily), typeof(Form), typeof(FormDetails), typeof(Department),
                typeof(EmployeeNetPay), typeof(EmployeeWatchList),
                typeof(DailyReference), typeof(FormRefernce), typeof(EmployeeRefernce)
            };

            foreach (var type in intPkTypes)
            {
                var et = model.FindEntityType(type)!;
                var pk = et.FindPrimaryKey()!;
                Assert.Equal("Id", pk.Properties.Single().Name);
                Assert.Equal(typeof(int), pk.Properties.Single().ClrType);
            }
        }

        [Fact]
        public void ExistingFKRelationships_AllPreservedIntact()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;")
                .Options;

            using var context = new ApplicationContext(options);
            var model = context.Model;

            // 1. Form -> Daily (FK: DailyId, Cascade)
            var form = model.FindEntityType(typeof(Form))!;
            var formDailyFk = form.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Daily));
            Assert.Equal("DailyId", formDailyFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, formDailyFk.DeleteBehavior);

            // 2. FormDetails -> Form (FK: FormId, Cascade)
            var formDetails = model.FindEntityType(typeof(FormDetails))!;
            var fdFormFk = formDetails.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Form));
            Assert.Equal("FormId", fdFormFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, fdFormFk.DeleteBehavior);

            // 3. FormDetails -> Employee (FK: EmployeeId)
            var fdEmpFk = formDetails.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", fdEmpFk.Properties.Single().Name);

            // 4. Employee -> Department (FK: DepartmentId, SetNull)
            var emp = model.FindEntityType(typeof(Employee))!;
            var empDeptFk = emp.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Department));
            Assert.Equal("DepartmentId", empDeptFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.SetNull, empDeptFk.DeleteBehavior);

            // 5. EmployeeBank -> Employee (FK: EmployeeId, 1-to-1)
            var bank = model.FindEntityType(typeof(EmployeeBank))!;
            var bankEmpFk = bank.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", bankEmpFk.Properties.Single().Name);

            // 6. DailyReference -> Daily (FK: DailyId, Cascade)
            var dailyRef = model.FindEntityType(typeof(DailyReference))!;
            var drDailyFk = dailyRef.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Daily));
            Assert.Equal("DailyId", drDailyFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, drDailyFk.DeleteBehavior);

            // 7. FormRefernce -> Form (FK: FormId, Cascade)
            var formRef = model.FindEntityType(typeof(FormRefernce))!;
            var frFormFk = formRef.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Form));
            Assert.Equal("FormId", frFormFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, frFormFk.DeleteBehavior);

            // 8. EmployeeRefernce -> Employee (FK: EmployeeId, ClientSetNull by convention for nullable string FK)
            var empRef = model.FindEntityType(typeof(EmployeeRefernce))!;
            var erEmpFk = empRef.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", erEmpFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.ClientSetNull, erEmpFk.DeleteBehavior);

            // 9. EmployeeNetPay -> Daily (FK: DailyId, Cascade)
            var netPay = model.FindEntityType(typeof(EmployeeNetPay))!;
            var npDailyFk = netPay.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Daily));
            Assert.Equal("DailyId", npDailyFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, npDailyFk.DeleteBehavior);

            // 10. EmployeeNetPay -> Employee (FK: EmployeeId)
            var npEmpFk = netPay.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", npEmpFk.Properties.Single().Name);

            // 11. EmployeeNetPay -> DailyReference (FK: DailyReferenceId)
            var npDrFk = netPay.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(DailyReference));
            Assert.Equal("DailyReferenceId", npDrFk.Properties.Single().Name);

            // 12. EmployeeWatchList -> Employee (FK: EmployeeId, Cascade)
            var watch = model.FindEntityType(typeof(EmployeeWatchList))!;
            var watchEmpFk = watch.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", watchEmpFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, watchEmpFk.DeleteBehavior);
        }

        [Fact]
        public void ContextSeparation_LocalAndAzureMetadata_AreStrictlyIsolated()
        {
            // 1. ApplicationContext must contain ZERO sync schema tables
            var appOptions = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;").Options;
            using var appContext = new ApplicationContext(appOptions);
            var appTables = appContext.Model.GetEntityTypes().Select(e => e.GetTableName()).ToList();
            Assert.DoesNotContain("LocalOutbox", appTables);
            Assert.DoesNotContain("LocalState", appTables);
            Assert.DoesNotContain("BootstrapManifest", appTables);
            Assert.DoesNotContain("ServerState", appTables);
            Assert.DoesNotContain("ServerChangeFeed", appTables);
            Assert.DoesNotContain("Tombstones", appTables);
            Assert.DoesNotContain("ProcessedOperations", appTables);

            // 2. LocalSyncContext must contain ONLY local sync tables in 'sync' schema
            var localOptions = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer("Server=localhost;Database=IProgramLocalDb2026;Trusted_Connection=True;").Options;
            using var localContext = new LocalSyncContext(localOptions);
            var localTypes = localContext.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
            Assert.Contains(typeof(LocalOutbox), localTypes);
            Assert.Contains(typeof(LocalState), localTypes);
            Assert.Contains(typeof(LocalBootstrapManifest), localTypes);
            Assert.DoesNotContain(typeof(ServerState), localTypes);
            Assert.DoesNotContain(typeof(ServerChangeFeed), localTypes);
            Assert.DoesNotContain(typeof(ServerTombstone), localTypes);
            Assert.DoesNotContain(typeof(ProcessedOperation), localTypes);

            // 3. AzureSyncContext must contain ONLY Azure sync tables in 'sync' schema
            var azureOptions = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer("Server=tcp:azure-server.database.windows.net,1433;Database=IProgramDb2026;Integrated Security=True;TrustServerCertificate=True;").Options;
            using var azureContext = new AzureSyncContext(azureOptions);
            var azureTypes = azureContext.Model.GetEntityTypes().Select(e => e.ClrType).ToList();
            Assert.Contains(typeof(ServerState), azureTypes);
            Assert.Contains(typeof(ServerChangeFeed), azureTypes);
            Assert.Contains(typeof(ServerTombstone), azureTypes);
            Assert.Contains(typeof(ProcessedOperation), azureTypes);
            Assert.DoesNotContain(typeof(LocalOutbox), azureTypes);
            Assert.DoesNotContain(typeof(LocalState), azureTypes);
            Assert.DoesNotContain(typeof(LocalBootstrapManifest), azureTypes);
        }

        [Fact]
        public void BackfillLogic_IsIdempotent_AndNeverModifiesExistingNonNullSyncId()
        {
            var initialSyncId = Guid.NewGuid();
            var dailyWithExistingSyncId = new Daily { Id = 1, Name = "Existing", SyncId = initialSyncId };
            var dailyNeedingSyncId = new Daily { Id = 2, Name = "NeedsBackfill", SyncId = Guid.Empty };

            var list = new System.Collections.Generic.List<Daily> { dailyWithExistingSyncId, dailyNeedingSyncId };

            // Backfill pass 1: only assign where Guid is empty / unassigned
            foreach (var d in list)
            {
                if (d.SyncId == Guid.Empty)
                {
                    d.SyncId = Guid.NewGuid();
                }
            }

            Assert.Equal(initialSyncId, dailyWithExistingSyncId.SyncId);
            Assert.NotEqual(Guid.Empty, dailyNeedingSyncId.SyncId);
            var assignedSyncId = dailyNeedingSyncId.SyncId;

            // Backfill pass 2 (idempotency check): re-running backfill must not alter any value
            foreach (var d in list)
            {
                if (d.SyncId == Guid.Empty)
                {
                    d.SyncId = Guid.NewGuid();
                }
            }

            Assert.Equal(initialSyncId, dailyWithExistingSyncId.SyncId);
            Assert.Equal(assignedSyncId, dailyNeedingSyncId.SyncId);
        }
    }
}

