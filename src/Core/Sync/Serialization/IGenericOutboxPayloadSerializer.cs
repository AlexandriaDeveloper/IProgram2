#nullable enable
using System;
using System.Collections.Generic;
using Core.Interfaces;

namespace Core.Sync.Serialization
{
    public interface IGenericOutboxPayloadSerializer
    {
        string SerializeDeterministicEnvelope(
            string operationType,
            string databaseId,
            Guid deviceId,
            long baseServerVersion,
            ISyncableEntity entity,
            IReadOnlyDictionary<string, Guid?>? parentSyncIds,
            DateTime timestampUtc);
    }
}
