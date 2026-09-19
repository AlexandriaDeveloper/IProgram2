using Core.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Infrastructure;

public static class InfrastructureExtension
{

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {

        var supabaseConnection = configuration.GetConnectionString("SupabaseConnection");
        var defaultConnection = configuration.GetConnectionString("DefaultConnection");

        // if (!string.IsNullOrEmpty(supabaseConnection))
        // {
        //     services.AddDbContext<SupabaseContext>(options =>
        //         options.UseNpgsql(supabaseConnection));

        //     services.AddScoped<ApplicationContext>(provider => provider.GetRequiredService<SupabaseContext>());
        // }
        var sqlOptions = Configuration.SqlServerOptions.FromConfiguration(configuration);
        services.AddScoped<Core.Interfaces.IDbConnectionProvider, Services.DbConnectionProvider>();
        services.AddScoped<Core.Interfaces.ISyncConnectionProvider>(provider => (Services.DbConnectionProvider)provider.GetRequiredService<Core.Interfaces.IDbConnectionProvider>());

        services.AddDbContext<Sync.LocalSyncContext>((serviceProvider, options) =>
        {
            var syncProvider = serviceProvider.GetRequiredService<Core.Interfaces.ISyncConnectionProvider>();
            var databaseId = syncProvider.GetSelectedDatabaseId();
            var localConnStr = syncProvider.GetLocalConnectionString(databaseId);
            options.UseSqlServer(localConnStr, o =>
            {
                o.UseCompatibilityLevel(120);
                o.MigrationsHistoryTable(Sync.LocalSyncContext.MigrationsHistoryTableName, Sync.LocalSyncContext.MigrationsHistoryTableSchema);
                o.EnableRetryOnFailure(
                    maxRetryCount: sqlOptions.MaxRetryCount,
                    maxRetryDelay: TimeSpan.FromSeconds(sqlOptions.MaxRetryDelaySeconds),
                    errorNumbersToAdd: null);
                o.CommandTimeout(sqlOptions.CommandTimeoutSeconds);
            });
        });

        services.AddScoped<Core.Interfaces.ILocalBootstrapWriteGate, Sync.LocalBootstrapWriteGate>();

        services.AddDbContext<ApplicationContext>((serviceProvider, options) =>
        {
            var dbProvider = serviceProvider.GetRequiredService<Core.Interfaces.IDbConnectionProvider>();
            options.UseSqlServer(dbProvider.GetConnectionString(), o =>
            {
                o.UseCompatibilityLevel(120);
                o.EnableRetryOnFailure(
                    maxRetryCount: sqlOptions.MaxRetryCount,
                    maxRetryDelay: TimeSpan.FromSeconds(sqlOptions.MaxRetryDelaySeconds),
                    errorNumbersToAdd: null);
                o.CommandTimeout(sqlOptions.CommandTimeoutSeconds);
            });
        });








        services.AddHttpContextAccessor();
        services.AddScoped<Core.Interfaces.ICurrentUserService, Services.CurrentUserService>();
        services.AddScoped<Core.Interfaces.IFileStorageService, Services.CloudinaryService>();
        services.AddScoped<Core.Interfaces.IDbCacheKeyFactory, Services.DbCacheKeyFactory>();
        services.AddScoped<Services.DataMigrationService>();

        return services;
    }

}
