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
        services.AddDbContext<ApplicationContext>(options =>
           options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
       // options.UseNpgsql(configuration.GetConnectionString("SupabaseConnection")));








        services.AddHttpContextAccessor();
        services.AddScoped<Core.Interfaces.ICurrentUserService, Services.CurrentUserService>();
        services.AddScoped<Core.Interfaces.IFileStorageService, Services.CloudinaryService>();
        services.AddScoped<Services.DataMigrationService>();

        return services;
    }

}
