using System;
using System.IO;
using Xunit;

namespace Auth.UnitTests
{
    public class SpaEnvironmentConfigurationTests
    {
        private static string GetRepositoryRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "IProgram.sln")))
            {
                dir = dir.Parent;
            }
            return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root containing IProgram.sln");
        }

        [Fact]
        public void EnvironmentProd_MustUseRelativeApiUrl_NotHardcodedPortOrHost()
        {
            var repoRoot = GetRepositoryRoot();
            var prodEnvPath = Path.Combine(repoRoot, "Client", "src", "app", "environment.prod.ts");

            Assert.True(File.Exists(prodEnvPath), $"Expected environment.prod.ts to exist at {prodEnvPath}");

            var content = File.ReadAllText(prodEnvPath);

            // Must use relative path for API so that SPA served by Kestrel on port 5000, 443, 80, or Azure resolves to the host's actual port
            Assert.Contains("apiUrl: '/api/'", content);
            Assert.Contains("apiContent: '/'", content);

            // Must NOT hardcode port 80 or any absolute localhost URL in production environment
            Assert.DoesNotContain("http://localhost/api/", content);
            Assert.DoesNotContain("http://localhost/", content);
        }

        [Fact]
        public void LoginComponent_Template_HasDatabaseSelector_BoundToDatabases()
        {
            var repoRoot = GetRepositoryRoot();
            var loginHtmlPath = Path.Combine(repoRoot, "Client", "src", "app", "account", "login", "login.component.html");
            var loginTsPath = Path.Combine(repoRoot, "Client", "src", "app", "account", "login", "login.component.ts");

            Assert.True(File.Exists(loginHtmlPath), $"Expected login.component.html to exist at {loginHtmlPath}");
            Assert.True(File.Exists(loginTsPath), $"Expected login.component.ts to exist at {loginTsPath}");

            var htmlContent = File.ReadAllText(loginHtmlPath);
            var tsContent = File.ReadAllText(loginTsPath);

            // HTML must bind formControlName="database" and iterate over databases
            Assert.Contains("formControlName=\"database\"", htmlContent);
            Assert.Contains("databases", htmlContent);

            // TS must call getDatabases and assign to databases
            Assert.Contains("getDatabases()", tsContent);
            Assert.Contains("this.databases = res", tsContent);
        }
    }
}
