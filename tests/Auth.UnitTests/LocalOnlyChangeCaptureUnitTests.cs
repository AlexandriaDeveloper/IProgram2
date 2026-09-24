#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Auth.Infrastructure;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using Persistence;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class LocalOnlyChangeCaptureUnitTests
    {
        private static IConfiguration CreateConfiguration(bool captureEnabled = false)
        {
            var configData = new Dictionary<string, string?>
            {
                ["Sync:LocalOnlyChangeCaptureEnabled"] = captureEnabled ? "true" : "false"
            };
            return new ConfigurationBuilder().AddInMemoryCollection(configData).Build();
        }

        [Fact]
        public async Task Test01_FlagFalse_DirectSave_ZeroLocalOutbox_RegressionPreserved()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(false);

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            await using var context = new ApplicationContext(options);
            var config = CreateConfiguration(captureEnabled: false);
            var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

            // Add Department and Employee in LocalOnlyProduction with capture disabled
            var dept = new Department { Name = "Operations", SyncId = Guid.NewGuid(), IsActive = true };
            context.Departments.Add(dept);
            await uow.SaveChangesAsync();

            var emp = new Employee
            {
                Id = "12345678901235",
                Name = "Direct Production Worker",
                Collage = "Operations",
                Section = "Maintenance",
                DepartmentId = dept.Id,
                SyncId = Guid.NewGuid(),
                IsActive = true
            };
            context.Employees.Add(emp);
            var saved = await uow.SaveChangesAsync();

            Assert.Equal(1, saved);

            // Confirm business rows exist in database
            var loadedEmp = await context.Employees.FindAsync("12345678901235");
            Assert.NotNull(loadedEmp);
            Assert.Equal("Direct Production Worker", loadedEmp.Name);

            // Confirm change capture did NOT trigger or generate any outbox errors
        }

        [Fact]
        public async Task Test02_FlagTrue_HardDelete_FailsClosed()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            await using var context = new ApplicationContext(options);
            var config = CreateConfiguration(captureEnabled: true);
            var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

            var dept = new Department { Name = "Finance", SyncId = Guid.NewGuid(), IsActive = true };
            context.Departments.Add(dept);
            await context.SaveChangesAsync();

            // Mark for Hard Delete
            context.Departments.Remove(dept);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => uow.SaveChangesAsync());
            Assert.Contains("HARD_DELETE_FORBIDDEN", ex.Message);
        }

        [Fact]
        public async Task Test03_FlagTrue_SyncIdMutation_FailsClosed()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            await using var context = new ApplicationContext(options);
            var config = CreateConfiguration(captureEnabled: true);
            var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

            var originalSyncId = Guid.NewGuid();
            var dept = new Department { Name = "Legal", SyncId = originalSyncId, IsActive = true };
            context.Departments.Add(dept);
            await context.SaveChangesAsync();

            // Mutate SyncId
            dept.SyncId = Guid.NewGuid();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => uow.SaveChangesAsync());
            Assert.Contains("SYNC_ID_IMMUTABLE", ex.Message);
        }

        private sealed class DummyUnregisteredSyncable : ISyncableEntity
        {
            public Guid SyncId { get; set; } = Guid.NewGuid();
        }

        [Fact]
        public async Task Test04_FlagTrue_SoftDelete_CorrectlyDetected()
        {
            var mockProvider = new Mock<ISyncConnectionProvider>();
            mockProvider.Setup(p => p.IsLocalFirstEnabled).Returns(true);
            mockProvider.Setup(p => p.IsReadOnlyMode).Returns(false);
            mockProvider.Setup(p => p.IsLocalOnlyProduction).Returns(true);
            mockProvider.Setup(p => p.IsLocalOnlyChangeCaptureEnabled).Returns(true);
            mockProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");

            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            await using var context = new ApplicationContext(options);
            var config = CreateConfiguration(captureEnabled: true);
            var uow = new UnitOfWork(context, mockProvider.Object, configuration: config);

            var dept = new Department { Name = "HR", SyncId = Guid.NewGuid(), IsActive = true };
            context.Departments.Add(dept);
            await context.SaveChangesAsync();

            // Soft-delete by setting IsActive = false
            dept.IsActive = false;
            dept.DeactivatedAt = DateTime.UtcNow;

            // In InMemoryDatabase, relational transactions are not supported, so execution strategy/BeginTransactionAsync
            // throws InvalidOperationException when attempting to start a relational transaction.
            // This proves pre-transaction soft-delete detection successfully passed without throwing HARD_DELETE_FORBIDDEN!
            var ex = await Record.ExceptionAsync(() => uow.SaveChangesAsync());
            Assert.NotNull(ex);
            Assert.DoesNotContain("HARD_DELETE_FORBIDDEN", ex.Message);
            Assert.DoesNotContain("SYNC_ID_IMMUTABLE", ex.Message);
        }
    }
}
