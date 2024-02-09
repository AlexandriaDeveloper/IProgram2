using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Application.Helpers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Org.BouncyCastle.Ocsp;

namespace Api.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    [ApiController]
    [Route("api/[controller]")]
    public class BaseApiController : ControllerBase
    {

        protected IActionResult HandleResult<T>(Result<T> result)
        {
            IActionResult response = null;

            if (result.IsSuccess && result.Error == Error.None) return Ok(result.Value);
            if (result.IsSuccess && result.Error != null) return Problem(result.Error);


            if (result == null) return NotFound(result);

            if (result.IsFailure && result.Error != null)
            {
                switch (result.Error.Code)
                {
                    case "404":
                        response = NotFound(result);// Result.Failure(new Error("404", "Not found"));
                        break;
                    case "400":
                        response = BadRequest(result);// Result.Failure(new Error("400", "Bad request"));
                        break;
                    case "401":
                        response = Unauthorized(result);// Result.Failure(new Error("401", "Unauthorized"));
                        break;
                    case "403":
                        response = Forbid();// Result.Failure(new Error("403", "Forbidden"));
                        break;
                    case "500":
                        response = Problem(result.Error.Message, null, int.Parse(result.Error.Code), "خطأ اثناء العمليه");
                        break;
                    case "1000":
                        response = BadRequest(result); // BadRequest(result);
                        break;
                    default:
                        response = new BadRequestObjectResult(result) { StatusCode = 500 }; // Result.Failure(new Error("500", "Internal server error"));
                        break;
                }
            }
            if (response == null)
            {
                response = new BadRequestObjectResult(result) { StatusCode = 500 };
            }
            return response;
        }


        protected IActionResult HandleResult(Result result)
        {
            IActionResult response = null;

            if (result.IsSuccess && result.Error == Error.None) return Ok(result);
            if (result.IsSuccess && result.Error != null) return Problem(result.Error);
            if (result == null) return NotFound(result);
            if (result.IsFailure && result.Error != null)
            {
                switch (result.Error.Code)
                {
                    case "404":
                        response = NotFound(result);
                        break;
                    case "400":
                        response = BadRequest(result);
                        break;
                    case "401":
                        response = Unauthorized(result);
                        break;
                    case "403":
                        response = Forbid();
                        break;
                    case "500":
                        response = Problem(result.Error.Message, null, int.Parse(result.Error.Code), "خطأ اثناء العمليه");
                        break;
                    case "1000":
                        response = BadRequest(result.Error);
                        break;
                    default:
                        response = new BadRequestObjectResult(result) { StatusCode = 500 }; // Result.Failure(new Error("500", "Internal server error"));
                        break;

                }

            }
            if (response == null)
            {
                response = new BadRequestObjectResult(result) { StatusCode = 500 };
            }


            return response;
            //return new BadRequestObjectResult(result) { StatusCode = 500 }; // Result.Failure(new Error("500", "Internal server error"));
        }

    }
}