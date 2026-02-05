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
         if (!string.IsNullOrEmpty(defaultConnection))
        {
            services.AddDbContext<ApplicationContext>(options =>
               options.UseSqlServer(defaultConnection));
        }








        services.AddHttpContextAccessor();
        services.AddScoped<Core.Interfaces.ICurrentUserService, Services.CurrentUserService>();
        services.AddScoped<Core.Interfaces.IFileStorageService, Services.CloudinaryService>();
        services.AddScoped<Services.DataMigrationService>();

        return services;
    }

}
