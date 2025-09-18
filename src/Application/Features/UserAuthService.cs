using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Application.Features
{
    public class UserAuthService
    {
        private readonly UserManager<ApplicationUser> _userMgr;
        private readonly RoleManager<IdentityRole> _roleMgr;

        public UserAuthService(UserManager<ApplicationUser> userMgr, RoleManager<IdentityRole> roleMgr)
        {
            this._userMgr = userMgr;
            this._roleMgr = roleMgr;
        }

        //GetUserById
        public async Task<ApplicationUser> GetUserById(string id)
        {
            var user = await _userMgr.FindByIdAsync(id);
            return user;
        }
        //GetRoles By UserId
        public async Task<IList<string>> GetUserRoles(string id)
        {
            var user = await _userMgr.FindByIdAsync(id);
            if (user == null) return new List<string>();
            var roles = await _userMgr.GetRolesAsync(user);
            return roles;
        }
    }
}