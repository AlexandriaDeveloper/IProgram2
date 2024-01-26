
using Application.Dtos;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{

    public class RoleController : BaseApiController
    {
        private readonly RoleService _roleService;

        public RoleController(RoleService roleService)
        {
            this._roleService = roleService;

        }
        [HttpGet("GetRoles")]
        public async Task<ActionResult<Result<List<RoleDto>>>> GetRoles()
        {
            return await _roleService.GetRoles();

        }


        //  [HttpPost("AddRole")]
        // [Authorize(Roles = "Admin")]
        // public async Task<ActionResult<UserDto>> AddRole(RoleDto roleDto)
        // {
        //     var role = await _roleManager.FindByNameAsync(roleDto.Name);
        //     if (role != null) return BadRequest("Role already exists");
        //     var result = await _roleManager.CreateAsync(new IdentityRole(roleDto.Name));
        //     if (!result.Succeeded) return BadRequest();
        //     return Ok();
        // }



        //    [Authorize(Roles = "Admin")]
        // [HttpDelete("RemoveRole")]
        // public async Task<ActionResult<UserDto>> RemoveRole(string roleId)
        // {
        //     var role = await _roleManager.FindByIdAsync(roleId);
        //     if (role == null) return BadRequest("Role does not exist");
        //     var result = await _roleManager.DeleteAsync(role);
        //     if (!result.Succeeded) return BadRequest();
        //     return Ok();
        // }
    }
}