#nullable enable
using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Sync.Push
{
    public class AzureRemoteDatabaseConnectionFactory : IRemoteDatabaseConnectionFactory
    {
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IConfiguration? _configuration;

        public AzureRemoteDatabaseConnectionFactory(
            ISyncConnectionProvider syncConnectionProvider,
            IConfiguration? configuration = null)
        {
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _configuration = configuration;
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
            var expectedRemoteDb = DatabaseBindingValidator.GetExpectedRemoteDatabaseName(databaseId);
            if (!string.Equals(builder.InitialCatalog, expectedRemoteDb, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Physical database mismatch: Remote Azure target for '{databaseId}' must be '{expectedRemoteDb}', but found '{builder.InitialCatalog}'.");
            }

            var allowIsolatedLocalRemote = _configuration != null &&
                _configuration.GetValue<bool>("Sync:AllowIsolatedLocalRemoteForTesting", false);

            if (!allowIsolatedLocalRemote)
            {
                DatabaseBindingValidator.ValidateAzureBinding(builder.DataSource, builder.InitialCatalog);
            }

            var connection = new SqlConnection(remoteConnStr);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
