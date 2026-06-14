
using System.IdentityModel.Tokens.Jwt;

using System.Security.Claims;
using System.Text;

using Core.Interfaces;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Persistence.Services
{
    public class TokenService : ITokenService

    {
        private readonly IConfiguration _config;
        private readonly UserManager<ApplicationUser> _userManager;

        private readonly SymmetricSecurityKey _key;
        private readonly IDbConnectionProvider _dbProvider;

        public TokenService(IConfiguration config, UserManager<ApplicationUser> userManager, IDbConnectionProvider dbProvider)
        {
            this._userManager = userManager;
            this._config = config;
            this._key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Token:Key"]));
            this._dbProvider = dbProvider;
        }
        public async Task<string> CreateToken(ApplicationUser user)
        {

            var claims = new List<Claim>{
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.GivenName,user.DisplayName),
                new Claim(ClaimTypes.NameIdentifier, user.Id),
                new Claim("db", _dbProvider.GetSelectedDatabaseId())
               };

            var roles = await _userManager.GetRolesAsync(user);
            claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

            var cred = new SigningCredentials(_key, SecurityAlgorithms.HmacSha512Signature);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.Now.AddDays(7),
                SigningCredentials = cred,
                Issuer = _config["Token:Issuer"]
            };
            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }
    }
}