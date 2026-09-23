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

// Global Exception Handling & Problem Details
builder.Services.AddExceptionHandler<Auth.Api.Middleware.GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// Application Health Checks
builder.Services.AddHealthChecks();

// Add Memory Cache for service-level caching
builder.Services.AddMemoryCache();

builder.Services
.AddIdentity(builder.Configuration)
.AddInfrastructure(builder.Configuration)
.AddPersistence()
.AddApplicationServices();

if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddScoped<Auth.Infrastructure.Sync.Push.IRemoteDatabaseConnectionFactory, Auth.Api.Testing.IsolatedTestRemoteDatabaseConnectionFactory>();
}

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

var maxUploadLimit = builder.Configuration.GetValue<long>("UploadLimits:GlobalMultipartBodyLengthLimit", 52428800L);
builder.Services.Configure<FormOptions>(o =>
{
    o.ValueLengthLimit = 10 * 1024 * 1024;
    o.MultipartBodyLengthLimit = maxUploadLimit;
    o.MemoryBufferThreshold = 2 * 1024 * 1024;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var isLocalFirst = app.Configuration.GetValue<bool>("LocalFirst:Enabled", false);
    var isReadOnly = app.Configuration.GetValue<bool>("LocalFirst:ReadOnlyMode", false);
    try 
    { 
        await SeedData.EnsureSeedData(roleMgr, skipRoleCreation: isLocalFirst || isReadOnly); 
    } 
    catch (Exception ex) 
    { 
        logger.LogError(ex, "Role seeding failed during application startup.");
    }
}


// Use centralized exception handling
app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

//app.UseHttpsRedirection();
app.UseRouting();

app.UseCors("CorsPolicy");

// Add Response Caching middleware BEFORE static files
app.UseResponseCaching();

// Block direct unauthenticated static access to sensitive reference directories
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var normalizedPath = path.Replace('\\', '/');
    if (normalizedPath.StartsWith("/content/DailyReferences", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith("/content/FormReferences", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith("/content/EmployeeReferences", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

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

// Enforce Read-Only Mode & Local-Only server-side guard
app.UseMiddleware<Auth.Api.Middleware.ReadOnlyModeMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

// Output Cache MUST be placed after Authentication & Authorization
app.UseOutputCache();

if (app.Configuration.GetValue<bool>("LegacyMigration:Enabled", false))
{
    app.MapHub<Auth.Infrastructure.Hubs.MigrationHub>("/migrationHub");
}

// Health check endpoint (application liveness)
app.MapHealthChecks("/health");

if (app.Configuration.GetValue<bool>("E2E:DiagnosticsEnabled", false))
{
    // Double-guarded diagnostic endpoint for E2E runtime DB safety verification (disabled in normal runtimes)
    app.MapGet("/api/diagnostics/e2e-db-safety", (IConfiguration configuration) =>
    {
        var databases = configuration.GetSection("DatabaseSettings:Databases").GetChildren().ToList();
        var results = new List<object>();
        bool allSafe = true;

        foreach (var db in databases)
        {
            var id = db["Id"];
            var connName = db["ConnectionStringName"] ?? "DefaultConnection";
            var connStr = configuration.GetConnectionString(connName);

            if (string.IsNullOrWhiteSpace(connStr))
            {
                return Results.Problem($"Missing connection string for '{connName}'.", statusCode: 500);
            }

            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connStr);
            var server = builder.DataSource?.ToLowerInvariant().Trim() ?? string.Empty;
            var initialCatalog = builder.InitialCatalog?.Trim() ?? string.Empty;

            bool isLocal = server == "localhost" || server == "(local)" || server == "127.0.0.1" ||
                           server == "." || server.StartsWith("localhost\\") || server.StartsWith("(local)\\") ||
                           server.StartsWith(".\\");

            bool isAzureOrRemote = server.Contains("database.windows.net") || server.Contains("azure");
            bool isQuarantined = initialCatalog.Equals("IProgramLocalDb2026", StringComparison.OrdinalIgnoreCase);
            bool isApprovedDb = initialCatalog.Equals("IProgramDb2026", StringComparison.OrdinalIgnoreCase) ||
                                initialCatalog.Equals("IProgramDb2027", StringComparison.OrdinalIgnoreCase);

            bool isSafe = isLocal && !isAzureOrRemote && !isQuarantined && isApprovedDb;
            if (!isSafe) allSafe = false;

            results.Add(new
            {
                DatabaseId = id,
                ServerClassification = isLocal ? "LOCAL_INSTANCE" : "REMOTE_NON_LOCAL",
                ApprovedDatabase = isApprovedDb,
                InitialCatalog = initialCatalog,
                IsQuarantinedDatabase = isQuarantined,
                IsAzureOrRemote = isAzureOrRemote,
                IsSafe = isSafe
            });
        }

        return Results.Ok(new
        {
            SafetyCheckPassed = allSafe,
            DatabaseCount = results.Count,
            Databases = results
        });
    });

    app.MapGet("/api/diagnostics/connection-audit", () =>
    {
        var records = Auth.Infrastructure.Sync.ConnectionAuditTracker.GetRecords();
        var disallowed = records.Where(r => !r.Allowed || !r.IsLocal || r.IsFallbackEndpoint).ToList();
        return Results.Ok(new
        {
            totalConnections = records.Count,
            allowedLocalConnections = records.Count(r => r.Allowed && r.IsLocal && !r.IsFallbackEndpoint),
            disallowedRemoteConnections = disallowed.Count,
            fallbackAttempts = records.Count(r => r.IsFallbackEndpoint),
            disallowedAttempts = disallowed,
            records = records
        });
    });

    app.MapPost("/api/diagnostics/connection-audit/clear", () =>
    {
        Auth.Infrastructure.Sync.ConnectionAuditTracker.Clear();
        return Results.Ok(new { message = "Connection audit records cleared." });
    });
}

app.MapControllers();
app.MapFallbackToController("Index", "Fallback");


app.Run();

// Trigger rebuild for MigrationController
