using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Core.Models;
using Auth.Infrastructure;

namespace Persistence.Services
{
    public static class SeedData
    {
        public static async Task EnsureSeedData(ApplicationContext context, RoleManager<IdentityRole> roleMgr)
        {
            if (context.Database.IsRelational())
            {
                await context.Database.MigrateAsync();
            }

            string[] roles = { "Admin", "User" };
            foreach (var role in roles)
            {
                if (!await roleMgr.RoleExistsAsync(role))
                {
                    await roleMgr.CreateAsync(new IdentityRole(role));
                }
            }
        }
    }
}
