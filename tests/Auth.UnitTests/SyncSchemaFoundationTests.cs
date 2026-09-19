using System;
using System.Linq;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync;
using Core.Interfaces;
using Core.Models;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auth.UnitTests
{
    public class SyncSchemaFoundationTests
    {
        [Fact]
        public void AllApprovedEntities_ImplementISyncableEntity_AndExposeSyncId()
        {
            var syncableTypes = new[]
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

            foreach (var type in syncableTypes)
            {
                Assert.True(typeof(ISyncableEntity).IsAssignableFrom(type), $"{type.Name} must implement ISyncableEntity");

                var prop = type.GetProperty("SyncId");
                Assert.NotNull(prop);
                Assert.Equal(typeof(Guid), prop.PropertyType);

                // Verify non-empty Guid default initialization
                var instance = Activator.CreateInstance(type) as ISyncableEntity;
                Assert.NotNull(instance);
                Assert.NotEqual(Guid.Empty, instance.SyncId);
            }
        }

        [Fact]
        public void ApplicationContext_ConfiguresUniqueIndexes_OnSyncId_ForSyncableEntities()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;")
                .Options;

            using var context = new ApplicationContext(options);
            var model = context.Model;

            var syncableEntityNames = new[]
            {
                typeof(Daily).FullName!,
                typeof(Form).FullName!,
                typeof(FormDetails).FullName!,
                typeof(Department).FullName!,
                typeof(Employee).FullName!,
                typeof(EmployeeBank).FullName!,
                typeof(EmployeeNetPay).FullName!,
                typeof(EmployeeWatchList).FullName!,
                typeof(DailyReference).FullName!,
                typeof(FormRefernce).FullName!,
                typeof(EmployeeRefernce).FullName!
            };

            foreach (var entityTypeName in syncableEntityNames)
            {
                var entityType = model.FindEntityType(entityTypeName);
                Assert.NotNull(entityType);

                var syncIdProp = entityType.FindProperty("SyncId");
                Assert.NotNull(syncIdProp);

                // Verify unique index on SyncId
                var syncIdIndex = entityType.GetIndexes()
                    .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == "SyncId");

                Assert.NotNull(syncIdIndex);
                Assert.True(syncIdIndex.IsUnique, $"SyncId index on {entityType.ClrType.Name} must be UNIQUE");
            }
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
        public void ExistingFKRelationships_RemainIntact()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;")
                .Options;

            using var context = new ApplicationContext(options);
            var model = context.Model;

            // Form -> Daily (FK: DailyId, Cascade)
            var form = model.FindEntityType(typeof(Form))!;
            var formDailyFk = form.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Daily));
            Assert.Equal("DailyId", formDailyFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, formDailyFk.DeleteBehavior);

            // FormDetails -> Form (FK: FormId, Cascade)
            var formDetails = model.FindEntityType(typeof(FormDetails))!;
            var fdFormFk = formDetails.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Form));
            Assert.Equal("FormId", fdFormFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.Cascade, fdFormFk.DeleteBehavior);

            // Employee -> Department (FK: DepartmentId, SetNull)
            var emp = model.FindEntityType(typeof(Employee))!;
            var empDeptFk = emp.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Department));
            Assert.Equal("DepartmentId", empDeptFk.Properties.Single().Name);
            Assert.Equal(DeleteBehavior.SetNull, empDeptFk.DeleteBehavior);

            // EmployeeBank -> Employee (FK: EmployeeId, 1-to-1)
            var bank = model.FindEntityType(typeof(EmployeeBank))!;
            var bankEmpFk = bank.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(Employee));
            Assert.Equal("EmployeeId", bankEmpFk.Properties.Single().Name);
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
            Assert.DoesNotContain("ServerState", appTables);
            Assert.DoesNotContain("ServerChangeFeed", appTables);
            Assert.DoesNotContain("Tombstones", appTables);
            Assert.DoesNotContain("ProcessedOperations", appTables);

            // 2. LocalSyncContext must contain ONLY local sync tables in 'sync' schema
            var localOptions = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;").Options;
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
                .UseSqlServer("Server=localhost;Database=Dummy;Trusted_Connection=True;").Options;
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
    }
}
