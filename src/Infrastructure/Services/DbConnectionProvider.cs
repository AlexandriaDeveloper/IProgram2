using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Services
{
    public class DbConnectionProvider : IDbConnectionProvider
    {
        private const string ContextItemKey = "__CanonicalDatabaseResolution";

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;

        public DbConnectionProvider(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
        }

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
                // 1. Authenticated user JWT claim "db" (Highest Authority)
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    var dbClaim = user.FindFirst("db")?.Value;
                    if (!string.IsNullOrWhiteSpace(dbClaim))
                    {
                        rawSelector = dbClaim;
                        selectorSource = "JwtClaim";
                    }
                }

                // 2. Custom header (for login / unauthenticated calls)
                if (rawSelector == null && httpContext.Request.Headers.TryGetValue("X-Db-Selection", out var dbHeader))
                {
                    var headerVal = dbHeader.ToString();
                    if (!string.IsNullOrWhiteSpace(headerVal))
                    {
                        rawSelector = headerVal;
                        selectorSource = "Header";
                    }
                }

                // 3. Query string (e.g. ?dbId=2027)
                if (rawSelector == null && httpContext.Request.Query.TryGetValue("dbId", out var dbQuery))
                {
                    var queryVal = dbQuery.ToString();
                    if (!string.IsNullOrWhiteSpace(queryVal))
                    {
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

                var connStr = _configuration.GetConnectionString(matched.ConnectionStringName);
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    // Fail Closed: Missing connection string for a configured database is a configuration failure
                    throw new DatabaseConfigurationException($"Missing or empty connection string for database '{matched.Id}' with ConnectionStringName '{matched.ConnectionStringName}'.");
                }

                resolution = new CanonicalDatabaseResolution
                {
                    DatabaseId = matched.Id, // Canonical configured ID
                    ConnectionString = connStr,
                    ConnectionStringName = matched.ConnectionStringName
                };
            }
            else
            {
                // No explicit selector provided: resolve to default configured database (first item)
                var defaultDb = databases[0];
                var connStr = _configuration.GetConnectionString(defaultDb.ConnectionStringName);
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    throw new DatabaseConfigurationException($"Missing or empty connection string for default database '{defaultDb.Id}' with ConnectionStringName '{defaultDb.ConnectionStringName}'.");
                }

                resolution = new CanonicalDatabaseResolution
                {
                    DatabaseId = defaultDb.Id,
                    ConnectionString = connStr,
                    ConnectionStringName = defaultDb.ConnectionStringName
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
