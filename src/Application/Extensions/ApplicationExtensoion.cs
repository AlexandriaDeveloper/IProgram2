using Application.Features;
using Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Infrastructure;

namespace Application.Extensions
{
    public static class ApplicationExtensoion
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {

            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.EnableDebugging = true;
            services.AddScoped<AccountService>();
            services.AddScoped<RoleService>();
            services.AddScoped<EmployeeService>();
            services.AddScoped<ReportService>();
            services.AddScoped<DailyService>();
            services.AddScoped<FormService>();
            services.AddScoped<FormDetailsService>();
            return services;
        }
    }
}