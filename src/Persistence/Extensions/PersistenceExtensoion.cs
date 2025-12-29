using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NetCore.AutoRegisterDi;
using Persistence.Helpers;
using Persistence.Repository;
using Persistence.Services;
using Persistence.Services;

namespace Persistence.Extensions
{
  public static class PersistenceExtensoion
  {
    public static IServiceCollection AddPersistence(this IServiceCollection services)
    {
      services.AddHttpContextAccessor();
      services.AddScoped<ITokenService, TokenService>();
      services.AddScoped<IUnitOfWork, UnitOfWork>();
      services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));
      // services.AddScoped<IAccountRepository, AccountRepository>();
      // services.AddScoped<IRoleRepository, RoleRepository>();
      // services.AddScoped<IEmployeeRepository, EmployeeRepository>();
      // services.AddScoped<IDailyRepository, DailyRepository>();
      // services.AddScoped<IFormRepository, FormRepository>();
      // services.AddScoped<IFormDetailsRepository, FormDetailsRepository>();
      // services.AddScoped<IDepartmentRepository, DepartmentRepository>();

      //auto register services end with word Repository
      var assembliesToScan = new[]
        {
        Assembly.GetExecutingAssembly(),
        Assembly.GetAssembly(typeof(IAccountRepository)),
        Assembly.GetAssembly(typeof(AccountRepository))
   };

      services.RegisterAssemblyPublicNonGenericClasses(assembliesToScan)
        //commenting the line below means it will scan all public classes
        .Where(c => c.Name.EndsWith("Repository"))
        .AsPublicImplementedInterfaces();


      // services.AddScoped<IParam, Param>();
      return services;
    }
  }
}