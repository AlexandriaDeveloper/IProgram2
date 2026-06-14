using System.Collections.Generic;

namespace Core.Interfaces
{
    public interface IDbConnectionProvider
    {
        string GetConnectionString();
        string GetSelectedDatabaseId();
        List<DatabaseInfo> GetAvailableDatabases();
    }

    public class DatabaseInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }
}
