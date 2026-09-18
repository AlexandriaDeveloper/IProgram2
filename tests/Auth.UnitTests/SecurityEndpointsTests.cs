using System;
using System.Linq;
using System.Reflection;
using Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Auth.UnitTests
{
    public class SecurityEndpointsTests
    {
        [Fact]
        public void BaseApiController_HasAuthorizeAttribute()
        {
            var authAttr = typeof(BaseApiController).GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(authAttr);
            Assert.Equal("Bearer", authAttr.AuthenticationSchemes);
        }

        [Fact]
        public void UploadDailyReference_DoesNotAllowAnonymous()
        {
            var method = typeof(DailyReferencesController).GetMethod(nameof(DailyReferencesController.UploadDailyReference));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);
        }

        [Fact]
        public void DeleteDailyReference_DoesNotAllowAnonymous()
        {
            var method = typeof(DailyReferencesController).GetMethod(nameof(DailyReferencesController.DeleteDailyReference));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);
        }

        [Fact]
        public void SyncLocalReferencesToCloudinary_RequiresAdmin_AndIsPostOnly()
        {
            var method = typeof(DailyReferencesController).GetMethod(nameof(DailyReferencesController.SyncLocalReferencesToCloudinary));
            Assert.NotNull(method);

            // No AllowAnonymous
            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);

            // Requires Admin role
            var authAttr = method.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(authAttr);
            Assert.Equal("Admin", authAttr.Roles);

            // Post only, NOT Get
            var postAttr = method.GetCustomAttribute<HttpPostAttribute>();
            var getAttr = method.GetCustomAttribute<HttpGetAttribute>();
            Assert.NotNull(postAttr);
            Assert.Null(getAttr);
        }

        [Fact]
        public void TestConnection_RequiresAdminRole_AndDoesNotAllowAnonymous()
        {
            var method = typeof(DailyReferencesController).GetMethod(nameof(DailyReferencesController.TestConnection));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);

            var authAttr = method.GetCustomAttribute<AuthorizeAttribute>();
            Assert.NotNull(authAttr);
            Assert.Equal("Admin", authAttr.Roles);
        }

        [Fact]
        public void UploadJsonForm_DoesNotAllowAnonymous()
        {
            var method = typeof(FormController).GetMethod(nameof(FormController.UploadJSONForm));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);
        }

        [Fact]
        public void DownloadDailyJson_DoesNotAllowAnonymous()
        {
            var method = typeof(DailyController).GetMethod(nameof(DailyController.DownloadJsonFile));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);
        }

        [Fact]
        public void EmployeeGetCollages_DoesNotAllowAnonymous()
        {
            var method = typeof(EmployeeController).GetMethod(nameof(EmployeeController.GetCollages));
            Assert.NotNull(method);

            var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();
            Assert.Null(allowAnonymous);
        }

        [Fact]
        public void VerifyPdfAgainstSummary_HasRequestSizeLimit_AndNoDisableRequestSizeLimit()
        {
            var method = typeof(DailyController).GetMethod(nameof(DailyController.VerifyPdfAgainstSummary));
            Assert.NotNull(method);

            var disableAttr = method.GetCustomAttribute<DisableRequestSizeLimitAttribute>();
            Assert.Null(disableAttr);

            var limitAttr = method.GetCustomAttribute<RequestSizeLimitAttribute>();
            Assert.NotNull(limitAttr);

            var cad = method.CustomAttributes.First(a => a.AttributeType == typeof(RequestSizeLimitAttribute));
            var limitValue = Convert.ToInt64(cad.ConstructorArguments[0].Value);
            Assert.Equal(52428800L, limitValue);
        }

        [Fact]
        public void OnlyPermittedEndpoints_HaveAllowAnonymous()
        {
            var controllerTypes = typeof(BaseApiController).Assembly.GetTypes()
                .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

            foreach (var controller in controllerTypes)
            {
                var methods = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                foreach (var method in methods)
                {
                    var allowAnon = method.GetCustomAttribute<AllowAnonymousAttribute>();
                    if (allowAnon != null)
                    {
                        // The ONLY permitted anonymous endpoints are:
                        // 1. AccountController.Login
                        // 2. AccountController.GetDatabases
                        // 3. FallbackController.Index
                        var permitted = (controller.Name == nameof(AccountController) && (method.Name == "Login" || method.Name == "GetDatabases"))
                                     || (controller.Name == nameof(FallbackController) && method.Name == "Index");

                        Assert.True(permitted, $"Endpoint '{controller.Name}.{method.Name}' has [AllowAnonymous] but is not in the whitelist of permitted anonymous endpoints.");
                    }
                }
            }
        }
    }
}
