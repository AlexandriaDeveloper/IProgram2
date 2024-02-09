
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Helpers;
using Core.Interfaces;
using Core.Models;

namespace Application.Features
{
    public class AccountService
    {
        private readonly IAccountRepository _accountRepository;
        private readonly IRoleRepository _roleRepository;
        private readonly ITokenService _tokenService;
        public AccountService(IAccountRepository accountRepository, IRoleRepository roleRepository, ITokenService tokenService)
        {
            this._accountRepository = accountRepository;
            this._roleRepository = roleRepository;
            this._tokenService = tokenService;
        }


        public async Task<Result<UserDto>> Login(string username, string password)
        {

            var appUser = await _accountRepository.Login(username, password);
            if (appUser == null)
                return Result.Failure<UserDto>(new Error("500", "Invalid username or password"));
            var token = await _tokenService.CreateToken(appUser);
            var user = new UserDto
            {
                Email = appUser.Email,
                Token = await _tokenService.CreateToken(appUser),
                DisplayName = appUser.DisplayName,
                DisplayImage = appUser.DisplayImage
            };
            return Result.Success(user);

        }


        public async Task<UserDto> RegisterUser(RegisterRequest registerDto, string Password)
        {


            if (await CheckEmailExistsAsync(registerDto.Email))
            {
                return null;
            }

            if (await CheckUsernameExistsAsync(registerDto.Username))
            {
                return null;
            }

            var user = new ApplicationUser
            {
                DisplayName = registerDto.DisplayName,
                Email = registerDto.Email,
                UserName = registerDto.Username,
                DisplayImage = "default.jpg"
            };

            var result = await _accountRepository.RegisterUser(user, registerDto.Password);

            if (!result.Succeeded) return null;

            foreach (var role in registerDto.Roles)
            {
                var existedRole = await _roleRepository.GetRoleByNameAsync(role);
                if (existedRole == null)
                {
                    continue;
                }
                await AssignUserToRole(user, existedRole.Name);

            }


            return new UserDto
            {
                DisplayName = user.DisplayName,
                Token = await _tokenService.CreateToken(user),
                Email = user.Email,
                DisplayImage = user.DisplayImage
            };
        }

        public async Task<bool> CheckEmailExistsAsync(string email)
        {
            return await _accountRepository.CheckEmailExistsAsync(email);
        }

        public async Task<bool> CheckUsernameExistsAsync(string username)
        {
            return await _accountRepository.CheckUsernameExists(username);
        }

        public async Task<UserDto> GetCurrentUserByNameAsync(string username)
        {
            var user = await _accountRepository.GetUserByUsername(username);
            if (user == null)
            {
                return null;
            }
            return new UserDto
            {
                DisplayImage = user.DisplayImage,
                Email = user.Email,
                DisplayName = user.DisplayName,
                Token = await _tokenService.CreateToken(user)

            };
        }

        public async Task<bool> UserExists(string username)
        {
            return await _accountRepository.CheckUsernameExists(username);
        }
        public async Task AssignUserToRole(ApplicationUser user, string roleName)
        {

            await _accountRepository.AssignUserToRole(user, roleName);

        }


        public async Task SignOut()
        {
            await this._accountRepository.SignOut();
        }
    }
}