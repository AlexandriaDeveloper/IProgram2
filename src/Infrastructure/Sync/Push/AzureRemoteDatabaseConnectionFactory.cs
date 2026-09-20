#nullable enable
using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;

namespace Auth.Infrastructure.Sync.Push
{
    public class AzureRemoteDatabaseConnectionFactory : IRemoteDatabaseConnectionFactory
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;

        public AzureRemoteDatabaseConnectionFactory(ISyncConnectionProvider syncConnectionProvider)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
        }

        public async Task<DbConnection> CreateOpenConnectionAsync(string databaseId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }

            var remoteConnStr = _syncConnectionProvider.GetRemoteConnectionString(databaseId);
            var builder = new SqlConnectionStringBuilder(remoteConnStr);

            // Strict production binding validation: physical target must be remote Azure (IProgramDb2026 / IProgramDb2027)
            DatabaseBindingValidator.ValidateAzureBinding(builder.DataSource, builder.InitialCatalog);

            var connection = new SqlConnection(remoteConnStr);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
