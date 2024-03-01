using System.Security.Claims;
using Api.Filters;
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;

using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;


namespace Api.Controllers
{

    public class AccountController : BaseApiController
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly AccountService _accountService;
        private readonly ITokenService _tokenService;
        //private readonly IMapper _mapper;
        public AccountController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            ITokenService tokenService,
            RoleManager<IdentityRole> roleManager,
            AccountService accountService
             //  IMapper mapper
             )
        {
            // this._mapper = mapper;
            this._tokenService = tokenService;
            this._signInManager = signInManager;
            this._userManager = userManager;
            this._roleManager = roleManager;
            this._accountService = accountService;
        }


        [HttpGet]
        public async Task<IActionResult> GetCurrentUser()
        {
            return HandleResult<UserDto>(await _accountService.GetCurrentUserByNameAsync(User.Identity.Name));
        }
        [HttpGet("emailexists")]
        public async Task<ActionResult<bool>> CheckEmailExistsAsync([FromQuery] string email)
        {
            return await _accountService.CheckEmailExistsAsync(email);
        }
        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginDto loginDto)
        {

            var user = await _accountService.Login(loginDto.Username, loginDto.Password);

            return HandleResult(user); // return user;

        }





        [HttpGet("GetRoleByUserId/{userId}")]

        public async Task<ActionResult<List<string>>> GetRoles(string userId)
        {
            //Only if you are an admin or you are logedin user 
            if (User.IsInRole("Admin") || User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier)?.Value == userId)
            {

                var user = await _userManager.FindByIdAsync(userId);
                if (user == null) return BadRequest("User does not exist");
                var roles = await _userManager.GetRolesAsync(user);
                return roles.ToList();
            }
            return Unauthorized();

        }




        [HttpPost("register")]

        [IncludeRolesFilter("Admin")]
        public async Task<IActionResult> Register(RegisterRequest registerDto)
        {
            if (ModelState.IsValid == false)
            {
                return BadRequest(ModelState.FirstOrDefault().Value);
            }
            var user = await _accountService.RegisterUser(registerDto, registerDto.Password);

            return HandleResult<UserDto>(user);
        }

        [HttpPut("ChangePassword")]
        public async Task<ActionResult<UserDto>> ChangePassword(ChangePasswordDto changePasswordDto)
        {
            var userId = User.Claims.FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                return NotFound();
            }
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return NotFound();
            }
            var result = await _userManager.ChangePasswordAsync(user, changePasswordDto.OldPassword, changePasswordDto.NewPassword);
            if (!result.Succeeded)
            {
                return BadRequest();
            }
            return Ok();

        }


        [HttpGet("logout")]
        public async Task<IActionResult> Logout()
        {
            await _accountService.SignOut();
            return Ok();
        }

        [HttpGet("GetUsers")]
        [Authorize(Roles = "User")]
        public async Task<IActionResult> GetUsers()
        {

            return await Task.FromResult(Content("hello from secure controller"));
        }


    }


}