using Core.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Auth.Infrastructure;

public static class IdentityExtension
{

    public static IServiceCollection AddIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCors(opt =>
        {
            opt.AddPolicy("CorsPolicy", policy =>
            {
                policy.AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials()
                .WithExposedHeaders("WWW-Authenticate")
                .WithExposedHeaders("Access-Control-Allow-Origin")
                .WithOrigins([
                    "http://localhost:4200",
                    "https://localhost:4200",
                    "http://localhost:5000",
                    "https://localhost:5001",
                    "http://localhost",
                    "https://localhost",
                ]);

            });
        });



        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(opt =>
        {
            opt.SaveToken = true;
            opt.RequireHttpsMetadata = false;

            opt.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(configuration["Token:Key"])),
                ValidateIssuer = true,
                ValidIssuer = configuration["Token:Issuer"],
                ValidateAudience = false,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                RoleClaimType = "role"

            };
        }).AddCookie();


        services.AddIdentity<ApplicationUser, IdentityRole>()
        // .AddUserManager<ApplicationUser>()
        // .AddRoles<IdentityRole>()
        // .AddRoleManager<IdentityRole>()
        .AddEntityFrameworkStores<ApplicationContext>()
        .AddDefaultTokenProviders();




        return services;
    }

}
