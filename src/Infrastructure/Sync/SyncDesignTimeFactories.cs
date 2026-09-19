using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auth.Infrastructure.Sync
{
    public class LocalSyncContextDesignTimeFactory : IDesignTimeDbContextFactory<LocalSyncContext>
    {
        public LocalSyncContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<LocalSyncContext>();
            optionsBuilder.UseSqlServer("Server=localhost\\SQLEXPRESS;Database=IProgram_DesignTime;Integrated Security=True;TrustServerCertificate=True;");
            return new LocalSyncContext(optionsBuilder.Options);
        }
    }

    public class AzureSyncContextDesignTimeFactory : IDesignTimeDbContextFactory<AzureSyncContext>
    {
        public AzureSyncContext CreateDbContext(string[] args)
        {
            var optionsBuilder = new DbContextOptionsBuilder<AzureSyncContext>();
            optionsBuilder.UseSqlServer("Server=localhost\\SQLEXPRESS;Database=IProgram_DesignTime;Integrated Security=True;TrustServerCertificate=True;");
            return new AzureSyncContext(optionsBuilder.Options);
        }
    }
}
