using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Persistence.Extensions
{
    public static class ClaimPrincipalExtensions
    {

        public static string RetriveAuthUserFromPrincipal(this System.Security.Claims.ClaimsPrincipal user)
        {
            return user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value;

        }

        // Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier).Value

    }
}