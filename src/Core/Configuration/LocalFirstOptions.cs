using System.Collections.Generic;

namespace Core.Configuration
{
    public class LocalFirstOptions
    {
        public const string SectionName = "LocalFirst";

        public bool Enabled { get; set; } = false;
        public string SqlServerInstance { get; set; } = @"localhost\SQLEXPRESS";
        public List<LocalDatabaseConfigItem> Databases { get; set; } = new List<LocalDatabaseConfigItem>();
    }

    public class LocalDatabaseConfigItem
    {
        public string Id { get; set; } = string.Empty;
        public string LocalDatabaseName { get; set; } = string.Empty;
        public string LocalConnectionStringName { get; set; } = string.Empty;
    }
}
