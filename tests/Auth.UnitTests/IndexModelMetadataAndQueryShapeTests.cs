using System;
using System.Linq;
using Auth.Infrastructure;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Auth.UnitTests
{
    public class IndexModelMetadataAndQueryShapeTests
    {
        private ApplicationContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationContext>()
                .UseSqlServer("Server=localhost;Database=DummyDb;Trusted_Connection=True;TrustServerCertificate=True;")
                .Options;

            return new ApplicationContext(options);
        }

        [Fact]
        public void Form_ModelMetadata_ContainsExpectedCompositeIndexes_AndNoRedundantStandaloneIndex()
        {
            using var context = CreateContext();
            var formEntityType = context.Model.FindEntityType(typeof(Form));
            Assert.NotNull(formEntityType);

            var indexes = formEntityType.GetIndexes().ToList();

            // 1. Verify IX_Form_DailyId_IsActive_Index exists with exact column ordering
            var dailyIdComposite = indexes.FirstOrDefault(i => i.GetDatabaseName() == "IX_Form_DailyId_IsActive_Index");
            Assert.NotNull(dailyIdComposite);
            var dailyIdCols = dailyIdComposite.Properties.Select(p => p.Name).ToList();
            Assert.Equal(new[] { "DailyId", "IsActive", "Index" }, dailyIdCols);

            // 2. Verify IX_Form_IsActive_CreatedAt exists with exact column ordering
            var activeCreatedAtComposite = indexes.FirstOrDefault(i => i.GetDatabaseName() == "IX_Form_IsActive_CreatedAt");
            Assert.NotNull(activeCreatedAtComposite);
            var activeCreatedAtCols = activeCreatedAtComposite.Properties.Select(p => p.Name).ToList();
            Assert.Equal(new[] { "IsActive", "CreatedAt" }, activeCreatedAtCols);

            // 3. Verify redundant standalone IX_Form_DailyId was replaced and does NOT exist
            var standaloneDailyId = indexes.FirstOrDefault(i =>
                i.GetDatabaseName() == "IX_Form_DailyId" ||
                (i.Properties.Count == 1 && i.Properties[0].Name == "DailyId"));
            Assert.Null(standaloneDailyId);
        }

        [Fact]
        public void FormDetails_ModelMetadata_ContainsExpectedCompositeIndex_AndNoRedundantStandaloneIndex()
        {
            using var context = CreateContext();
            var formDetailsEntityType = context.Model.FindEntityType(typeof(FormDetails));
            Assert.NotNull(formDetailsEntityType);

            var indexes = formDetailsEntityType.GetIndexes().ToList();

            // 1. Verify IX_FormDetails_FormId_IsActive_EmployeeId exists with exact column ordering
            var formIdComposite = indexes.FirstOrDefault(i => i.GetDatabaseName() == "IX_FormDetails_FormId_IsActive_EmployeeId");
            Assert.NotNull(formIdComposite);
            var formIdCols = formIdComposite.Properties.Select(p => p.Name).ToList();
            Assert.Equal(new[] { "FormId", "IsActive", "EmployeeId" }, formIdCols);

            // 2. Verify SqlServer:Include includes Amount and OrderNum via IDesignTimeModel
            var designTimeModel = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>(context).Model;
            var dtFormDetails = designTimeModel.FindEntityType(typeof(FormDetails));
            Assert.NotNull(dtFormDetails);
            var dtIndex = dtFormDetails.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_FormDetails_FormId_IsActive_EmployeeId");
            Assert.NotNull(dtIndex);
            var includeProps = SqlServerIndexExtensions.GetIncludeProperties(dtIndex);
            Assert.NotNull(includeProps);
            Assert.Contains("Amount", includeProps);
            Assert.Contains("OrderNum", includeProps);

            // 3. Verify redundant standalone IX_FormDetails_FormId was replaced and does NOT exist
            var standaloneFormId = indexes.FirstOrDefault(i =>
                i.GetDatabaseName() == "IX_FormDetails_FormId" ||
                (i.Properties.Count == 1 && i.Properties[0].Name == "FormId"));
            Assert.Null(standaloneFormId);
        }

        [Fact]
        public void FormList_WithinDaily_QueryShape_Matches_IX_Form_DailyId_IsActive_Index()
        {
            using var context = CreateContext();
            var targetDailyId = 42;

            // Query shape matching FormSpecification inside Daily (DailyId == id, IsActive == true, ORDER BY Index DESC)
            var query = context.Set<Form>()
                .Where(f => f.DailyId == targetDailyId && f.IsActive)
                .OrderByDescending(f => f.Index);

            var sql = query.ToQueryString();

            // Evidence that SQL Server predicate and sort align with (DailyId, IsActive, Index)
            Assert.Contains("[f].[DailyId] = @__targetDailyId_0", sql);
            Assert.Contains("[f].[IsActive] = CAST(1 AS bit)", sql);
            Assert.Contains("ORDER BY [f].[Index] DESC", sql);
        }

        [Fact]
        public void Dashboard_QueryShape_Matches_IX_Form_IsActive_CreatedAt_And_FormDetailsComposite()
        {
            using var context = CreateContext();
            var startDate = new DateTime(2026, 1, 1);
            var endDate = new DateTime(2026, 1, 31);

            // Dashboard query shape: range seek on Form (IsActive, CreatedAt) joining FormDetails (FormId, IsActive)
            var query = context.Set<Form>()
                .Where(f => f.IsActive && f.CreatedAt >= startDate && f.CreatedAt <= endDate)
                .SelectMany(f => f.FormDetails)
                .Where(fd => fd.IsActive)
                .Select(fd => new { fd.EmployeeId, fd.Amount });

            var sql = query.ToQueryString();

            // Evidence that SQL Server predicate aligns with (IsActive, CreatedAt) on Form
            Assert.Contains("[f].[IsActive] = CAST(1 AS bit)", sql);
            Assert.Contains("[f].[CreatedAt] >= @__startDate_0", sql);
            Assert.Contains("[f].[CreatedAt] <= @__endDate_1", sql);

            // Evidence that join and filter align with (FormId, IsActive) on FormDetails
            Assert.Contains("[f].[Id] = [f0].[FormId]", sql);
            Assert.Contains("[f0].[IsActive] = CAST(1 AS bit)", sql);
        }
    }
}
