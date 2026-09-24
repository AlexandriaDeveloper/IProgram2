#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Core.Models;

namespace Core.Sync.Registry
{
    public class SyncEntityRegistry : ISyncEntityRegistry
    {
        private readonly Dictionary<string, ISyncEntityDescriptor> _byEntityType;
        private readonly Dictionary<string, ISyncEntityDescriptor> _byTableName;
        private readonly Dictionary<Type, ISyncEntityDescriptor> _byClrType;
        private readonly List<ISyncEntityDescriptor> _topologicalOrder;
        private readonly List<ISyncEntityDescriptor> _reverseDeleteOrder;

        public static ISyncEntityRegistry Instance { get; } = new SyncEntityRegistry();

        public SyncEntityRegistry()
            : this(BuildDefaultDescriptors())
        {
        }

        public SyncEntityRegistry(IEnumerable<ISyncEntityDescriptor> descriptors)
        {
            if (descriptors == null) throw new ArgumentNullException(nameof(descriptors));

            _byEntityType = new Dictionary<string, ISyncEntityDescriptor>(StringComparer.OrdinalIgnoreCase);
            _byTableName = new Dictionary<string, ISyncEntityDescriptor>(StringComparer.OrdinalIgnoreCase);
            _byClrType = new Dictionary<Type, ISyncEntityDescriptor>();

            foreach (var descriptor in descriptors)
            {
                if (_byEntityType.ContainsKey(descriptor.EntityType))
                {
                    throw new InvalidOperationException(
                        $"DUPLICATE_ENTITY_REGISTRATION: EntityType '{descriptor.EntityType}' is already registered in SyncEntityRegistry.");
                }

                if (_byTableName.ContainsKey(descriptor.TableName))
                {
                    throw new InvalidOperationException(
                        $"DUPLICATE_TABLE_REGISTRATION: TableName '{descriptor.TableName}' is already registered in SyncEntityRegistry.");
                }

                if (_byClrType.ContainsKey(descriptor.ClrType))
                {
                    throw new InvalidOperationException(
                        $"DUPLICATE_CLR_TYPE_REGISTRATION: ClrType '{descriptor.ClrType.FullName}' is already registered in SyncEntityRegistry.");
                }

                _byEntityType[descriptor.EntityType] = descriptor;
                _byTableName[descriptor.TableName] = descriptor;
                _byClrType[descriptor.ClrType] = descriptor;
            }

            ValidateAcyclicGraph();

            _topologicalOrder = _byEntityType.Values
                .OrderBy(d => d.TopologicalRank)
                .ThenBy(d => d.EntityType, StringComparer.Ordinal)
                .ToList();

            _reverseDeleteOrder = _byEntityType.Values
                .OrderBy(d => d.ReverseDeleteRank)
                .ThenBy(d => d.EntityType, StringComparer.Ordinal)
                .ToList();
        }

        public ISyncEntityDescriptor GetDescriptor(string entityType)
        {
            if (TryGetDescriptor(entityType, out var descriptor) && descriptor != null)
            {
                return descriptor;
            }
            throw new KeyNotFoundException($"UNREGISTERED_SYNC_ENTITY: EntityType or TableName '{entityType}' is not registered.");
        }

        public ISyncEntityDescriptor GetDescriptor(Type clrType)
        {
            if (TryGetDescriptor(clrType, out var descriptor) && descriptor != null)
            {
                return descriptor;
            }
            throw new KeyNotFoundException($"UNREGISTERED_SYNC_CLR_TYPE: ClrType '{clrType.FullName}' is not registered in SyncEntityRegistry.");
        }

        public bool TryGetDescriptor(string entityType, out ISyncEntityDescriptor? descriptor)
        {
            descriptor = null;
            if (string.IsNullOrWhiteSpace(entityType)) return false;

            if (_byEntityType.TryGetValue(entityType, out descriptor)) return true;
            if (_byTableName.TryGetValue(entityType, out descriptor)) return true;

            return false;
        }

        public bool TryGetDescriptor(Type clrType, out ISyncEntityDescriptor? descriptor)
        {
            descriptor = null;
            if (clrType == null) return false;

            return _byClrType.TryGetValue(clrType, out descriptor);
        }

        public IReadOnlyList<ISyncEntityDescriptor> GetAllDescriptors() => _topologicalOrder;

        public IReadOnlyList<ISyncEntityDescriptor> GetDescriptorsInTopologicalOrder() => _topologicalOrder;

        public IReadOnlyList<ISyncEntityDescriptor> GetDescriptorsInReverseDeleteOrder() => _reverseDeleteOrder;

        public bool IsRegistered(string entityType) => TryGetDescriptor(entityType, out _);

        public bool IsRegistered(Type clrType) => TryGetDescriptor(clrType, out _);

        private void ValidateAcyclicGraph()
        {
            // Build adjacency list for cycle detection
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityType in _byEntityType.Keys)
            {
                if (!visited.Contains(entityType))
                {
                    if (HasCycle(entityType, visited, inStack))
                    {
                        throw new InvalidOperationException($"CYCLIC_SYNC_DEPENDENCY_DETECTED: Cycle detected involving '{entityType}'.");
                    }
                }
            }
        }

        private bool HasCycle(string current, HashSet<string> visited, HashSet<string> inStack)
        {
            visited.Add(current);
            inStack.Add(current);

            var descriptor = _byEntityType[current];
            foreach (var dep in descriptor.ParentDependencies)
            {
                var parent = dep.ParentEntityType;
                if (!visited.Contains(parent))
                {
                    if (HasCycle(parent, visited, inStack)) return true;
                }
                else if (inStack.Contains(parent))
                {
                    return true;
                }
            }

            inStack.Remove(current);
            return false;
        }

        public static IReadOnlyList<ISyncEntityDescriptor> BuildDefaultDescriptors()
        {
            return new List<ISyncEntityDescriptor>
            {
                // 1. Departments (Root master)
                new SyncEntityDescriptor(
                    entityType: "Department",
                    tableName: "Departments",
                    clrType: typeof(Department),
                    topologicalRank: 0,
                    reverseDeleteRank: 5,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true),

                // 2. Employees (Depends on Departments)
                new SyncEntityDescriptor(
                    entityType: "Employee",
                    tableName: "Employees",
                    clrType: typeof(Employee),
                    topologicalRank: 1,
                    reverseDeleteRank: 4,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true,
                    naturalKeyPropertyName: nameof(Employee.Id),
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Department", nameof(Employee.DepartmentId), "DepartmentSyncId", isRequired: false)
                    }),

                // 3. EmployeeBank (1:1 with Employee)
                new SyncEntityDescriptor(
                    entityType: "EmployeeBank",
                    tableName: "EmployeeBank",
                    clrType: typeof(EmployeeBank),
                    topologicalRank: 2,
                    reverseDeleteRank: 3,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true,
                    naturalKeyPropertyName: nameof(EmployeeBank.EmployeeId),
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Employee", nameof(EmployeeBank.EmployeeId), "EmployeeSyncId", isRequired: true, usesNaturalKey: true, naturalKeyPropertyName: nameof(Employee.Id))
                    }),

                // 4. EmployeeWatchLists (Depends on Employee)
                new SyncEntityDescriptor(
                    entityType: "EmployeeWatchList",
                    tableName: "EmployeeWatchLists",
                    clrType: typeof(EmployeeWatchList),
                    topologicalRank: 2,
                    reverseDeleteRank: 3,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: false,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Employee", nameof(EmployeeWatchList.EmployeeId), "EmployeeSyncId", isRequired: true, usesNaturalKey: true, naturalKeyPropertyName: nameof(Employee.Id))
                    }),

                // 5. EmployeeRefernce (Metadata only; Cloudinary binary out of scope)
                new SyncEntityDescriptor(
                    entityType: "EmployeeRefernce",
                    tableName: "EmployeeRefernce",
                    clrType: typeof(EmployeeRefernce),
                    topologicalRank: 2,
                    reverseDeleteRank: 3,
                    isMetadataOnlyReference: true,
                    isConflictSensitive: false,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Employee", nameof(EmployeeRefernce.EmployeeId), "EmployeeSyncId", isRequired: true, usesNaturalKey: true, naturalKeyPropertyName: nameof(Employee.Id))
                    }),

                // 6. Daily (Transaction root)
                new SyncEntityDescriptor(
                    entityType: "Daily",
                    tableName: "Daily",
                    clrType: typeof(Daily),
                    topologicalRank: 0,
                    reverseDeleteRank: 5,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true),

                // 7. DailyReference (Metadata only; depends on Daily)
                new SyncEntityDescriptor(
                    entityType: "DailyReference",
                    tableName: "DailyReference",
                    clrType: typeof(DailyReference),
                    topologicalRank: 1,
                    reverseDeleteRank: 4,
                    isMetadataOnlyReference: true,
                    isConflictSensitive: false,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Daily", nameof(DailyReference.DailyId), "DailySyncId", isRequired: true)
                    }),

                // 8. Form (Depends on Daily)
                new SyncEntityDescriptor(
                    entityType: "Form",
                    tableName: "Form",
                    clrType: typeof(Form),
                    topologicalRank: 1,
                    reverseDeleteRank: 4,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Daily", nameof(Form.DailyId), "DailySyncId", isRequired: true)
                    }),

                // 9. EmployeeNetPays (Depends on Daily and Employee, optionally DailyReference)
                new SyncEntityDescriptor(
                    entityType: "EmployeeNetPay",
                    tableName: "EmployeeNetPays",
                    clrType: typeof(EmployeeNetPay),
                    topologicalRank: 3,
                    reverseDeleteRank: 2,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Daily", nameof(EmployeeNetPay.DailyId), "DailySyncId", isRequired: true),
                        new SyncParentDependency("Employee", nameof(EmployeeNetPay.EmployeeId), "EmployeeSyncId", isRequired: true, usesNaturalKey: true, naturalKeyPropertyName: nameof(Employee.Id)),
                        new SyncParentDependency("DailyReference", nameof(EmployeeNetPay.DailyReferenceId), "DailyReferenceSyncId", isRequired: false)
                    }),

                // 10. FormDetails (Depends on Form and Employee)
                new SyncEntityDescriptor(
                    entityType: "FormDetails",
                    tableName: "FormDetails",
                    clrType: typeof(FormDetails),
                    topologicalRank: 3,
                    reverseDeleteRank: 2,
                    isMetadataOnlyReference: false,
                    isConflictSensitive: true,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Form", nameof(FormDetails.FormId), "FormSyncId", isRequired: true),
                        new SyncParentDependency("Employee", nameof(FormDetails.EmployeeId), "EmployeeSyncId", isRequired: true, usesNaturalKey: true, naturalKeyPropertyName: nameof(Employee.Id))
                    }),

                // 11. FormRefernce (Metadata only; depends on Form)
                new SyncEntityDescriptor(
                    entityType: "FormRefernce",
                    tableName: "FormRefernce",
                    clrType: typeof(FormRefernce),
                    topologicalRank: 2,
                    reverseDeleteRank: 3,
                    isMetadataOnlyReference: true,
                    isConflictSensitive: false,
                    parentDependencies: new[]
                    {
                        new SyncParentDependency("Form", nameof(FormRefernce.FormId), "FormSyncId", isRequired: true)
                    })
            };
        }
    }
}
