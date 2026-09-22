#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auth.Infrastructure.Sync.Pull;
using Core.Models;
using Core.Exceptions;
using Core.Models.Sync;
using Persistence.Repository;
using Xunit;

namespace Auth.UnitTests
{
    public class FormsSyncTests
    {
        [Fact]
        public void EnvelopeSerialization_Form_IncludesDailySyncId_AndProducesDeterministicJson()
        {
            var formSyncId = Guid.Parse("11111111-1111-1111-1111-111111111111");
            var dailySyncId = Guid.Parse("22222222-2222-2222-2222-222222222222");
            var deviceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
            var timestamp = new DateTime(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

            var form = new Form
            {
                Id = 101,
                SyncId = formSyncId,
                DailyId = 42,
                Name = "Test Form",
                Description = "Form Description",
                Index = 1,
                IsActive = true,
                CreatedAt = timestamp,
                CreatedBy = "user1"
            };

            var json1 = UnitOfWork.BuildDeterministicFormPayloadJson(
                "INSERT", "2026", deviceId, 5, form, dailySyncId, timestamp);
            var json2 = UnitOfWork.BuildDeterministicFormPayloadJson(
                "INSERT", "2026", deviceId, 5, form, dailySyncId, timestamp);

            Assert.Equal(json1, json2);

            using var doc = JsonDocument.Parse(json1);
            var root = doc.RootElement;
            Assert.Equal("Form", root.GetProperty("entityType").GetString());
            Assert.Equal("INSERT", root.GetProperty("operationType").GetString());
            Assert.Equal(formSyncId.ToString(), root.GetProperty("entitySyncId").GetString());
            Assert.Equal(5, root.GetProperty("baseServerVersion").GetInt64());

            var entityData = root.GetProperty("entityData");
            Assert.Equal(dailySyncId.ToString(), entityData.GetProperty("DailySyncId").GetString());
            Assert.Equal("Test Form", entityData.GetProperty("Name").GetString());
            Assert.True(entityData.GetProperty("IsActive").GetBoolean());
        }

        [Fact]
        public void EnvelopeSerialization_ArchiveForm_HandlesNullDailySyncId()
        {
            var formSyncId = Guid.Parse("44444444-4444-4444-4444-444444444444");
            var deviceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
            var timestamp = new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);

            var archiveForm = new Form
            {
                Id = 202,
                SyncId = formSyncId,
                DailyId = null,
                Name = "Archived Form",
                Description = null,
                Index = 2,
                IsActive = true,
                CreatedAt = timestamp,
                CreatedBy = "archiver"
            };

            var json = UnitOfWork.BuildDeterministicFormPayloadJson(
                "INSERT", "2026", deviceId, 10, archiveForm, null, timestamp);

            using var doc = JsonDocument.Parse(json);
            var entityData = doc.RootElement.GetProperty("entityData");
            Assert.Equal(JsonValueKind.Null, entityData.GetProperty("DailySyncId").ValueKind);
            Assert.Equal("Archived Form", entityData.GetProperty("Name").GetString());
        }

        [Fact]
        public void EnvelopeSerialization_FormDetails_IncludesFormSyncId_AndProducesDeterministicJson()
        {
            var detailsSyncId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            var formSyncId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            var deviceId = Guid.Parse("88888888-8888-8888-8888-888888888888");
            var timestamp = new DateTime(2026, 9, 23, 14, 0, 0, DateTimeKind.Utc);

            var details = new FormDetails
            {
                Id = 303,
                SyncId = detailsSyncId,
                FormId = 101,
                EmployeeId = "12345678901234",
                Amount = 1500.50,
                OrderNum = 1,
                IsActive = true,
                CreatedAt = timestamp,
                CreatedBy = "user1"
            };

            var json1 = UnitOfWork.BuildDeterministicFormDetailsPayloadJson(
                "UPDATE", "2026", deviceId, 15, details, formSyncId, timestamp);
            var json2 = UnitOfWork.BuildDeterministicFormDetailsPayloadJson(
                "UPDATE", "2026", deviceId, 15, details, formSyncId, timestamp);

            Assert.Equal(json1, json2);

            using var doc = JsonDocument.Parse(json1);
            var root = doc.RootElement;
            Assert.Equal("FormDetails", root.GetProperty("entityType").GetString());
            Assert.Equal("UPDATE", root.GetProperty("operationType").GetString());

            var entityData = root.GetProperty("entityData");
            Assert.Equal(formSyncId.ToString(), entityData.GetProperty("FormSyncId").GetString());
            Assert.Equal("12345678901234", entityData.GetProperty("EmployeeId").GetString());
            Assert.Equal(1500.50, entityData.GetProperty("Amount").GetDouble());
        }

        [Fact]
        public void DeterministicPayload_ProducesIdenticalSha256Hash()
        {
            var form = new Form
            {
                Id = 1,
                SyncId = Guid.NewGuid(),
                Name = "Deterministic Test",
                Index = 0,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = "admin"
            };

            var jsonA = UnitOfWork.BuildDeterministicFormPayloadJson("INSERT", "2026", Guid.NewGuid(), 1, form, null, DateTime.UtcNow);
            var jsonB = jsonA;

            using var sha = SHA256.Create();
            var hashA = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(jsonA)));
            var hashB = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(jsonB)));

            Assert.Equal(hashA, hashB);
        }

        [Fact]
        public void TopologicalOrdering_GetIngestionOrder_UpsertsInRootFirstOrder()
        {
            var dailyCmd = new PullCommand { CommandType = PullCommandType.Upsert, EntityType = "Daily" };
            var formCmd = new PullCommand { CommandType = PullCommandType.Upsert, EntityType = "Form" };
            var detailsCmd = new PullCommand { CommandType = PullCommandType.Upsert, EntityType = "FormDetails" };
            var refCmd = new PullCommand { CommandType = PullCommandType.Upsert, EntityType = "FormRefernce" };

            var orderDaily = LocalPullTransactionCoordinator.GetIngestionOrder(dailyCmd);
            var orderForm = LocalPullTransactionCoordinator.GetIngestionOrder(formCmd);
            var orderDetails = LocalPullTransactionCoordinator.GetIngestionOrder(detailsCmd);
            var orderRef = LocalPullTransactionCoordinator.GetIngestionOrder(refCmd);

            Assert.True(orderDaily < orderForm, "Daily must ingest before Form");
            Assert.True(orderForm < orderDetails, "Form must ingest before FormDetails");
            Assert.True(orderDetails < orderRef, "FormDetails must ingest before FormRefernce");
        }

        [Fact]
        public void TopologicalOrdering_GetIngestionOrder_DeletesInLeafFirstOrder()
        {
            var dailyCmd = new PullCommand { CommandType = PullCommandType.Delete, EntityType = "Daily" };
            var formCmd = new PullCommand { CommandType = PullCommandType.Delete, EntityType = "Form" };
            var detailsCmd = new PullCommand { CommandType = PullCommandType.Delete, EntityType = "FormDetails" };
            var refCmd = new PullCommand { CommandType = PullCommandType.Delete, EntityType = "FormRefernce" };

            var orderDaily = LocalPullTransactionCoordinator.GetIngestionOrder(dailyCmd);
            var orderForm = LocalPullTransactionCoordinator.GetIngestionOrder(formCmd);
            var orderDetails = LocalPullTransactionCoordinator.GetIngestionOrder(detailsCmd);
            var orderRef = LocalPullTransactionCoordinator.GetIngestionOrder(refCmd);

            Assert.True(orderRef < orderDetails, "FormRefernce must delete before FormDetails");
            Assert.True(orderDetails < orderForm, "FormDetails must delete before Form");
            Assert.True(orderForm < orderDaily, "Form must delete before Daily");
        }

        [Fact]
        public void PullCommand_CreateFormUpsert_PopulatesExpectedProperties()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new FormAuthoritativeSnapshot
            {
                SyncId = syncId,
                Name = "Test",
                IsActive = true
            };

            var item = PullCommand.CreateFormUpsert(snapshot, 42);

            Assert.Equal("Form", item.EntityType);
            Assert.Equal(PullCommandType.Upsert, item.CommandType);
            Assert.Equal(syncId, item.EntitySyncId);
            Assert.Equal(42, item.TerminalServerVersion);
            Assert.Same(snapshot, item.FormSnapshot);
            Assert.Null(item.FormDetailsSnapshot);
            Assert.Null(item.FormRefernceSnapshot);
            Assert.Null(item.Snapshot);
        }

        [Fact]
        public void PullCommand_CreateFormDetailsUpsert_PopulatesExpectedProperties()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new FormDetailsAuthoritativeSnapshot
            {
                SyncId = syncId,
                EmployeeId = "12345678901234",
                Amount = 100.0,
                IsActive = true
            };

            var item = PullCommand.CreateFormDetailsUpsert(snapshot, 43);

            Assert.Equal("FormDetails", item.EntityType);
            Assert.Equal(PullCommandType.Upsert, item.CommandType);
            Assert.Equal(syncId, item.EntitySyncId);
            Assert.Equal(43, item.TerminalServerVersion);
            Assert.Same(snapshot, item.FormDetailsSnapshot);
        }

        [Fact]
        public void PullCommand_CreateFormRefernceUpsert_PopulatesExpectedProperties()
        {
            var syncId = Guid.NewGuid();
            var snapshot = new FormRefernceAuthoritativeSnapshot
            {
                SyncId = syncId,
                ReferencePath = "Content/FormReferences/ref.pdf",
                IsActive = true
            };

            var item = PullCommand.CreateFormRefernceUpsert(snapshot, 44);

            Assert.Equal("FormRefernce", item.EntityType);
            Assert.Equal(PullCommandType.Upsert, item.CommandType);
            Assert.Equal(syncId, item.EntitySyncId);
            Assert.Equal(44, item.TerminalServerVersion);
            Assert.Same(snapshot, item.FormRefernceSnapshot);
        }

        [Fact]
        public void PullCommand_CreateDelete_PopulatesExpectedProperties()
        {
            var syncId = Guid.NewGuid();
            var item = PullCommand.CreateDelete("FormDetails", syncId, 50);

            Assert.Equal("FormDetails", item.EntityType);
            Assert.Equal(PullCommandType.Delete, item.CommandType);
            Assert.Equal(syncId, item.EntitySyncId);
            Assert.Equal(50, item.TerminalServerVersion);
            Assert.Null(item.FormDetailsSnapshot);
        }

        [Fact]
        public void ConflictRiskException_ThrowsExpectedCode_WhenConflictDetected()
        {
            var ex = new SyncConflictRiskException(10, 15, 2);
            Assert.Equal("BOTH_CHANGED_CONFLICT_RISK", ex.ErrorCode);
            Assert.Equal(10, ex.LocalVersion);
            Assert.Equal(15, ex.ServerVersion);
            Assert.Equal(2, ex.PendingOutboxCount);
        }

        [Fact]
        public void PullForeignKeyResolutionException_ThrowsExpectedCode_WhenResolutionFails()
        {
            var ex = new SyncPullForeignKeyResolutionException("تعذر العثور على المفتاح الأجنبي FormSyncId محلياً.");
            Assert.Equal("PULL_FOREIGN_KEY_RESOLUTION_FAILED", ex.ErrorCode);
        }
    }
}
