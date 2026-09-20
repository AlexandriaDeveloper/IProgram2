using System.Collections.Generic;
using Core.Models.Sync;

namespace Core.Interfaces
{
    public interface ISyncConnectionProvider : IDbConnectionProvider
    {
        bool IsLocalFirstEnabled { get; }
        bool IsReadOnlyMode { get; }
        string GetLocalConnectionString(string databaseId);
        string GetRemoteConnectionString(string databaseId);
        LocalDatabaseBinding GetLocalBinding(string databaseId);
        AzureDatabaseBinding GetRemoteBinding(string databaseId);
    }
}
