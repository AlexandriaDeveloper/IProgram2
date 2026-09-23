using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Core.Configuration;
using Core.Exceptions;
using Core.Interfaces;
using Core.Models.Sync;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Services
{
    public class DbConnectionProvider : ISyncConnectionProvider
    {
        private const string ContextItemKey = "__CanonicalDatabaseResolution";

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;

        public DbConnectionProvider(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
        }

        public bool IsLocalFirstEnabled => _configuration.GetValue<bool>("LocalFirst:Enabled", false);
        public bool IsReadOnlyMode => _configuration.GetValue<bool>("LocalFirst:ReadOnlyMode", false);

        public string GetSelectedDatabaseId()
        {
            return ResolveDatabase().DatabaseId;
        }

        public string GetConnectionString()
        {
            return ResolveDatabase().ConnectionString;
        }

        public List<DatabaseInfo> GetAvailableDatabases()
        {
            return GetConfiguredDatabases()
                .Select(d => new DatabaseInfo { Id = d.Id, Name = d.Name })
                .ToList();
        }

        public LocalDatabaseBinding GetLocalBinding(string databaseId)
        {
            return LocalDatabaseBinding.For(databaseId);
        }

        public AzureDatabaseBinding GetRemoteBinding(string databaseId)
        {
            return AzureDatabaseBinding.For(databaseId);
        }

        public string GetRemoteConnectionString(string databaseId)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }

            var databases = GetConfiguredDatabases();
            var matched = databases.FirstOrDefault(d => d.Id.Equals(databaseId.Trim(), StringComparison.OrdinalIgnoreCase));

            if (matched == null)
            {
                throw new DatabaseConfigurationException($"No database configured for canonical DatabaseId '{databaseId}'.");
            }

            var connStr = _configuration.GetConnectionString(matched.ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new DatabaseConfigurationException(
                    $"Missing or empty remote connection string for database '{matched.Id}' with ConnectionStringName '{matched.ConnectionStringName}'.");
            }

            var binding = GetRemoteBinding(databaseId);

            // Validate physical target database from connection string
            ValidateConnectionStringPhysicalDatabase(binding.CanonicalDatabaseId, connStr, isLocal: false);

            return connStr;
        }

        public string GetManualSyncRemoteConnectionString(string databaseId)
        {
            if (string.IsNullOrWhiteSpace(databaseId))
            {
                throw new InvalidDatabaseSelectionException("Canonical database ID is required.");
            }

            var canonicalId = databaseId.Trim();
            var connStrName = $"ManualSyncRemote{canonicalId}";
            var connStr = _configuration.GetConnectionString(connStrName);

            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new InvalidOperationException("MANUAL_SYNC_REMOTE_NOT_CONFIGURED");
            }

            var builder = new SqlConnectionStringBuilder(connStr);
            var expectedRemoteDb = DatabaseBindingValidator.GetExpectedRemoteDatabaseName(canonicalId);
            if (!string.Equals(builder.InitialCatalog, expectedRemoteDb, StringComparison.OrdinalIgnoreCase))
            {
                throw new PhysicalDatabaseMismatchException(
                    $"Physical database mismatch: Manual sync remote target for '{canonicalId}' must be '{expectedRemoteDb}', but found '{builder.InitialCatalog}'.");
            }

            DatabaseBindingValidator.ValidateAzureBinding(builder.DataSource, builder.InitialCatalog);

            return connStr;
        }

        public string GetLocalConnectionString(string databaseId)
        {
            var binding = GetLocalBinding(databaseId);

            // Check if explicitly configured in LocalFirst:Databases
            var localDatabases = _configuration.GetSection("LocalFirst:Databases").Get<List<LocalDatabaseConfigItem>>();
            var localConfigItem = localDatabases?.FirstOrDefault(d => d.Id.Equals(binding.CanonicalDatabaseId, StringComparison.OrdinalIgnoreCase));

            string connStr = null;
            if (localConfigItem != null && !string.IsNullOrWhiteSpace(localConfigItem.LocalConnectionStringName))
            {
                connStr = _configuration.GetConnectionString(localConfigItem.LocalConnectionStringName);
            }

            // Fallback: check standard convention connection string name LocalConnection{Id}
            if (string.IsNullOrWhiteSpace(connStr))
            {
                connStr = _configuration.GetConnectionString($"LocalConnection{binding.CanonicalDatabaseId}");
            }

            // Check if configured under DatabaseSettings:Databases
            if (string.IsNullOrWhiteSpace(connStr))
            {
                var databases = _configuration.GetSection("DatabaseSettings:Databases").Get<List<DatabaseConfigItem>>();
                var matched = databases?.FirstOrDefault(d => d.Id.Equals(binding.CanonicalDatabaseId, StringComparison.OrdinalIgnoreCase));
                if (matched != null && !string.IsNullOrWhiteSpace(matched.ConnectionStringName))
                {
                    connStr = _configuration.GetConnectionString(matched.ConnectionStringName);
                }
            }

            // Fallback: construct standard trusted connection using configured instance
            if (string.IsNullOrWhiteSpace(connStr))
            {
                var instance = _configuration.GetValue<string>("LocalFirst:SqlServerInstance") ?? "localhost";
                connStr = $"Server={instance};Database={binding.ExpectedDatabaseName};Trusted_Connection=True;TrustServerCertificate=True";
            }

            // Validate physical target database from connection string
            ValidateConnectionStringPhysicalDatabase(binding.CanonicalDatabaseId, connStr, isLocal: true);

            return connStr;
        }

        private void ValidateConnectionStringPhysicalDatabase(string canonicalId, string connStr, bool isLocal)
        {
            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new DatabaseConfigurationException($"Missing connection string for canonical DatabaseId '{canonicalId}'.");
            }

            SqlConnectionStringBuilder builder;
            try
            {
                builder = new SqlConnectionStringBuilder(connStr);
            }
            catch (Exception ex)
            {
                // Fail-closed on parse error, NEVER skip validation. Sanitize exception to avoid leaking credentials.
                throw new DatabaseConfigurationException(
                    $"Malformed connection string for canonical DatabaseId '{canonicalId}'. Could not parse SQL connection string parameters. ({ex.GetType().Name})");
            }

            // InitialCatalog / Database MUST be present
            var physicalDbName = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(physicalDbName))
            {
                throw new DatabaseConfigurationException(
                    $"Invalid connection string for canonical DatabaseId '{canonicalId}': InitialCatalog / Database is missing or empty.");
            }

            // Local endpoint validation: DataSource / Server MUST be a trusted local instance
            if (isLocal)
            {
                var configuredInstance = _configuration.GetValue<string>("LocalFirst:SqlServerInstance");
                var dataSource = builder.DataSource;

                if (!IsLocalServerEndpoint(dataSource, configuredInstance))
                {
                    throw new PhysicalDatabaseMismatchException(
                        $"Security violation: Local target connection for canonical DatabaseId '{canonicalId}' cannot point to non-local server endpoint '{dataSource}'. Expected local endpoint.");
                }
            }

            // Validate catalog name against binding rules
            DatabaseBindingValidator.ValidateTargetDatabase(canonicalId, physicalDbName, isLocalTarget: isLocal);
        }

        private static bool IsLocalServerEndpoint(string dataSource, string configuredInstance)
        {
            if (string.IsNullOrWhiteSpace(dataSource)) return false;

            var trimmed = dataSource.Trim();
            var parts = trimmed.Split(new[] { '\\', ',' }, 2);
            var hostPart = parts[0].Trim();

            var isLocalHost = hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              hostPart.Equals(".", StringComparison.OrdinalIgnoreCase) ||
                              hostPart.Equals("(local)", StringComparison.OrdinalIgnoreCase) ||
                              hostPart.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                              hostPart.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
                              hostPart.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

            if (isLocalHost) return true;

            if (!string.IsNullOrWhiteSpace(configuredInstance))
            {
                var configParts = configuredInstance.Trim().Split(new[] { '\\', ',' }, 2);
                if (configParts.Length > 0 && hostPart.Equals(configParts[0].Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private CanonicalDatabaseResolution ResolveDatabase()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext != null && httpContext.Items.TryGetValue(ContextItemKey, out var cached) && cached is CanonicalDatabaseResolution cachedResolution)
            {
                return cachedResolution;
            }

            var databases = GetConfiguredDatabases();
            if (databases.Count == 0)
            {
                throw new DatabaseConfigurationException("No databases configured under 'DatabaseSettings:Databases'.");
            }

            string rawSelector = null;
            string selectorSource = "None";

            if (httpContext != null)
            {
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    // 1. Authenticated user: JWT claim "db" is the SOLE and EXCLUSIVE authority
                    var dbClaim = user.FindFirst("db")?.Value;
                    if (string.IsNullOrWhiteSpace(dbClaim))
                    {
                        throw new InvalidDatabaseSelectionException("Authenticated request is missing a valid 'db' claim.");
                    }

                    rawSelector = dbClaim;
                    selectorSource = "JwtClaim";
                }
                else
                {
                    // 2. Unauthenticated request: Check explicit Header presence first
                    if (httpContext.Request.Headers.ContainsKey("X-Db-Selection"))
                    {
                        var headerVal = httpContext.Request.Headers["X-Db-Selection"].ToString();
                        if (string.IsNullOrWhiteSpace(headerVal))
                        {
                            throw new InvalidDatabaseSelectionException("Explicit 'X-Db-Selection' header is empty or whitespace.");
                        }

                        rawSelector = headerVal;
                        selectorSource = "Header";
                    }
                    // 3. Unauthenticated request: Check explicit Query presence only if Header key is absent
                    else if (httpContext.Request.Query.ContainsKey("dbId"))
                    {
                        var queryVal = httpContext.Request.Query["dbId"].ToString();
                        if (string.IsNullOrWhiteSpace(queryVal))
                        {
                            throw new InvalidDatabaseSelectionException("Explicit 'dbId' query parameter is empty or whitespace.");
                        }

                        rawSelector = queryVal;
                        selectorSource = "Query";
                    }
                }
            }

            CanonicalDatabaseResolution resolution;

            if (rawSelector != null)
            {
                // Canonicalize selection with trim and case-insensitive comparison
                var candidate = rawSelector.Trim();
                var matched = databases.FirstOrDefault(d => d.Id.Equals(candidate, StringComparison.OrdinalIgnoreCase));

                if (matched == null)
                {
                    // Fail Closed: Explicit invalid database selection MUST NOT fallback to default connection
                    throw new InvalidDatabaseSelectionException($"Invalid database selection '{candidate}' from source '{selectorSource}'.");
                }

                bool routeToLocal = IsLocalFirstEnabled || IsReadOnlyMode;
                string resolvedConnStr;
                string connStrName;

                if (routeToLocal)
                {
                    resolvedConnStr = GetLocalConnectionString(matched.Id);
                    connStrName = $"LocalConnection{matched.Id}";
                }
                else
                {
                    resolvedConnStr = GetRemoteConnectionString(matched.Id);
                    connStrName = matched.ConnectionStringName;
                }

                resolution = new CanonicalDatabaseResolution
                {
                    DatabaseId = matched.Id, // Canonical configured ID
                    ConnectionString = resolvedConnStr,
                    ConnectionStringName = connStrName
                };
            }
            else
            {
                // No explicit selector provided: resolve to default configured database (first item)
                var defaultDb = databases[0];
                bool routeToLocal = IsLocalFirstEnabled || IsReadOnlyMode;
                string resolvedConnStr;
                string connStrName;

                if (routeToLocal)
                {
                    resolvedConnStr = GetLocalConnectionString(defaultDb.Id);
                    connStrName = $"LocalConnection{defaultDb.Id}";
                }
                else
                {
                    resolvedConnStr = GetRemoteConnectionString(defaultDb.Id);
                    connStrName = defaultDb.ConnectionStringName;
                }

                resolution = new CanonicalDatabaseResolution
                {
                    DatabaseId = defaultDb.Id,
                    ConnectionString = resolvedConnStr,
                    ConnectionStringName = connStrName
                };
            }

            if (httpContext != null)
            {
                httpContext.Items[ContextItemKey] = resolution;
            }

            return resolution;
        }

        public List<DatabaseConfigItem> GetConfiguredDatabases()
        {
            var list = _configuration.GetSection("DatabaseSettings:Databases").Get<List<DatabaseConfigItem>>();
            if (list == null || list.Count == 0)
            {
                throw new DatabaseConfigurationException("No databases configured under 'DatabaseSettings:Databases'.");
            }

            var validated = new List<DatabaseConfigItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.Id))
                {
                    throw new DatabaseConfigurationException("Configured database entry has an empty or whitespace Id.");
                }

                var canonicalId = item.Id.Trim();
                if (seenIds.Contains(canonicalId))
                {
                    throw new DatabaseConfigurationException($"Duplicate or ambiguous database configuration Id detected: '{canonicalId}'.");
                }

                if (string.IsNullOrWhiteSpace(item.ConnectionStringName))
                {
                    throw new DatabaseConfigurationException($"Configured database '{canonicalId}' has an empty ConnectionStringName.");
                }

                seenIds.Add(canonicalId);
                validated.Add(new DatabaseConfigItem
                {
                    Id = canonicalId,
                    Name = item.Name?.Trim() ?? canonicalId,
                    ConnectionStringName = item.ConnectionStringName.Trim()
                });
            }

            return validated;
        }

        private class CanonicalDatabaseResolution
        {
            public string DatabaseId { get; set; } = string.Empty;
            public string ConnectionString { get; set; } = string.Empty;
            public string ConnectionStringName { get; set; } = string.Empty;
        }
    }

    public class DatabaseConfigItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ConnectionStringName { get; set; } = string.Empty;
    }
}
