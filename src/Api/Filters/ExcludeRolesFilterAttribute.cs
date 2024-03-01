using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Org.BouncyCastle.Asn1.Cms;

namespace Api.Filters
{
    public class ExcludeRolesFilterAttribute : System.Attribute, IAuthorizationFilter
    {
        private readonly string[] _roles;

        public ExcludeRolesFilterAttribute(params string[] roles)
        {
            this._roles = roles;
        }
        public void OnAuthorization(AuthorizationFilterContext context)
        {
            ClaimsPrincipal? user = context.HttpContext.User;
            if (_roles.Any(role => user.IsInRole(role)))
                context.Result = new StatusCodeResult((int)HttpStatusCode.Forbidden);
        }
    }
}