using System;
using Core.Interfaces;

namespace Auth.Infrastructure.Services
{
    public class DbCacheKeyFactory : IDbCacheKeyFactory
    {
        private readonly IDbConnectionProvider _dbConnectionProvider;

        public DbCacheKeyFactory(IDbConnectionProvider dbConnectionProvider)
        {
            _dbConnectionProvider = dbConnectionProvider ?? throw new ArgumentNullException(nameof(dbConnectionProvider));
        }

        public string GetCurrentDatabaseId()
        {
            var dbId = _dbConnectionProvider.GetSelectedDatabaseId();
            return string.IsNullOrWhiteSpace(dbId) ? "default" : dbId.Trim();
        }

        public string GetFormDetailsKey(int formId)
        {
            return CreateKey("FormDetails", formId);
        }

        public string CreateKey(string entity, object id)
        {
            if (string.IsNullOrWhiteSpace(entity))
                throw new ArgumentException("Entity name cannot be null or whitespace.", nameof(entity));

            if (id == null)
                throw new ArgumentNullException(nameof(id));

            var dbId = GetCurrentDatabaseId();
            return $"{dbId}:{entity}:{id}";
        }

        public string CreateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Cache key cannot be null or whitespace.", nameof(key));

            var dbId = GetCurrentDatabaseId();
            return $"{dbId}:{key}";
        }
    }
}
