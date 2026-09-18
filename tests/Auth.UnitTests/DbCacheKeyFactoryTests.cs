using System;
using Auth.Infrastructure.Services;
using Core.Interfaces;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class DbCacheKeyFactoryTests
    {
        [Fact]
        public void Same_FormId_In_Different_DatabaseId_Produces_Different_CacheKeys()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            var factory = new DbCacheKeyFactory(mockDbProvider.Object);
            int formId = 100;

            // Act: Scope to 2026
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var key2026 = factory.GetFormDetailsKey(formId);

            // Act: Scope to 2027
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");
            var key2027 = factory.GetFormDetailsKey(formId);

            // Assert
            Assert.Equal("2026:FormDetails:100", key2026);
            Assert.Equal("2027:FormDetails:100", key2027);
            Assert.NotEqual(key2026, key2027);
        }

        [Fact]
        public void Same_DatabaseId_And_Same_FormId_Produces_Identical_CacheKeys()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var factory = new DbCacheKeyFactory(mockDbProvider.Object);
            int formId = 100;

            // Act
            var key1 = factory.GetFormDetailsKey(formId);
            var key2 = factory.GetFormDetailsKey(formId);

            // Assert
            Assert.Equal("2026:FormDetails:100", key1);
            Assert.Equal(key1, key2);
        }

        [Fact]
        public void General_Key_Is_Properly_Scoped_To_Database()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            var factory = new DbCacheKeyFactory(mockDbProvider.Object);

            // Act 2026
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var key2026 = factory.CreateKey("departments_all");

            // Act 2027
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");
            var key2027 = factory.CreateKey("departments_all");

            // Assert
            Assert.Equal("2026:departments_all", key2026);
            Assert.Equal("2027:departments_all", key2027);
            Assert.NotEqual(key2026, key2027);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Fails_Closed_When_DatabaseId_Is_Empty_Or_Null(string? dbId)
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns(dbId!);
            var factory = new DbCacheKeyFactory(mockDbProvider.Object);

            // Act & Assert: Must fail closed instead of silently falling back to 'default'
            Assert.Throws<InvalidOperationException>(() => factory.GetFormDetailsKey(50));
        }

        [Fact]
        public void Throws_ArgumentException_On_Invalid_Inputs()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var factory = new DbCacheKeyFactory(mockDbProvider.Object);

            // Assert
            Assert.Throws<ArgumentException>(() => factory.CreateKey("", 123));
            Assert.Throws<ArgumentNullException>(() => factory.CreateKey("Form", null!));
            Assert.Throws<ArgumentException>(() => factory.CreateKey(""));
        }
    }
}
