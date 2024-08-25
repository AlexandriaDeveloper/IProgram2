using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Core.Interfaces
{
    public interface IAccountRepository
    {
        Task<IdentityResult> RegisterUser(ApplicationUser user, string Password, List<string> roles);

        Task<ApplicationUser> Login(string username, string password);
        Task<bool> CheckUsernameExists(string username);

        Task<ApplicationUser> GetUserByUsername(string username);

        Task<ApplicationUser> GetUserById(string id);

        Task<IEnumerable<ApplicationUser>> GetUsers();

        Task<bool> CheckEmailExistsAsync(string email);

        Task<bool> CheckUserIdExistsAsync(string userId);

        Task AssignUserToRole(ApplicationUser user, string roleId);

        Task<List<string>> GetUserRoles(string userId);


        Task SignOut();
        Task<IdentityResult> ChangePasswordAsync(ApplicationUser user, string oldPassword, string newPassword);
    }
}