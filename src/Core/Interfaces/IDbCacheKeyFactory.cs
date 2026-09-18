namespace Core.Interfaces
{
    public interface IDbCacheKeyFactory
    {
        /// <summary>
        /// Creates a cache key scoped to the currently selected database, entity, and entity identifier.
        /// Example: $"{dbId}:{entity}:{id}" -> "2026:FormDetails:123"
        /// </summary>
        string CreateKey(string entity, object id);

        /// <summary>
        /// Creates a cache key scoped to the currently selected database and a general key string.
        /// Example: $"{dbId}:{key}" -> "2026:departments_all"
        /// </summary>
        string CreateKey(string key);

        /// <summary>
        /// Creates a strongly-typed cache key specifically for FormDetails.
        /// Example: $"{dbId}:FormDetails:{formId}" -> "2026:FormDetails:123"
        /// </summary>
        string GetFormDetailsKey(int formId);

        /// <summary>
        /// Gets the logical database ID currently in scope.
        /// </summary>
        string GetCurrentDatabaseId();
    }
}
