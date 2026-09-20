#nullable enable
using System;

namespace Core.Interfaces
{
    /// <summary>
    /// Guard responsible for validating that the current DbConnection targets an approved
    /// authoritative Azure database endpoint before applying authoritative mutations.
    /// </summary>
    public interface IAuthoritativeDatabaseBindingGuard
    {
        void ValidateAuthoritativeAzureBinding(string canonicalDatabaseId, string? serverOrDataSource, string? physicalDbName);
    }
}
