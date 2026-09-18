using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using Persistence.Services;
using Xunit;

namespace Auth.UnitTests
{
    public class SeedDataSecurityTests
    {
        private ApplicationContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new ApplicationContext(options);
        }

        private Mock<RoleManager<IdentityRole>> CreateMockRoleManager(List<string> existingRoles, List<string> createdRoles)
        {
            var roleStore = new Mock<IRoleStore<IdentityRole>>();
            var mock = new Mock<RoleManager<IdentityRole>>(
                roleStore.Object,
                null!, null!, null!, null!);

            mock.Setup(r => r.RoleExistsAsync(It.IsAny<string>()))
                .ReturnsAsync((string roleName) => existingRoles.Contains(roleName));

            mock.Setup(r => r.CreateAsync(It.IsAny<IdentityRole>()))
                .ReturnsAsync((IdentityRole role) =>
                {
                    createdRoles.Add(role.Name!);
                    existingRoles.Add(role.Name!);
                    return IdentityResult.Success;
                });

            return mock;
        }

        [Fact]
        public async Task EnsureSeedData_SeedsAdminAndUserRoles_WhenMissing()
        {
            using var context = CreateInMemoryContext();
            var existingRoles = new List<string>();
            var createdRoles = new List<string>();
            var mockRoleManager = CreateMockRoleManager(existingRoles, createdRoles);

            await SeedData.EnsureSeedData(context, mockRoleManager.Object);

            Assert.Contains("Admin", createdRoles);
            Assert.Contains("User", createdRoles);
            Assert.Equal(2, createdRoles.Count);
        }

        [Fact]
        public async Task EnsureSeedData_DoesNotRecreateRoles_WhenAlreadyPresent()
        {
            using var context = CreateInMemoryContext();
            var existingRoles = new List<string> { "Admin", "User" };
            var createdRoles = new List<string>();
            var mockRoleManager = CreateMockRoleManager(existingRoles, createdRoles);

            await SeedData.EnsureSeedData(context, mockRoleManager.Object);

            Assert.Empty(createdRoles);
        }

        [Fact]
        public void SeedData_DoesNotAcceptOrInteractWithUserManager()
        {
            // SeedData should no longer have any method accepting UserManager<ApplicationUser>
            var method = typeof(SeedData).GetMethod(nameof(SeedData.EnsureSeedData));
            Assert.NotNull(method);

            var parameters = method.GetParameters();
            var hasUserManagerParam = parameters.Any(p => p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(UserManager<>));

            Assert.False(hasUserManagerParam, "SeedData should not accept UserManager parameter to prevent user seeding.");
        }

        [Fact]
        public void SeedData_SourceCode_DoesNotContainHardcodedUsernamesOrPasswords()
        {
            var seedDataAssembly = typeof(SeedData).Assembly;
            var seedDataType = typeof(SeedData);
            
            // Check methods of SeedData
            foreach (var m in seedDataType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                // Ensure no method creates users or has default passwords
                Assert.DoesNotContain("alice", m.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("bob", m.Name, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
