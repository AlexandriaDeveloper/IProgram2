using Microsoft.EntityFrameworkCore;

namespace Auth.Infrastructure;

public class SupabaseContext : ApplicationContext
{
    public SupabaseContext(DbContextOptions<SupabaseContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Fix for "Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'"
        // This forces all DateTime properties to use 'timestamp without time zone'
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
                {
                    property.SetColumnType("timestamp without time zone");
                }
            }
        }
    }
}
