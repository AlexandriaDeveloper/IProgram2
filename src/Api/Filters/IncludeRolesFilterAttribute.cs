using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Api.Filters
{
    public class IncludeRolesFilterAttribute : System.Attribute, IAuthorizationFilter
    {
        private readonly string[] _roles;

        public IncludeRolesFilterAttribute(params string[] roles)
        {
            this._roles = roles;
        }
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ClaimsPrincipal? user = context.HttpContext.User;
            if (!_roles.Any(role => user.IsInRole(role)))
                context.Result = new StatusCodeResult((int)HttpStatusCode.Forbidden);
        }

    }
}