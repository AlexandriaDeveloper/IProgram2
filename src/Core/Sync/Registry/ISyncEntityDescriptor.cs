#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Core.Sync.Registry
{
    public interface ISyncEntityDescriptor
    {
        string EntityType { get; }
        string TableName { get; }
        string SchemaName { get; }
        Type ClrType { get; }
        PropertyInfo SyncIdProperty { get; }
        string? NaturalKeyPropertyName { get; }
        IReadOnlyList<SyncParentDependency> ParentDependencies { get; }
        int TopologicalRank { get; }
        int ReverseDeleteRank { get; }
        bool IsMetadataOnlyReference { get; }
        bool IsConflictSensitive { get; }
        int PayloadSchemaVersion { get; }
    }
}
