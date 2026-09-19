#nullable enable
using System;

namespace Core.Interfaces
{
    public interface ISyncableEntity
    {
        Guid SyncId { get; set; }
    }
}

