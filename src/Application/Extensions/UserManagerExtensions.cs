using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Application.Extensions
{
    public static class UserManagerExtensions
    {
        public static async Task<ApplicationUser> FindByDisplayNameAsync(this UserManager<ApplicationUser> um, string displayName)
        {
            return await um?.Users.FirstOrDefaultAsync(x => x.DisplayName == displayName);
        }

    }
}