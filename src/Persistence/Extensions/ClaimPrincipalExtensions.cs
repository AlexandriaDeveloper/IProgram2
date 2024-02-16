using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

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
        // Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value

    }
}