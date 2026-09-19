#nullable enable
using System;
using Core.Interfaces;

namespace Core.Models
{
    public abstract class SyncableEntity : Entity, ISyncableEntity
    {
        public Guid SyncId { get; set; } = Guid.NewGuid();
    }
}
