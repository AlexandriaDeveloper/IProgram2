#nullable enable
using System;
using System.Collections.Generic;

namespace Core.Sync.Registry
{
    public interface ISyncEntityRegistry
    {
        ISyncEntityDescriptor GetDescriptor(string entityType);
        ISyncEntityDescriptor GetDescriptor(Type clrType);
        bool TryGetDescriptor(string entityType, out ISyncEntityDescriptor? descriptor);
        bool TryGetDescriptor(Type clrType, out ISyncEntityDescriptor? descriptor);
        IReadOnlyList<ISyncEntityDescriptor> GetAllDescriptors();
        IReadOnlyList<ISyncEntityDescriptor> GetDescriptorsInTopologicalOrder();
        IReadOnlyList<ISyncEntityDescriptor> GetDescriptorsInReverseDeleteOrder();
        bool IsRegistered(string entityType);
        bool IsRegistered(Type clrType);
    }
}
