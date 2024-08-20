using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Core.Interfaces
{
    public interface IRoleRepository
    {
        Task AddRoleAsync(string roleName);

        Task RemoveRoleAsync(string roleName);
        Task<List<IdentityRole>> GetRolesAsync();
        Task<IdentityRole> GetRoleByNameAsync(string name);
        Task<IdentityRole> GetRoleByIdAsync(string id);
        Task<bool> RoleExistsAsync(string roleName);
        Task<List<string>> GetUserRoles(string UserId);
        Task<List<ApplicationUser>> GetUsersInRoleAsync(string roleName);

    }
}