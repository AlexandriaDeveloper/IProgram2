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
                "Server=localhost;Database=IProgramLocalDb2026;Integrated Security=True;TrustServerCertificate=True;",
                x => x.MigrationsHistoryTable(LocalSyncContext.MigrationsHistoryTableName, LocalSyncContext.MigrationsHistoryTableSchema));
            return new LocalSyncContext(optionsBuilder.Options);
        }
    }

    public class AzureSyncContextDesignTimeFactory : IDesignTimeDbContextFactory<AzureSyncContext>
    {
        public AzureSyncContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AzureSyncContext>();
            // Non-routable, credential-free placeholder used strictly as metadata-only for EF Core design-time model construction. Never used as a runtime connection.
            optionsBuilder.UseSqlServer(
                "Server=tcp:design-time.invalid,1433;Database=IProgramDb2026;Integrated Security=True;TrustServerCertificate=True;",
                x => x.MigrationsHistoryTable(AzureSyncContext.MigrationsHistoryTableName, AzureSyncContext.MigrationsHistoryTableSchema));
            return new AzureSyncContext(optionsBuilder.Options);
        }
    }
}
