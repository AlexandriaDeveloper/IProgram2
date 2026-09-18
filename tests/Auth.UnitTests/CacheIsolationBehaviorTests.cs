using System;
using Application.Dtos;
using Auth.Infrastructure.Services;
using Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class CacheIsolationBehaviorTests
    {
        private readonly IMemoryCache _memoryCache;

        public CacheIsolationBehaviorTests()
        {
            _memoryCache = new MemoryCache(new MemoryCacheOptions());
        }

        [Fact]
        public void Cache_Isolation_Prevents_Cross_Database_Data_Leak()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            int formId = 100;
            var form2026 = new FormDto { Id = formId, Name = "استمارة ميزانية 2026" };

            // Act 1: User in Database 2026 fetches and caches Form 100
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var key2026 = keyFactory.GetFormDetailsKey(formId);
            _memoryCache.Set(key2026, form2026);

            // Act 2: User switches to Database 2027 and requests Form 100
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");
            var key2027 = keyFactory.GetFormDetailsKey(formId);
            var existsIn2027 = _memoryCache.TryGetValue(key2027, out FormDto? cachedIn2027);

            // Assert: Database 2027 must NOT see Database 2026's cached data!
            Assert.False(existsIn2027);
            Assert.Null(cachedIn2027);

            // Verify Database 2026 still has its data intact
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var existsIn2026 = _memoryCache.TryGetValue(keyFactory.GetFormDetailsKey(formId), out FormDto? cachedIn2026);
            Assert.True(existsIn2026);
            Assert.Equal("استمارة ميزانية 2026", cachedIn2026!.Name);
        }

        [Fact]
        public void Invalidation_In_One_Database_Does_Not_Affect_Other_Databases()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            int formId = 100;
            var form2026 = new FormDto { Id = formId, Name = "استمارة 2026" };
            var form2027 = new FormDto { Id = formId, Name = "استمارة 2027" };

            // Cache data in 2026
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var key2026 = keyFactory.GetFormDetailsKey(formId);
            _memoryCache.Set(key2026, form2026);

            // Cache data in 2027
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");
            var key2027 = keyFactory.GetFormDetailsKey(formId);
            _memoryCache.Set(key2027, form2027);

            // Verify both are initially cached
            Assert.True(_memoryCache.TryGetValue(key2026, out _));
            Assert.True(_memoryCache.TryGetValue(key2027, out _));

            // Act: Invalidate/Clear cache in Database 2026
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            _memoryCache.Remove(keyFactory.GetFormDetailsKey(formId));

            // Assert: 2026 is invalidated, but 2027 remains CACHED!
            Assert.False(_memoryCache.TryGetValue(key2026, out _));
            Assert.True(_memoryCache.TryGetValue(key2027, out FormDto? remaining2027));
            Assert.NotNull(remaining2027);
            Assert.Equal("استمارة 2027", remaining2027.Name);
        }

        [Fact]
        public void No_Regression_In_Single_Database_Caching_Behavior()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            int formId = 250;
            var expectedForm = new FormDto { Id = formId, Name = "استمارة تجريبية" };

            // 1. Initial State: Cache miss
            var key = keyFactory.GetFormDetailsKey(formId);
            Assert.False(_memoryCache.TryGetValue(key, out _));

            // 2. Set cache
            _memoryCache.Set(key, expectedForm, TimeSpan.FromMinutes(10));

            // 3. Cache hit
            var hit = _memoryCache.TryGetValue(key, out FormDto? actual);
            Assert.True(hit);
            Assert.Same(expectedForm, actual);

            // 4. Invalidation clears cache
            _memoryCache.Remove(keyFactory.GetFormDetailsKey(formId));
            Assert.False(_memoryCache.TryGetValue(key, out _));
        }

        [Fact]
        public void Department_Cache_Keys_Are_Isolated_Per_Database()
        {
            // Arrange
            var mockDbProvider = new Mock<IDbConnectionProvider>();
            var keyFactory = new DbCacheKeyFactory(mockDbProvider.Object);

            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2026");
            var key2026 = keyFactory.CreateKey("departments_all");

            mockDbProvider.Setup(p => p.GetSelectedDatabaseId()).Returns("2027");
            var key2027 = keyFactory.CreateKey("departments_all");

            _memoryCache.Set(key2026, "DeptList2026");
            _memoryCache.Set(key2027, "DeptList2027");

            // Invalidate 2026
            _memoryCache.Remove(key2026);

            // Assert
            Assert.False(_memoryCache.TryGetValue(key2026, out _));
            Assert.True(_memoryCache.TryGetValue(key2027, out string? dept2027));
            Assert.Equal("DeptList2027", dept2027);
        }
    }
}
