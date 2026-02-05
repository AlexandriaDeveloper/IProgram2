using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.OpenApi.Models;
using Persistence.Extensions;
using Persistence.Services;
using Application.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;

using System.Security.Claims;

using QuestPDF.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers(options =>
{
    // Add Response Caching
    options.CacheProfiles.Add("Default", new Microsoft.AspNetCore.Mvc.CacheProfile { Duration = 300 }); // 5 minutes
    options.CacheProfiles.Add("Short", new Microsoft.AspNetCore.Mvc.CacheProfile { Duration = 60 }); // 1 minute
    options.CacheProfiles.Add("Long", new Microsoft.AspNetCore.Mvc.CacheProfile { Duration = 1800 }); // 30 minutes
});

// Add Response Caching middleware
builder.Services.AddResponseCaching();

// Add Output Caching for more advanced scenarios
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromMinutes(30)));
    options.AddPolicy("Short", builder => builder.Expire(TimeSpan.FromMinutes(1)));
});

// Add Memory Cache for service-level caching
builder.Services.AddMemoryCache();

builder.Services
.AddIdentity(builder.Configuration)
.AddInfrastructure(builder.Configuration)
.AddPersistence()
.AddInfrastructure(builder.Configuration)
.AddPersistence()
.AddApplicationServices();

builder.Services.AddSignalR(); // Add SignalR Service


// builder.Services.AddScoped<ITokenService, TokenService>();
// SeedData.EnsureSeedData(builder.Services.BuildServiceProvider());

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Auth API",
        Version = "v1"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Basic auth added to authorization header",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Scheme = "Bearer",
        Type = SecuritySchemeType.ApiKey,
        BearerFormat = "JWT"

    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer",

                }
            },
            new string[] {}
        }
    });

});

builder.Services.Configure<FormOptions>(o =>
{
    o.ValueLengthLimit = int.MaxValue;
    o.MultipartBodyLengthLimit = int.MaxValue;
    o.MemoryBufferThreshold = int.MaxValue;
});

var app = builder.Build();

// ========== AUTO MIGRATION DISABLED ==========
// Migration is now triggered manually via API endpoint
// try
// {
//     RunMigration.Execute();
// }
// catch (Exception ex)
// {
//     Console.WriteLine($"MIGRATION ERROR: {ex.Message}");
// }
// =============================================

var scope = app.Services.CreateScope();
// try 
// { 
//     ManualCleanup.Execute(app.Configuration); 
// } 
// catch (Exception ex) 
// { 
//     Console.WriteLine("Cleanup Error: " + ex.Message); 
// }

var context = scope.ServiceProvider.GetRequiredService<ApplicationContext>();

var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
try 
{ 
    if (userMgr.Users.Count() == 0) 
        SeedData.EnsureSeedData(context, userMgr, roleMgr); 
} 
catch (Exception ex) 
{ 
    Console.WriteLine(ex); 
}


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();
app.UseRouting();

// Add Response Caching middleware BEFORE static files
app.UseResponseCaching();

// Add Output Caching middleware
app.UseOutputCache();

app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(Directory.GetCurrentDirectory(), @"Content")),
    RequestPath = "/content",
    OnPrepareResponse = ctx =>
    {
        // Cache static files for 1 hour
        ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=3600");
    }
});

app.UseCors("CorsPolicy");


app.UseAuthentication();
app.UseAuthorization();
    // await next(context);
//app.MapControllers();
app.MapHub<Auth.Infrastructure.Hubs.MigrationHub>("/migrationHub"); // Map MigrationHub


app.UseEndpoints(endpoints =>
         {
             endpoints.MapControllers();
             endpoints.MapFallbackToController("Index", "Fallback");
         });


app.Run();

// Trigger rebuild for MigrationController
