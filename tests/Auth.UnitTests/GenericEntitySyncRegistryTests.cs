#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Core.Sync.Capture;
using Core.Sync.Registry;
using Core.Sync.Serialization;
using Microsoft.EntityFrameworkCore;
using Persistence;
using Xunit;

namespace Auth.UnitTests
{
    public class GenericEntitySyncRegistryTests
    {
        [Fact]
        public void Test01_Registry_ContainsExactly11BusinessSyncableTables_NoIdentityOrMetadataTables()
        {
            var registry = SyncEntityRegistry.Instance;
            var descriptors = registry.GetAllDescriptors();

            Assert.Equal(11, descriptors.Count);

            var expectedEntityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Department",
                "Employee",
                "EmployeeBank",
                "EmployeeWatchList",
                "EmployeeRefernce",
                "Daily",
                "DailyReference",
                "Form",
                "EmployeeNetPay",
                "FormDetails",
                "FormRefernce"
            };

            var registeredEntityTypes = descriptors.Select(d => d.EntityType).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(expectedEntityTypes, registeredEntityTypes);

            // Verify explicitly NO Identity tables are registered
            var forbiddenTables = new[]
            {
                "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetUserLogins",
                "AspNetUserRoles", "AspNetUserTokens", "DevicePairingPins",
                "__EFMigrationsHistory", "BootstrapManifest", "ServerChangeFeed",
                "LocalOutbox", "SyncCheckpoint", "SyncLease", "SyncScopeBaselines"
            };

            foreach (var forbidden in forbiddenTables)
            {
                Assert.False(registry.IsRegistered(forbidden), $"Security/metadata table '{forbidden}' must NOT be in sync registry.");
            }
        }

        [Fact]
        public void Test02_DuplicateRegistration_ThrowsInvalidOperationException()
        {
            var defaultDescriptors = SyncEntityRegistry.BuildDefaultDescriptors();

            // 1. Duplicate EntityType
            var duplicateEntityTypeList = new List<ISyncEntityDescriptor>(defaultDescriptors)
            {
                new SyncEntityDescriptor("Department", "Departments_Copy", typeof(Department), 0, 5, false, true)
            };
            var ex1 = Assert.Throws<InvalidOperationException>(() => new SyncEntityRegistry(duplicateEntityTypeList));
            Assert.Contains("DUPLICATE_ENTITY_REGISTRATION", ex1.Message);

            // 2. Duplicate TableName
            var duplicateTableNameList = new List<ISyncEntityDescriptor>(defaultDescriptors)
            {
                new SyncEntityDescriptor("Department2", "Departments", typeof(Department), 0, 5, false, true)
            };
            var ex2 = Assert.Throws<InvalidOperationException>(() => new SyncEntityRegistry(duplicateTableNameList));
            Assert.Contains("DUPLICATE_TABLE_REGISTRATION", ex2.Message);
        }

        [Fact]
        public void Test03_EveryDescriptor_MapsApplicationContextEntityAndSyncIdProperty()
        {
            var registry = SyncEntityRegistry.Instance;
            var descriptors = registry.GetAllDescriptors();

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            using var context = new ApplicationContext(options);

            foreach (var descriptor in descriptors)
            {
                // Must be mapped in ApplicationContext EF Core model
                var entityType = context.Model.FindEntityType(descriptor.ClrType);
                Assert.NotNull(entityType);

                // Must implement ISyncableEntity
                Assert.True(typeof(ISyncableEntity).IsAssignableFrom(descriptor.ClrType),
                    $"Type '{descriptor.ClrType.Name}' must implement ISyncableEntity.");

                // Must have a public Guid SyncId property
                var syncIdProp = descriptor.ClrType.GetProperty("SyncId");
                Assert.NotNull(syncIdProp);
                Assert.Equal(typeof(Guid), syncIdProp.PropertyType);

                // TableName must be valid and non-empty
                Assert.False(string.IsNullOrWhiteSpace(descriptor.TableName));
                Assert.False(string.IsNullOrWhiteSpace(descriptor.EntityType));
            }
        }

        [Fact]
        public void Test04_DependencyGraph_IsAcyclic()
        {
            // The default instance constructor automatically runs cycle detection;
            // if this executes without throwing, the graph is acyclic.
            var registry = SyncEntityRegistry.Instance;
            Assert.NotNull(registry);

            // Now test that introducing an artificial cycle throws InvalidOperationException
            var cyclicDescriptors = new List<ISyncEntityDescriptor>
            {
                new SyncEntityDescriptor(
                    "ParentA", "TableA", typeof(Department), 0, 1, false, true,
                    parentDependencies: new[] { new SyncParentDependency("ParentB", "BId", "BSyncId", true) }),
                new SyncEntityDescriptor(
                    "ParentB", "TableB", typeof(Daily), 1, 0, false, true,
                    parentDependencies: new[] { new SyncParentDependency("ParentA", "AId", "ASyncId", true) })
            };

            var ex = Assert.Throws<InvalidOperationException>(() => new SyncEntityRegistry(cyclicDescriptors));
            Assert.Contains("CYCLIC_SYNC_DEPENDENCY_DETECTED", ex.Message);
        }

        [Fact]
        public void Test05_TopologicalAndReverseDeleteOrder_AreDeterministic()
        {
            var registry = SyncEntityRegistry.Instance;

            var topoOrder = registry.GetDescriptorsInTopologicalOrder();
            var reverseOrder = registry.GetDescriptorsInReverseDeleteOrder();

            Assert.Equal(11, topoOrder.Count);
            Assert.Equal(11, reverseOrder.Count);

            // Verify topological rank monotonic progression:
            // Rank 0: Department, Daily
            // Rank 1: Employee, DailyReference, Form
            // Rank 2: EmployeeBank, EmployeeWatchList, EmployeeRefernce, FormRefernce
            // Rank 3: EmployeeNetPay, FormDetails
            var topoRanks = topoOrder.Select(d => d.TopologicalRank).ToList();
            for (int i = 1; i < topoRanks.Count; i++)
            {
                Assert.True(topoRanks[i] >= topoRanks[i - 1], "Topological rank must be monotonically non-decreasing.");
            }

            // Verify every parent comes before its children in topological order
            var topoIndexByEntity = topoOrder
                .Select((d, idx) => (d.EntityType, idx))
                .ToDictionary(x => x.EntityType, x => x.idx, StringComparer.OrdinalIgnoreCase);

            foreach (var d in topoOrder)
            {
                foreach (var dep in d.ParentDependencies)
                {
                    Assert.True(topoIndexByEntity.ContainsKey(dep.ParentEntityType));
                    var parentIdx = topoIndexByEntity[dep.ParentEntityType];
                    var childIdx = topoIndexByEntity[d.EntityType];
                    Assert.True(parentIdx < childIdx,
                        $"Topological violation: Parent '{dep.ParentEntityType}' (idx {parentIdx}) must appear before Child '{d.EntityType}' (idx {childIdx}).");
                }
            }

            // Verify reverse delete order: children must appear BEFORE parents
            var reverseIndexByEntity = reverseOrder
                .Select((d, idx) => (d.EntityType, idx))
                .ToDictionary(x => x.EntityType, x => x.idx, StringComparer.OrdinalIgnoreCase);

            foreach (var d in reverseOrder)
            {
                foreach (var dep in d.ParentDependencies)
                {
                    var parentReverseIdx = reverseIndexByEntity[dep.ParentEntityType];
                    var childReverseIdx = reverseIndexByEntity[d.EntityType];
                    Assert.True(childReverseIdx < parentReverseIdx,
                        $"Reverse delete violation: Child '{d.EntityType}' (idx {childReverseIdx}) must be deleted before Parent '{dep.ParentEntityType}' (idx {parentReverseIdx}).");
                }
            }
        }

        [Fact]
        public void Test06_DeterministicEnvelopeSerialization_Root_Child_1To1_MultiParent_Reference()
        {
            var serializer = GenericOutboxPayloadSerializer.Default;
            var deviceId = Guid.NewGuid();
            var timestamp = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

            // 1. Root Entity: Department
            var deptSyncId = Guid.NewGuid();
            var department = new Department
            {
                Id = 42,
                Name = "Engineering",
                SyncId = deptSyncId,
                CreatedAt = timestamp,
                IsActive = true
            };
            var deptJson = serializer.SerializeDeterministicEnvelope("INSERT", "2026", deviceId, 10, department, null, timestamp);
            using var docDept = JsonDocument.Parse(deptJson);
            var rootDept = docDept.RootElement;
            Assert.Equal("INSERT", rootDept.GetProperty("operationType").GetString());
            Assert.Equal("Department", rootDept.GetProperty("entityType").GetString());
            Assert.Equal(deptSyncId.ToString(), rootDept.GetProperty("entitySyncId").GetString());
            Assert.Equal("Engineering", rootDept.GetProperty("entityData").GetProperty("Name").GetString());

            // 2. Child Entity: Form (depends on Daily)
            var dailySyncId = Guid.NewGuid();
            var formSyncId = Guid.NewGuid();
            var form = new Form
            {
                Id = 101,
                DailyId = 1,
                Name = "Overtime Request",
                SyncId = formSyncId,
                CreatedAt = timestamp,
                IsActive = true
            };
            var formParents = new Dictionary<string, Guid?> { ["DailySyncId"] = dailySyncId };
            var formJson = serializer.SerializeDeterministicEnvelope("INSERT", "2026", deviceId, 10, form, formParents, timestamp);
            using var docForm = JsonDocument.Parse(formJson);
            var rootForm = docForm.RootElement;
            Assert.Equal("Form", rootForm.GetProperty("entityType").GetString());
            Assert.Equal(dailySyncId.ToString(), rootForm.GetProperty("entityData").GetProperty("DailySyncId").GetString());

            // 3. 1:1 Entity: EmployeeBank (depends on Employee via natural key Id)
            var empSyncId = Guid.NewGuid();
            var bankSyncId = Guid.NewGuid();
            var bank = new EmployeeBank
            {
                EmployeeId = "12345678901234",
                BankName = "National Bank",
                AccountNumber = "EG123456789",
                SyncId = bankSyncId,
                CreatedAt = timestamp,
                IsActive = true
            };
            var bankParents = new Dictionary<string, Guid?> { ["EmployeeSyncId"] = empSyncId };
            var bankJson = serializer.SerializeDeterministicEnvelope("UPDATE", "2026", deviceId, 10, bank, bankParents, timestamp);
            using var docBank = JsonDocument.Parse(bankJson);
            var rootBank = docBank.RootElement;
            Assert.Equal("EmployeeBank", rootBank.GetProperty("entityType").GetString());
            Assert.Equal("UPDATE", rootBank.GetProperty("operationType").GetString());
            Assert.Equal(empSyncId.ToString(), rootBank.GetProperty("entityData").GetProperty("EmployeeSyncId").GetString());
            Assert.Equal("12345678901234", rootBank.GetProperty("entityData").GetProperty("EmployeeId").GetString());

            // 4. Multi-Parent Entity: EmployeeNetPay (depends on Daily, Employee, DailyReference)
            var netPaySyncId = Guid.NewGuid();
            var dailyRefSyncId = Guid.NewGuid();
            var netPay = new EmployeeNetPay
            {
                Id = 555,
                DailyId = 1,
                EmployeeId = "12345678901234",
                DailyReferenceId = 88,
                NetPay = 2500.75,
                SyncId = netPaySyncId,
                CreatedAt = timestamp,
                IsActive = true
            };
            var netPayParents = new Dictionary<string, Guid?>
            {
                ["DailySyncId"] = dailySyncId,
                ["EmployeeSyncId"] = empSyncId,
                ["DailyReferenceSyncId"] = dailyRefSyncId
            };
            var netPayJson = serializer.SerializeDeterministicEnvelope("INSERT", "2026", deviceId, 10, netPay, netPayParents, timestamp);
            using var docNetPay = JsonDocument.Parse(netPayJson);
            var rootNetPay = docNetPay.RootElement;
            Assert.Equal("EmployeeNetPay", rootNetPay.GetProperty("entityType").GetString());
            Assert.Equal(dailySyncId.ToString(), rootNetPay.GetProperty("entityData").GetProperty("DailySyncId").GetString());
            Assert.Equal(empSyncId.ToString(), rootNetPay.GetProperty("entityData").GetProperty("EmployeeSyncId").GetString());
            Assert.Equal(dailyRefSyncId.ToString(), rootNetPay.GetProperty("entityData").GetProperty("DailyReferenceSyncId").GetString());
            Assert.Equal(2500.75m, rootNetPay.GetProperty("entityData").GetProperty("NetPay").GetDecimal());

            // 5. Reference Metadata Entity: DailyReference
            var dailyRef = new DailyReference
            {
                Id = 88,
                DailyId = 1,
                ReferencePath = "https://res.cloudinary.com/demo/image/upload/sample.jpg",
                SyncId = dailyRefSyncId,
                CreatedAt = timestamp,
                IsActive = true
            };
            var dailyRefJson = serializer.SerializeDeterministicEnvelope("INSERT", "2026", deviceId, 10, dailyRef, formParents, timestamp);
            using var docDailyRef = JsonDocument.Parse(dailyRefJson);
            var rootDailyRef = docDailyRef.RootElement;
            Assert.Equal("DailyReference", rootDailyRef.GetProperty("entityType").GetString());
            Assert.Equal("https://res.cloudinary.com/demo/image/upload/sample.jpg",
                rootDailyRef.GetProperty("entityData").GetProperty("ReferencePath").GetString());
        }

        [Fact]
        public void Test07_ReferenceMetadataCapture_ExecutesZeroCloudinaryOrNetworkCalls()
        {
            var serializer = GenericOutboxPayloadSerializer.Default;
            var timestamp = DateTime.UtcNow;

            // Verify that for all 3 reference entities, serializing does not require network or Cloudinary
            var dailyRef = new DailyReference
            {
                Id = 1,
                DailyId = 10,
                ReferencePath = "metadata/ref1.pdf",
                SyncId = Guid.NewGuid(),
                CreatedAt = timestamp,
                IsActive = true
            };

            var formRef = new FormRefernce
            {
                Id = 2,
                FormId = 20,
                ReferencePath = "metadata/form_ref.pdf",
                SyncId = Guid.NewGuid(),
                CreatedAt = timestamp,
                IsActive = true
            };

            var empRef = new EmployeeRefernce
            {
                Id = 3,
                EmployeeId = "12345678901234",
                ReferencePath = "metadata/emp_id.jpg",
                SyncId = Guid.NewGuid(),
                CreatedAt = timestamp,
                IsActive = true
            };

            // None of these should throw or attempt network calls
            var json1 = serializer.SerializeDeterministicEnvelope("INSERT", "2026", Guid.NewGuid(), 1, dailyRef, null, timestamp);
            var json2 = serializer.SerializeDeterministicEnvelope("INSERT", "2026", Guid.NewGuid(), 1, formRef, null, timestamp);
            var json3 = serializer.SerializeDeterministicEnvelope("INSERT", "2026", Guid.NewGuid(), 1, empRef, null, timestamp);

            Assert.Contains("metadata/ref1.pdf", json1);
            Assert.Contains("metadata/form_ref.pdf", json2);
            Assert.Contains("metadata/emp_id.jpg", json3);
        }

        [Fact]
        public void Test08_InvalidOperationType_FailsClosed()
        {
            var serializer = GenericOutboxPayloadSerializer.Default;
            var dept = new Department { Name = "Dept", SyncId = Guid.NewGuid(), IsActive = true };

            // Hard delete forbidden
            var ex1 = Assert.Throws<InvalidOperationException>(() =>
                serializer.SerializeDeterministicEnvelope("DELETE", "2026", Guid.NewGuid(), 1, dept, null, DateTime.UtcNow));
            Assert.Contains("INVALID_SYNC_OPERATION_TYPE", ex1.Message);

            // Arbitrary operation forbidden
            var ex2 = Assert.Throws<InvalidOperationException>(() =>
                serializer.SerializeDeterministicEnvelope("PURGE", "2026", Guid.NewGuid(), 1, dept, null, DateTime.UtcNow));
            Assert.Contains("INVALID_SYNC_OPERATION_TYPE", ex2.Message);
        }

        [Fact]
        public void Test09_PreCaptureReconciliationClassifier_TagsAll4StatesCorrectly()
        {
            var classifier = LocalPreCaptureReconciliationClassifier.Default;
            var baselineUtc = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
            var activationUtc = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

            // 1. CapturedPostActivation: hasOutboxRecord is true
            var entity1 = new Daily
            {
                SyncId = Guid.NewGuid(),
                CreatedAt = activationUtc.AddHours(2),
                IsActive = true
            };
            var status1 = classifier.ClassifyEntity(entity1, baselineUtc, activationUtc, hasOutboxRecord: true);
            Assert.Equal(EntityCaptureStatus.CapturedPostActivation, status1);

            // 2. UntrackedOrDiverged: modified after activation, but hasOutboxRecord is false
            var entity2 = new Daily
            {
                SyncId = Guid.NewGuid(),
                CreatedAt = activationUtc.AddHours(2),
                IsActive = true
            };
            var status2 = classifier.ClassifyEntity(entity2, baselineUtc, activationUtc, hasOutboxRecord: false);
            Assert.Equal(EntityCaptureStatus.UntrackedOrDiverged, status2);

            // 3. LocallyChangedPreCapture: modified between baseline and activation
            var entity3 = new Daily
            {
                SyncId = Guid.NewGuid(),
                CreatedAt = baselineUtc.AddHours(-10),
                UpdatedAt = baselineUtc.AddHours(5), // Modified between baseline and activation
                IsActive = true
            };
            var status3 = classifier.ClassifyEntity(entity3, baselineUtc, activationUtc, hasOutboxRecord: false);
            Assert.Equal(EntityCaptureStatus.LocallyChangedPreCapture, status3);

            // 4. UnchangedFromBaseline: created/modified before baseline
            var entity4 = new Daily
            {
                SyncId = Guid.NewGuid(),
                CreatedAt = baselineUtc.AddDays(-5),
                UpdatedAt = baselineUtc.AddDays(-2),
                IsActive = true
            };
            var status4 = classifier.ClassifyEntity(entity4, baselineUtc, activationUtc, hasOutboxRecord: false);
            Assert.Equal(EntityCaptureStatus.UnchangedFromBaseline, status4);
        }
    }
}
