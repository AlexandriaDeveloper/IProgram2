using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Auth.Infrastructure.Services
{
    public class DbConnectionProvider : IDbConnectionProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IConfiguration _configuration;

        public DbConnectionProvider(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
        {
            _httpContextAccessor = httpContextAccessor;
            _configuration = configuration;
        }

        public string GetSelectedDatabaseId()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext != null)
            {
                // 1. Authenticated user JWT claim
                var user = httpContext.User;
                if (user?.Identity?.IsAuthenticated == true)
                {
                    var dbClaim = user.FindFirst("db")?.Value;
                    if (!string.IsNullOrEmpty(dbClaim))
                    {
                        return dbClaim;
                    }
                }

                // 2. Custom header (for login / anonymous calls)
                if (httpContext.Request.Headers.TryGetValue("X-Db-Selection", out var dbHeader))
                {
                    var dbId = dbHeader.ToString();
                    if (!string.IsNullOrEmpty(dbId))
                    {
                        return dbId;
                    }
                }
            }

            // Fallback to default
            var databases = GetConfiguredDatabases();
            return databases.FirstOrDefault()?.Id ?? "old";
        }

        public string GetConnectionString()
        {
            var dbId = GetSelectedDatabaseId();
            var databases = GetConfiguredDatabases();
            var matchedDb = databases.FirstOrDefault(d => d.Id.Equals(dbId, StringComparison.OrdinalIgnoreCase));

            if (matchedDb != null)
            {
                var connStr = _configuration.GetConnectionString(matchedDb.ConnectionStringName);
                if (!string.IsNullOrEmpty(connStr))
                {
                    return connStr;
                }
            }

            return _configuration.GetConnectionString("DefaultConnection");
        }

        public List<DatabaseInfo> GetAvailableDatabases()
        {
            return GetConfiguredDatabases()
                .Select(d => new DatabaseInfo { Id = d.Id, Name = d.Name })
                .ToList();
        }

        private List<DatabaseConfigItem> GetConfiguredDatabases()
        {
            var list = _configuration.GetSection("DatabaseSettings:Databases").Get<List<DatabaseConfigItem>>();
            if (list == null || list.Count == 0)
            {
                // Fallback to defaults if not configured
                list = new List<DatabaseConfigItem>
                {
                    new DatabaseConfigItem { Id = "old", Name = "بيانات السنة السابقة", ConnectionStringName = "DefaultConnection" },
                    new DatabaseConfigItem { Id = "new", Name = "بيانات السنة الجديدة", ConnectionStringName = "NewConnection" }
                };
            }
            return list;
        }
    }

    public class DatabaseConfigItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ConnectionStringName { get; set; }
    }
}
