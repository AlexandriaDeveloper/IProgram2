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

        // Year-bound background-safe factory for LocalSyncContext
        services.AddScoped<Sync.ILocalSyncContextFactory, Sync.LocalSyncContextFactory>();

        // Request-bound LocalSyncContext
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

        // Server-side bootstrap write-gate
        services.AddScoped<Core.Interfaces.ILocalBootstrapWriteGate>(sp =>
            new Sync.LocalBootstrapWriteGate(sp.GetRequiredService<Sync.ILocalSyncContextFactory>()));
        services.AddScoped<Sync.LocalBootstrapWriteGateInterceptor>();
        services.AddScoped<Sync.LocalWriteSafetyInterceptor>();
        services.AddScoped<Sync.ReadOnlyDbCommandInterceptor>();
        services.AddScoped<Sync.ReadOnlyDbConnectionInterceptor>();

        // Slice 4.3C - Idempotent Daily Outbox Push to Azure
        services.AddScoped<Sync.Push.IRemoteDatabaseConnectionFactory, Sync.Push.AzureRemoteDatabaseConnectionFactory>();
        services.AddScoped<Sync.Push.IAzurePushTransactionCoordinator, Sync.Push.AzurePushTransactionCoordinator>();
        services.AddScoped<Sync.Push.ILocalPushLeaseManager, Sync.Push.LocalPushLeaseManager>();
        services.AddScoped<Sync.Push.ILocalOutboxPushService, Sync.Push.LocalOutboxPushService>();

        // Slice 4.4A - Authoritative Azure Daily Mutation Tracking
        services.AddScoped<Core.Interfaces.IAuthoritativeDailyMutationTracker, Sync.Authoritative.AuthoritativeDailyMutationTracker>();
        services.AddScoped<Core.Interfaces.IAuthoritativeDatabaseBindingGuard, Sync.Authoritative.AuthoritativeDatabaseBindingGuard>();
        services.AddScoped<Core.Interfaces.IAuthoritativeCutoverGuard, Sync.Authoritative.AuthoritativeCutoverGuard>();
        services.AddScoped<Sync.Authoritative.AuthoritativeTrackingSafetyInterceptor>();

        services.AddDbContext<ApplicationContext>((serviceProvider, options) =>
        {
            var dbProvider = serviceProvider.GetRequiredService<Core.Interfaces.IDbConnectionProvider>();
            var writeGateInterceptor = serviceProvider.GetRequiredService<Sync.LocalBootstrapWriteGateInterceptor>();
            var writeSafetyInterceptor = serviceProvider.GetRequiredService<Sync.LocalWriteSafetyInterceptor>();
            var authSafetyInterceptor = serviceProvider.GetRequiredService<Sync.Authoritative.AuthoritativeTrackingSafetyInterceptor>();
            var readOnlyCommandInterceptor = serviceProvider.GetRequiredService<Sync.ReadOnlyDbCommandInterceptor>();
            var readOnlyConnectionInterceptor = serviceProvider.GetRequiredService<Sync.ReadOnlyDbConnectionInterceptor>();

            options.AddInterceptors(writeGateInterceptor, writeSafetyInterceptor, authSafetyInterceptor, readOnlyCommandInterceptor, readOnlyConnectionInterceptor);
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
