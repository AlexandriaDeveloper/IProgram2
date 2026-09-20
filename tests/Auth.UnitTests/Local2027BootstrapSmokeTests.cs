using System;
using System.Linq;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Auth.Infrastructure.Sync;
using Core.Models;
using Core.Models.Sync;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Auth.UnitTests
{
    [Collection("Local2027Bootstrap")]
    public class Local2027BootstrapSmokeTests
    {
        private const string Local2027ConnectionString =
            "Server=localhost;Database=IProgramLocalDb2027;Trusted_Connection=True;TrustServerCertificate=True;";

        private bool IsLocalDatabaseAvailable()
        {
            try
            {
                var options = new DbContextOptionsBuilder<ApplicationContext>()
                    .UseSqlServer(Local2027ConnectionString, o => o.UseCompatibilityLevel(120))
                    .Options;
                using var context = new ApplicationContext(options);
                return context.Database.CanConnect();
            }
            catch
            {
                return false;
            }
        }

        private void EnsureLocalDatabaseAvailable()
        {
            if (!IsLocalDatabaseAvailable())
            {
                Assert.Fail("Local database IProgramLocalDb2027 is unavailable on localhost. Generic CI must exclude this test using --filter \"Category!=LocalDbRequired\".");
            }
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public async Task Gate11_ApplicationContext_CanConnectAndQuery_Local2027()
        {
            EnsureLocalDatabaseAvailable();

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(Local2027ConnectionString, o => o.UseCompatibilityLevel(120))
                .Options;

            using var context = new ApplicationContext(options);
            Assert.True(await context.Database.CanConnectAsync());

            var count = await context.Employees.CountAsync();
            Assert.True(count > 0, "Local 2027 Employees table must contain rows");
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public async Task Gate11_IdentityTables_CanBeReadFromLocal2027()
        {
            EnsureLocalDatabaseAvailable();

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(Local2027ConnectionString, o => o.UseCompatibilityLevel(120))
                .Options;

            using var context = new ApplicationContext(options);
            var users = await context.Users.AsNoTracking().ToListAsync();
            Assert.NotNull(users);
            Assert.NotEmpty(users);

            var roles = await context.Roles.AsNoTracking().ToListAsync();
            Assert.NotNull(roles);
            Assert.NotEmpty(roles);
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public async Task Gate11_RepresentativeBusinessEntities_CanBeQueried()
        {
            EnsureLocalDatabaseAvailable();

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(Local2027ConnectionString, o => o.UseCompatibilityLevel(120))
                .Options;

            using var context = new ApplicationContext(options);

            var empCount = await context.Employees.AsNoTracking().CountAsync();
            var dailyCount = await context.Set<Daily>().AsNoTracking().CountAsync();
            var formCount = await context.Set<Form>().AsNoTracking().CountAsync();
            var formDetailsCount = await context.Set<FormDetails>().AsNoTracking().CountAsync();
            var deptCount = await context.Departments.AsNoTracking().CountAsync();
            var netPayCount = await context.EmployeeNetPays.AsNoTracking().CountAsync();

            Assert.True(empCount > 0);
            Assert.True(dailyCount > 0);
            Assert.True(formCount > 0);
            Assert.True(formDetailsCount > 0);
            Assert.True(deptCount > 0);
            Assert.True(netPayCount > 0);
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public async Task Gate11_CommonLinqPatterns_ExecuteOnSQLServer2014()
        {
            EnsureLocalDatabaseAvailable();

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer(Local2027ConnectionString, o => o.UseCompatibilityLevel(120))
                .Options;

            using var context = new ApplicationContext(options);

            // Pattern 1: Projection with Joins/Navigations and Grouping
            var deptSummary = await context.Employees.AsNoTracking()
                .Where(e => e.DepartmentId != null)
                .GroupBy(e => e.DepartmentId)
                .Select(g => new { DepartmentId = g.Key, Count = g.Count() })
                .Take(5)
                .ToListAsync();
            Assert.NotNull(deptSummary);

            // Pattern 2: Subquery / Any / Contains filter
            var topFormIds = await context.Set<Form>().AsNoTracking()
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => f.Id)
                .Take(3)
                .ToListAsync();

            var formDetails = await context.Set<FormDetails>().AsNoTracking()
                .Where(fd => topFormIds.Contains(fd.FormId))
                .Take(10)
                .ToListAsync();
            Assert.NotNull(formDetails);
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public async Task Gate11_LocalSyncContext_CanReadLocalMetadata()
        {
            EnsureLocalDatabaseAvailable();

            var options = new DbContextOptionsBuilder<LocalSyncContext>()
                .UseSqlServer(Local2027ConnectionString, o =>
                {
                    o.UseCompatibilityLevel(120);
                    o.MigrationsHistoryTable(LocalSyncContext.MigrationsHistoryTableName, LocalSyncContext.MigrationsHistoryTableSchema);
                })
                .Options;

            using var context = new LocalSyncContext(options);
            var manifest = await context.BootstrapManifests
                .Where(m => m.DatabaseId == "2027")
                .OrderByDescending(m => m.Id)
                .FirstOrDefaultAsync();

            Assert.NotNull(manifest);
            Assert.Contains(manifest.Status, new[] { "VERIFIED_READY", "REVIEW_HOLD", "QUARANTINED" });
            if (manifest.Status == "VERIFIED_READY")
            {
                Assert.True(manifest.IsWriteAllowed, "When VERIFIED_READY, IsWriteAllowed must be true.");
            }
            else
            {
                Assert.False(manifest.IsWriteAllowed, "When quarantined or in review hold, IsWriteAllowed must be false.");
            }

            var state = await context.LocalStates
                .Where(s => s.DatabaseId == "2027")
                .FirstOrDefaultAsync();
            Assert.NotNull(state);
            Assert.True(state.LastServerVersion >= 0);

            var outboxCount = await context.LocalOutboxes
                .Where(o => o.DatabaseId == "2027")
                .CountAsync();
            Assert.Equal(0, outboxCount);
        }

        [Fact]
        [Trait("Category", "LocalDbRequired")]
        public void Gate11_AzureSyncContext_PhysicalBindingGuard_RejectsLocal2027()
        {
            var options = new DbContextOptionsBuilder<AzureSyncContext>()
                .UseSqlServer(Local2027ConnectionString)
                .Options;

            var ex = Assert.Throws<InvalidOperationException>(() => new AzureSyncContext(options));
            Assert.Contains("Security violation: AzureSyncContext cannot target local", ex.Message);
        }
    }
}
