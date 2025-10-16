using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Core.Models;

namespace Persistence.Extensions
{
    public static class ClaimPrincipalExtensions
    {

        public static string RetriveAuthUserIdFromPrincipal(this System.Security.Claims.ClaimsPrincipal user)
        {
            return user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;

        }
        public static string RetriveAuthUserNameFromPrincipal(this System.Security.Claims.ClaimsPrincipal user)
        {
            return user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.GivenName).Value;

        }
        //Retrive Name By I 

        // Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value

    }


    public static class UserManagerExtensions
    {
        //Get User By Id
        public static async Task<string> GetUserByIdAsync(this UserManager<ApplicationUser> userManager, string id)
        {
            if (id == null) return null;
            return userManager.Users.FirstOrDefaultAsync(u => u.Id == id).Result.DisplayName;
        }
    }
}