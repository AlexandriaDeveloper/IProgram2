
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
        private readonly IDbConnectionProvider _dbConnectionProvider;

        public AccountRepository(
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            SignInManager<ApplicationUser> signInManager,
            ApplicationContext context,
            IDbConnectionProvider dbConnectionProvider)
        {
            this._signInManager = signInManager;
            this._roleManager = roleManager;
            this._userManager = userManager;
            this._context = context;
            this._dbConnectionProvider = dbConnectionProvider;
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

            var roles = await _userManager.GetRolesAsync(user);
            return roles.ToList();
        }

        public async Task<IEnumerable<ApplicationUser>> GetUsers()
        {
            return await _userManager.Users.ToListAsync();
        }

        public async Task<ApplicationUser> Login(string username, string password)
        {
            var user = await _userManager.FindByNameAsync(username);
            if (user == null) return null;

            bool isLocalRuntime = _dbConnectionProvider is ISyncConnectionProvider syncProvider &&
                                  (syncProvider.IsLocalFirstEnabled || syncProvider.IsReadOnlyMode);
            if (isLocalRuntime)
            {
                if (string.IsNullOrEmpty(user.PasswordHash)) return null;

                var verificationResult = _userManager.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
                if (verificationResult == PasswordVerificationResult.Failed)
                {
                    return null;
                }

                // In LocalFirst runtimes (ReadOnly or ReadWritePilot): both Success and SuccessRehashNeeded authenticate successfully
                // WITHOUT invoking _userManager.UpdateAsync(user) or mutating user.PasswordHash in the local database.
                return user;
            }

            var result = await _userManager.CheckPasswordAsync(user, password);
            if (!result) return null;
            return user;
        }

        public async Task<IdentityResult> RegisterUser(ApplicationUser user, string Password, List<string> roles)
        {
            var creationResult = await _userManager.CreateAsync(user, Password);
            if (!creationResult.Succeeded)
            {
                return creationResult;
            }

            var roleResult = await _userManager.AddToRolesAsync(user, roles);
            if (!roleResult.Succeeded)
            {
                return IdentityResult.Failed(roleResult.Errors.ToArray());
            }

            return creationResult;
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