#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using Core.Interfaces;
using Core.Sync.Registry;

namespace Core.Sync.Serialization
{
    public class GenericOutboxPayloadSerializer : IGenericOutboxPayloadSerializer
    {
        private readonly ISyncEntityRegistry _registry;

        public static GenericOutboxPayloadSerializer Default { get; } = new GenericOutboxPayloadSerializer(SyncEntityRegistry.Instance);

        public GenericOutboxPayloadSerializer(ISyncEntityRegistry? registry = null)
        {
            _registry = registry ?? SyncEntityRegistry.Instance;
        }

        public string SerializeDeterministicEnvelope(
            string operationType,
            string databaseId,
            Guid deviceId,
            long baseServerVersion,
            ISyncableEntity entity,
            IReadOnlyDictionary<string, Guid?>? parentSyncIds,
            DateTime timestampUtc)
        {
            if (string.IsNullOrWhiteSpace(operationType))
                throw new ArgumentException("OperationType cannot be null or empty.", nameof(operationType));
            if (string.IsNullOrWhiteSpace(databaseId))
                throw new ArgumentException("DatabaseId cannot be null or empty.", nameof(databaseId));
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            var normOp = operationType.Trim().ToUpperInvariant();
            if (normOp != "INSERT" && normOp != "UPDATE" && normOp != "SOFT_DELETE")
            {
                throw new InvalidOperationException(
                    $"INVALID_SYNC_OPERATION_TYPE: OperationType '{operationType}' is forbidden. Only INSERT, UPDATE, and SOFT_DELETE are allowed. Hard deletes fail closed.");
            }

            var descriptor = _registry.GetDescriptor(entity.GetType());
            var scalarData = new SortedDictionary<string, object?>(StringComparer.Ordinal);

            // Extract all readable scalar properties
            var properties = entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in properties)
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;

                var propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                // Exclude complex entity navigation objects and collections
                if (typeof(IEnumerable).IsAssignableFrom(propType) && propType != typeof(string) && propType != typeof(byte[]))
                    continue;
                if (propType.IsClass && propType != typeof(string) && !propType.IsValueType)
                    continue;

                var val = prop.GetValue(entity);
                if (val == null)
                {
                    scalarData[prop.Name] = null;
                }
                else if (val is DateTime dt)
                {
                    scalarData[prop.Name] = dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff");
                }
                else if (val is DateTimeOffset dto)
                {
                    scalarData[prop.Name] = dto.ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");
                }
                else if (val is Guid g)
                {
                    scalarData[prop.Name] = g.ToString();
                }
                else if (val is bool b)
                {
                    scalarData[prop.Name] = b;
                }
                else if (val is byte || val is sbyte || val is short || val is ushort ||
                         val is int || val is uint || val is long || val is ulong ||
                         val is float || val is double || val is decimal)
                {
                    scalarData[prop.Name] = val;
                }
                else
                {
                    scalarData[prop.Name] = val.ToString();
                }
            }

            // Invert/inject resolved parent SyncIds into scalarData
            if (parentSyncIds != null)
            {
                foreach (var kvp in parentSyncIds)
                {
                    scalarData[kvp.Key] = kvp.Value?.ToString();
                }
            }

            // Always ensure SyncId is present in scalarData
            scalarData["SyncId"] = entity.SyncId.ToString();

            var envelope = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["baseServerVersion"] = baseServerVersion,
                ["createdAtUtc"] = timestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ"),
                ["databaseId"] = databaseId,
                ["deviceId"] = deviceId.ToString(),
                ["entityData"] = scalarData,
                ["entitySyncId"] = entity.SyncId.ToString(),
                ["entityType"] = descriptor.EntityType,
                ["operationType"] = normOp,
                ["schemaVersion"] = descriptor.PayloadSchemaVersion
            };

            return JsonSerializer.Serialize(envelope);
        }
    }
}
