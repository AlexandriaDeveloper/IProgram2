
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Persistence.Repository
{
    public class AccountRepository : IAccountRepository
    {
        private readonly ApplicationContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly SignInManager<ApplicationUser> _signInManager;

        public AccountRepository(UserManager<ApplicationUser> userManager, RoleManager<IdentityRole> roleManager, SignInManager<ApplicationUser> signInManager, ApplicationContext context)
        {
            this._signInManager = signInManager;
            this._roleManager = roleManager;
            this._userManager = userManager;
            this._context = context;
        }
        public async Task AssignUserToRole(ApplicationUser user, string roleName)
        {
            await _userManager.AddToRoleAsync(user, roleName);
        }



        public Task<ApplicationUser> GetUserByUsername(string username)
        {
            return _userManager.FindByNameAsync(username);
        }
        public Task<ApplicationUser> GetUserById(string id)
        {
            return _userManager.FindByIdAsync(id);
        }


        public async Task<List<string>> GetUserRoles(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            if (user == null)
            {
                return null;
            }
            return _userManager.GetRolesAsync(user).Result.ToList();
        }

        public async Task<IEnumerable<ApplicationUser>> GetUsers()
        {
            return await _userManager.Users.ToListAsync();
        }

        public async Task<ApplicationUser> Login(string username, string password)
        {
            var user = await _userManager.FindByNameAsync(username);
            if (user == null) return null;
            var result = await _userManager.CheckPasswordAsync(user, password);
            if (!result) return null;
            return user;

        }

        public async Task<IdentityResult> RegisterUser(ApplicationUser user, string Password, List<string> roles)
        {
            var user2 = await _userManager.CreateAsync(user, Password);
            await _userManager.AddToRolesAsync(user, roles);

            return user2;
        }

        public async Task<bool> CheckUsernameExists(string username)
        {

            var user = await _userManager.FindByNameAsync(username);


            return user != null;
        }

        public async Task<bool> CheckEmailExistsAsync(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            return user != null;
        }

        public async Task<bool> CheckUserIdExistsAsync(string userId)
        {
            var user = await _userManager.FindByIdAsync(userId);
            return user != null;
        }


        public async Task SignOut()
        {
            await this._signInManager.SignOutAsync();
        }
        //change password
        public async Task<IdentityResult> ChangePasswordAsync(ApplicationUser user, string oldPassword, string newPassword)
        {


            IdentityResult result = await _userManager.ChangePasswordAsync(user, oldPassword, newPassword);

            return result;


        }
    }
}