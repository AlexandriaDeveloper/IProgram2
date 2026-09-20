using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace Persistence.Services
{
    public static class SeedData
    {
        public static async Task EnsureSeedData(RoleManager<IdentityRole> roleMgr, bool skipRoleCreation = false)
        {
            string[] roles = { "Admin", "User" };
            foreach (var role in roles)
            {
                if (!await roleMgr.RoleExistsAsync(role))
                {
                    if (skipRoleCreation)
                    {
                        continue;
                    }
                    await roleMgr.CreateAsync(new IdentityRole(role));
                }
            }
        }
    }
}
