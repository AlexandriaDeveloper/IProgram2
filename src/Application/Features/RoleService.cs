using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;
using Application.Shared;
using Core.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Application.Features
{
    public class RoleService
    {

        private readonly IRoleRepository _roleRepository;

        public RoleService(IRoleRepository roleRepository)
        {
            this._roleRepository = roleRepository;
        }



        public async Task<Result<List<RoleDto>>> GetRoles()
        {
            var roles = await _roleRepository.GetRolesAsync();

            if (roles == null)
                return Result.Failure<List<RoleDto>>(new Error("404", "No roles found"));
            var roleDtos = roles.Select(x => new RoleDto
            {
                Id = x.Id,
                Name = x.Name
            }).ToList();


            return Result.Success(roleDtos);
        }
    }
}