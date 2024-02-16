using System.Reflection;
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



            var assembly = Assembly.GetAssembly(typeof(AccountService));
            var types = assembly.GetTypes().Where(t => t.Name.EndsWith("Service")).ToList();
            foreach (var type in types)
            {
                services.AddScoped(type);
            }
            return services;
        }



    }
}