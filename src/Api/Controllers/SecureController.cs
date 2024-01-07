using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    public class SecureController : BaseApiController
    {

        [HttpGet("secure")]
        public async Task<ActionResult> SecureContent()
        {

            return await Task.FromResult(Ok(new { message = " Hello from secure api controller with admin role" }));
        }
    }
}