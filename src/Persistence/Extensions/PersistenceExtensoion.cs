using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Persistence.Helpers;
using Persistence.Repository;
using Persistence.Services;

namespace Persistence.Extensions
{
    public static class PersistenceExtensoion
    {
        public static IServiceCollection AddPersistence(this IServiceCollection services)
        {
            services.AddHttpContextAccessor();
            services.AddScoped<ITokenService, TokenService>();
            services.AddScoped<IUniteOfWork, UniteOfWork>();
            services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
            services.AddScoped<IAccountRepository, AccountRepository>();
            services.AddScoped<IRoleRepository, RoleRepository>();
            services.AddScoped<IEmployeeRepository, EmployeeRepository>();
            services.AddScoped<IDailyRepository, DailyRepository>();
            services.AddScoped<IFormRepository, FormRepository>();
            services.AddScoped<IFormDetailsRepository, FormDetailsRepository>();


            // services.AddScoped<IParam, Param>();
            return services;
        }
    }
}