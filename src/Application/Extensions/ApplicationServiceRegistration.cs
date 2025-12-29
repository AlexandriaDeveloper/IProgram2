using Application.Features;
using Application.Interfaces;
using Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Application.Extensions
{
    public static class ApplicationServiceRegistration
    {
        public static IServiceCollection AddApplicationServices(this IServiceCollection services)
        {
            services.AddScoped<IDashboardService, DashboardService>();
            services.AddScoped<ReportService>();
            services.AddScoped<FormArchivedService>();
            services.AddScoped<AccountService>();
            services.AddScoped<DailyReferenceService>();
            services.AddScoped<DailyService>();
            services.AddScoped<DepartmentService>();
            services.AddScoped<EmployeeBankService>();
            services.AddScoped<EmployeeRefernceService>();
            services.AddScoped<EmployeeService>();
            services.AddScoped<FormDetailsService>();
            services.AddScoped<FormReferenceService>();
            services.AddScoped<FormService>();
            services.AddScoped<RoleService>();
            services.AddScoped<UserAuthService>();
            return services;
        }
    }
}
