#nullable enable
using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Auth.Api.Testing
{
    /// <summary>
    /// Test-only remote database connection factory active strictly in the "Testing" hosting environment.
    /// Never active in Development or Production. Connects directly to the test databases configured in ConnectionStrings.
    /// Fails closed with zero fallback to production connection strings (DefaultConnection, CON2027).
    /// </summary>
    public sealed class IsolatedTestRemoteDatabaseConnectionFactory : IRemoteDatabaseConnectionFactory
    {
        private readonly IConfiguration _configuration;

        public IsolatedTestRemoteDatabaseConnectionFactory(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<DbConnection> CreateOpenConnectionAsync(string databaseId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }

            var canonicalId = databaseId.Trim();
            var connName = canonicalId switch
            {
                "2026" => "TestRemoteConnection2026",
                "2027" => "TestRemoteConnection2027",
                _ => throw new InvalidDatabaseSelectionException($"Unsupported canonical DatabaseId '{databaseId}'.")
            };

            var connStr = _configuration.GetConnectionString(connName);

            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new InvalidOperationException(
                    $"Missing isolated test connection string '{connName}' for canonical database '{canonicalId}'. Fallback to production databases is strictly prohibited.");
            }

            var connection = new SqlConnection(connStr);
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
    }
}
