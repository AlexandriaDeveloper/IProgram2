using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;

namespace Persistence.Services
{
    public static class SeedData
    {
        public static async Task EnsureSeedData(RoleManager<IdentityRole> roleMgr)
        {
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
