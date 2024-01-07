using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Helpers;
using Application.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer")]
    [ApiController]
    [Route("api/[controller]")]
    public class BaseApiController : ControllerBase
    {

        protected Result HandleResult<T>(Result<T> result)
        {
            if (result == null) return Result.Failure<T>(new Error("404", "An error occured"));
            // if (result.IsSuccess && result.Value != null) return Result.Success(result.Value);
            // if (result.IsSuccess && result.Value == null) return Result.Failure(new Error("404", "Not found"));
            if (result.IsFailure && result.Error != null)
            {
                switch (result.Error.Code)
                {
                    case "404":
                        return Result.Failure<T>(new Error("404", "Not found"));
                    case "400":
                        return Result.Failure<T>(new Error("400", "Bad request"));
                    case "401":
                        return Result.Failure<T>(new Error("401", "Unauthorized"));
                    case "403":
                        return Result.Failure<T>(new Error("403", "Forbidden"));
                    case "500":
                        return Result.Failure<T>(new Error("500", "Internal server error"));
                    default:
                        return Result.Failure<T>(new Error("500", "Internal server error"));

                }
            }
            return Result.Failure<T>(new Error("500", "Internal server error"));
        }

    }
}