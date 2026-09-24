#nullable enable
using System;

namespace Core.Sync.Registry
{
    public class SyncParentDependency
    {
        public string ParentEntityType { get; }
        public string ForeignKeyPropertyName { get; }
        public string ParentSyncIdPropertyName { get; }
        public bool IsRequired { get; }
        public bool UsesNaturalKey { get; }
        public string? NaturalKeyPropertyName { get; }

        public SyncParentDependency(
            string parentEntityType,
            string foreignKeyPropertyName,
            string parentSyncIdPropertyName,
            bool isRequired = true,
            bool usesNaturalKey = false,
            string? naturalKeyPropertyName = null)
        {
            if (string.IsNullOrWhiteSpace(parentEntityType))
                throw new ArgumentException("Parent entity type cannot be null or empty.", nameof(parentEntityType));
            if (string.IsNullOrWhiteSpace(foreignKeyPropertyName))
                throw new ArgumentException("Foreign key property name cannot be null or empty.", nameof(foreignKeyPropertyName));
            if (string.IsNullOrWhiteSpace(parentSyncIdPropertyName))
                throw new ArgumentException("Parent SyncId property name cannot be null or empty.", nameof(parentSyncIdPropertyName));

            ParentEntityType = parentEntityType;
            ForeignKeyPropertyName = foreignKeyPropertyName;
            ParentSyncIdPropertyName = parentSyncIdPropertyName;
            IsRequired = isRequired;
            UsesNaturalKey = usesNaturalKey;
            NaturalKeyPropertyName = naturalKeyPropertyName;
        }
    }
}
