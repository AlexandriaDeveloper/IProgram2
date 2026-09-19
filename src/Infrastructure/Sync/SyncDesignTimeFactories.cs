using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auth.Infrastructure.Sync
{
    public class LocalSyncContextDesignTimeFactory : IDesignTimeDbContextFactory<LocalSyncContext>
    {
        public LocalSyncContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<LocalSyncContext>();
            optionsBuilder.UseSqlServer(
                "Server=localhost;Database=IProgram_DesignTime;Integrated Security=True;TrustServerCertificate=True;",
                x => x.MigrationsHistoryTable(LocalSyncContext.MigrationsHistoryTableName, LocalSyncContext.MigrationsHistoryTableSchema));
            return new LocalSyncContext(optionsBuilder.Options);
        }
    }

    public class AzureSyncContextDesignTimeFactory : IDesignTimeDbContextFactory<AzureSyncContext>
    {
        public AzureSyncContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AzureSyncContext>();
            optionsBuilder.UseSqlServer(
                "Server=localhost;Database=IProgram_DesignTime;Integrated Security=True;TrustServerCertificate=True;",
                x => x.MigrationsHistoryTable(AzureSyncContext.MigrationsHistoryTableName, AzureSyncContext.MigrationsHistoryTableSchema));
            return new AzureSyncContext(optionsBuilder.Options);
        }
    }
}
