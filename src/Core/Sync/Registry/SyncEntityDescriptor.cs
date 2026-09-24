#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using Core.Interfaces;

namespace Core.Sync.Registry
{
    public class SyncEntityDescriptor : ISyncEntityDescriptor
    {
        public string EntityType { get; }
        public string TableName { get; }
        public string SchemaName { get; }
        public Type ClrType { get; }
        public PropertyInfo SyncIdProperty { get; }
        public string? NaturalKeyPropertyName { get; }
        public IReadOnlyList<SyncParentDependency> ParentDependencies { get; }
        public int TopologicalRank { get; }
        public int ReverseDeleteRank { get; }
        public bool IsMetadataOnlyReference { get; }
        public bool IsConflictSensitive { get; }
        public int PayloadSchemaVersion { get; }

        public SyncEntityDescriptor(
            string entityType,
            string tableName,
            Type clrType,
            int topologicalRank,
            int reverseDeleteRank,
            bool isMetadataOnlyReference = false,
            bool isConflictSensitive = true,
            string? naturalKeyPropertyName = null,
            IReadOnlyList<SyncParentDependency>? parentDependencies = null,
            string schemaName = "dbo",
            int payloadSchemaVersion = 1)
        {
            if (string.IsNullOrWhiteSpace(entityType))
                throw new ArgumentException("EntityType cannot be null or whitespace.", nameof(entityType));
            if (string.IsNullOrWhiteSpace(tableName))
                throw new ArgumentException("TableName cannot be null or whitespace.", nameof(tableName));
            if (clrType == null)
                throw new ArgumentNullException(nameof(clrType));
            if (!typeof(ISyncableEntity).IsAssignableFrom(clrType))
                throw new ArgumentException($"ClrType '{clrType.FullName}' must implement ISyncableEntity.", nameof(clrType));

            var syncIdProp = clrType.GetProperty("SyncId", BindingFlags.Public | BindingFlags.Instance);
            if (syncIdProp == null || syncIdProp.PropertyType != typeof(Guid))
                throw new ArgumentException($"ClrType '{clrType.FullName}' must contain a public Guid property named 'SyncId'.", nameof(clrType));

            EntityType = entityType;
            TableName = tableName;
            SchemaName = schemaName;
            ClrType = clrType;
            SyncIdProperty = syncIdProp;
            TopologicalRank = topologicalRank;
            ReverseDeleteRank = reverseDeleteRank;
            IsMetadataOnlyReference = isMetadataOnlyReference;
            IsConflictSensitive = isConflictSensitive;
            NaturalKeyPropertyName = naturalKeyPropertyName;
            ParentDependencies = parentDependencies ?? Array.Empty<SyncParentDependency>();
            PayloadSchemaVersion = payloadSchemaVersion;
        }
    }
}
