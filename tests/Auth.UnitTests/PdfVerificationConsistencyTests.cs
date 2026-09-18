using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Features;
using Application.Helpers;
using Application.Interfaces;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class PdfVerificationConsistencyTests
    {
        private readonly Mock<PayrollPdfParserService> _pdfParserMock;
        private readonly Mock<DailyService> _dailyServiceMock;
        private readonly Mock<IUnitOfWork> _uowMock;
        private readonly Mock<IGenericRepository<EmployeeNetPay>> _netPayRepoMock;
        private readonly Mock<IGenericRepository<FormDetails>> _formDetailsRepoMock;
        private readonly Mock<ILogger<PdfVerificationService>> _loggerMock;
        private readonly Mock<IDailyClosureGuard> _closureGuardMock;
        private readonly Mock<IPdfVerificationDocumentRenderer> _rendererMock;

        private readonly List<EmployeeNetPay> _inMemoryNetPays;
        private readonly List<FormDetails> _inMemoryFormDetails;

        public PdfVerificationConsistencyTests()
        {
            _pdfParserMock = new Mock<PayrollPdfParserService>();
            _dailyServiceMock = new Mock<DailyService>();
            _uowMock = new Mock<IUnitOfWork>();
            _netPayRepoMock = new Mock<IGenericRepository<EmployeeNetPay>>();
            _formDetailsRepoMock = new Mock<IGenericRepository<FormDetails>>();
            _loggerMock = new Mock<ILogger<PdfVerificationService>>();
            _closureGuardMock = new Mock<IDailyClosureGuard>();
            _rendererMock = new Mock<IPdfVerificationDocumentRenderer>();

            _inMemoryNetPays = new List<EmployeeNetPay>();
            _inMemoryFormDetails = new List<FormDetails>();

            _netPayRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(() => _inMemoryNetPays.AsAsyncQueryable());

            _netPayRepoMock.Setup(r => r.Insert(It.IsAny<EmployeeNetPay>()))
                .Callback<EmployeeNetPay>(entity => _inMemoryNetPays.Add(entity))
                .Returns(Task.CompletedTask);

            _formDetailsRepoMock.Setup(r => r.GetQueryable(It.IsAny<bool?>()))
                .Returns(() => _inMemoryFormDetails.AsAsyncQueryable());

            // Default closure guard: daily is open
            _closureGuardMock.Setup(g => g.EnsureDailyOpenAsync(It.IsAny<int>()))
                .ReturnsAsync(Result.Success());

            // Default renderer: returns dummy PDF bytes
            _rendererMock.Setup(r => r.RenderAnnotatedPdf(It.IsAny<byte[]>(), It.IsAny<List<PdfAnnotation>>()))
                .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 }); // %PDF-1.4

            _uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(1);
        }

        private PdfVerificationService CreateService()
        {
            return new PdfVerificationService(
                _pdfParserMock.Object,
                _dailyServiceMock.Object,
                _uowMock.Object,
                _netPayRepoMock.Object,
                _formDetailsRepoMock.Object,
                _loggerMock.Object,
                _closureGuardMock.Object,
                _rendererMock.Object);
        }

        [Fact]
        public async Task SuccessfulVerification_PersistsFormDetailsAndNetPay_WithSingleSaveChangesAsync_AndReturnsReports()
        {
            // Arrange
            int dailyId = 100;
            string userId = "user-123";
            string nationalId = "29001011234567";

            var formDetail = new FormDetails
            {
                Id = 501,
                FormId = 1,
                EmployeeId = nationalId,
                Amount = 2500.0,
                IsActive = true,
                IsSummaryReviewed = false
            };
            _inMemoryFormDetails.Add(formDetail);

            var summary = new DailyBeneficiarySummaryResponse
            {
                DailyName = "يومية الرواتب",
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = nationalId,
                        EmployeeName = "محمد أحمد",
                        TegaraCode = "1001",
                        TotalAmount = 2500.0,
                        Details = new List<BeneficiaryDetailDto>
                        {
                            new BeneficiaryDetailDto
                            {
                                FormDetailId = 501,
                                FormId = 1,
                                FormName = "استمارة 1",
                                Amount = 2500.0,
                                IsSummaryReviewed = false
                            }
                        }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            var pdfRecords = new List<PdfEmployeeRecord>
            {
                new PdfEmployeeRecord
                {
                    NationalId = nationalId,
                    Name = "محمد أحمد",
                    TegaraCode = "1001",
                    TotalEntitlements = 2500.0,
                    NetPay = 2100.0,
                    PageNumber = 1,
                    BoundingBoxBottom = 100,
                    BoundingBoxTop = 120
                }
            };
            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(pdfRecords);

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, userId);

            // Assert
            Assert.True(result.IsSuccess);
            Assert.NotNull(result.Value);
            Assert.True(result.Value.Success);
            Assert.Equal(1, result.Value.MatchedCount);
            Assert.Equal(0, result.Value.ErrorCount);
            Assert.NotNull(result.Value.ReportFile);
            Assert.NotNull(result.Value.TextReportFile);
            Assert.NotEmpty(result.Value.ReportFile);
            Assert.NotEmpty(result.Value.TextReportFile);

            // Verify single SaveChangesAsync
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

            // Verify FormDetails mutated correctly
            Assert.True(formDetail.IsSummaryReviewed);
            Assert.Equal(userId, formDetail.IsSummaryReviewedBy);
            Assert.Equal("Auto", formDetail.SummaryReviewMethod);

            // Verify NetPay inserted
            Assert.Single(_inMemoryNetPays);
            Assert.Equal(nationalId, _inMemoryNetPays[0].EmployeeId);
            Assert.Equal(2100.0, _inMemoryNetPays[0].NetPay);
            Assert.Equal(dailyId, _inMemoryNetPays[0].DailyId);
        }

        [Fact]
        public async Task PdfRendererThrows_ReturnsFailure_AndNeverCallsSaveChangesAsync_DatabaseUnchanged()
        {
            // Arrange
            int dailyId = 101;
            string nationalId = "29001019999999";

            var formDetail = new FormDetails
            {
                Id = 601,
                FormId = 1,
                EmployeeId = nationalId,
                Amount = 1500.0,
                IsActive = true,
                IsSummaryReviewed = false
            };
            _inMemoryFormDetails.Add(formDetail);

            var summary = new DailyBeneficiarySummaryResponse
            {
                DailyName = "يومية تجربة الفشل",
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = nationalId,
                        EmployeeName = "علي حسن",
                        TegaraCode = "2002",
                        TotalAmount = 1500.0,
                        Details = new List<BeneficiaryDetailDto>
                        {
                            new BeneficiaryDetailDto { FormDetailId = 601, Amount = 1500.0, IsSummaryReviewed = false }
                        }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(new List<PdfEmployeeRecord>
                {
                    new PdfEmployeeRecord
                    {
                        NationalId = nationalId,
                        Name = "علي حسن",
                        TegaraCode = "2002",
                        TotalEntitlements = 1500.0,
                        NetPay = 1300.0
                    }
                });

            // Renderer throws an exception
            _rendererMock.Setup(r => r.RenderAnnotatedPdf(It.IsAny<byte[]>(), It.IsAny<List<PdfAnnotation>>()))
                .Throws(new InvalidOperationException("iText rendering crashed!"));

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-1");

            // Assert
            Assert.True(result.IsFailure);
            Assert.Contains("حدث خطأ أثناء إنشاء نتيجة مراجعة ملف الـ PDF", result.Error.Message);

            // DB was NOT touched!
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            _netPayRepoMock.Verify(r => r.Insert(It.IsAny<EmployeeNetPay>()), Times.Never);
            _netPayRepoMock.Verify(r => r.Update(It.IsAny<EmployeeNetPay>()), Times.Never);
            Assert.False(formDetail.IsSummaryReviewed);
            Assert.Empty(_inMemoryNetPays);
        }

        [Fact]
        public async Task DailyClosesDuringProcessing_ReturnsFailure_AndNeverCallsSaveChangesAsync()
        {
            // Arrange
            int dailyId = 102;
            string nationalId = "29001018888888";

            _inMemoryFormDetails.Add(new FormDetails
            {
                Id = 701,
                EmployeeId = nationalId,
                Amount = 3000.0,
                IsActive = true,
                IsSummaryReviewed = false
            });

            var summary = new DailyBeneficiarySummaryResponse
            {
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = nationalId,
                        TotalAmount = 3000.0,
                        Details = new List<BeneficiaryDetailDto>
                        {
                            new BeneficiaryDetailDto { FormDetailId = 701, Amount = 3000.0 }
                        }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(new List<PdfEmployeeRecord>
                {
                    new PdfEmployeeRecord
                    {
                        NationalId = nationalId,
                        TotalEntitlements = 3000.0,
                        NetPay = 2700.0
                    }
                });

            // Guard succeeds on initial check (Phase A), but FAILS on second check (Phase D)
            _closureGuardMock.SetupSequence(g => g.EnsureDailyOpenAsync(dailyId))
                .ReturnsAsync(Result.Success())
                .ReturnsAsync(Result.Failure(new Error("400", "اليومية مغلقة ولا يمكن تعديلها")));

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-1");

            // Assert
            Assert.True(result.IsFailure);
            Assert.Contains("اليومية مغلقة", result.Error.Message);

            // SaveChangesAsync was NEVER called
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
            Assert.Empty(_inMemoryNetPays);
        }

        [Fact]
        public async Task DatabaseFailureOnSaveChangesAsync_ReturnsGenericFailure_WithoutLeakingInternalException()
        {
            // Arrange
            int dailyId = 103;
            string nationalId = "29001017777777";

            _inMemoryFormDetails.Add(new FormDetails
            {
                Id = 801,
                EmployeeId = nationalId,
                Amount = 1000.0,
                IsActive = true
            });

            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(new DailyBeneficiarySummaryResponse
                {
                    Beneficiaries = new List<BeneficiarySummaryDto>
                    {
                        new BeneficiarySummaryDto
                        {
                            EmployeeId = nationalId,
                            TotalAmount = 1000.0,
                            Details = new List<BeneficiaryDetailDto>
                            {
                                new BeneficiaryDetailDto { FormDetailId = 801, Amount = 1000.0 }
                            }
                        }
                    }
                }));

            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(new List<PdfEmployeeRecord>
                {
                    new PdfEmployeeRecord { NationalId = nationalId, TotalEntitlements = 1000.0, NetPay = 900.0 }
                });

            // SaveChangesAsync throws a low-level DB exception with sensitive info
            _uowMock.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DbUpdateException("FATAL: Table 'dbo.EmployeeNetPay' deadlock detected on key (0x3344)"));

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-1");

            // Assert
            Assert.True(result.IsFailure);
            Assert.Equal("500", result.Error.Code);
            Assert.Equal("حدث خطأ أثناء حفظ بيانات مراجعة اليومية.", result.Error.Message);
            Assert.DoesNotContain("deadlock", result.Error.Message);
            Assert.DoesNotContain("EmployeeNetPay", result.Error.Message);
        }

        [Fact]
        public async Task AlreadyReviewedRecords_PreservedMatchingBehavior_AndDoNotPersistDuplicateNetPay()
        {
            // Arrange
            int dailyId = 104;
            string nationalId = "29001016666666";

            var summary = new DailyBeneficiarySummaryResponse
            {
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = nationalId,
                        TotalAmount = 4000.0,
                        Details = new List<BeneficiaryDetailDto>
                        {
                            new BeneficiaryDetailDto
                            {
                                FormDetailId = 901,
                                Amount = 4000.0,
                                IsSummaryReviewed = true // Already reviewed!
                            }
                        }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(new List<PdfEmployeeRecord>
                {
                    new PdfEmployeeRecord { NationalId = nationalId, TotalEntitlements = 4000.0, NetPay = 3500.0 }
                });

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-1");

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value.MatchedCount);
            Assert.Equal(0, result.Value.ErrorCount);

            // Because it was already reviewed, no updates or inserts should be dispatched
            _netPayRepoMock.Verify(r => r.Insert(It.IsAny<EmployeeNetPay>()), Times.Never);
            _netPayRepoMock.Verify(r => r.Update(It.IsAny<EmployeeNetPay>()), Times.Never);
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task MixedResults_OnlyMatchedEmployeesArePersistedToDatabase()
        {
            // Arrange
            int dailyId = 105;
            string empMatchedId = "11111111111111";
            string empMismatchedId = "22222222222222";
            string empMissingFromDailyId = "33333333333333";

            var formDetailMatched = new FormDetails { Id = 1001, EmployeeId = empMatchedId, Amount = 1000.0, IsActive = true, IsSummaryReviewed = false };
            var formDetailMismatched = new FormDetails { Id = 1002, EmployeeId = empMismatchedId, Amount = 2000.0, IsActive = true, IsSummaryReviewed = false };
            _inMemoryFormDetails.AddRange(new[] { formDetailMatched, formDetailMismatched });

            var summary = new DailyBeneficiarySummaryResponse
            {
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = empMatchedId,
                        EmployeeName = "مطابق",
                        TegaraCode = "10",
                        TotalAmount = 1000.0,
                        Details = new List<BeneficiaryDetailDto> { new BeneficiaryDetailDto { FormDetailId = 1001, Amount = 1000.0, IsSummaryReviewed = false } }
                    },
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = empMismatchedId,
                        EmployeeName = "مختلف",
                        TegaraCode = "20",
                        TotalAmount = 2000.0, // In DB: 2000
                        Details = new List<BeneficiaryDetailDto> { new BeneficiaryDetailDto { FormDetailId = 1002, Amount = 2000.0, IsSummaryReviewed = false } }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            var pdfRecords = new List<PdfEmployeeRecord>
            {
                // 1. Matched
                new PdfEmployeeRecord { NationalId = empMatchedId, Name = "مطابق", TegaraCode = "10", TotalEntitlements = 1000.0, NetPay = 850.0 },
                // 2. Mismatch in entitlement (9999 != 2000)
                new PdfEmployeeRecord { NationalId = empMismatchedId, Name = "مختلف", TegaraCode = "20", TotalEntitlements = 9999.0, NetPay = 1700.0 },
                // 3. Not in DB summary at all
                new PdfEmployeeRecord { NationalId = empMissingFromDailyId, Name = "غير موجود", TegaraCode = "30", TotalEntitlements = 500.0, NetPay = 450.0 }
            };
            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(pdfRecords);

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-mixed");

            // Assert
            Assert.True(result.IsSuccess);
            Assert.Equal(1, result.Value.MatchedCount);
            Assert.Equal(2, result.Value.ErrorCount); // 1 mismatch + 1 missing

            // Persisted once
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

            // Matched employee is reviewed
            Assert.True(formDetailMatched.IsSummaryReviewed);
            Assert.Equal("user-mixed", formDetailMatched.IsSummaryReviewedBy);

            // Mismatched employee is UNTOUCHED
            Assert.False(formDetailMismatched.IsSummaryReviewed);
            Assert.Null(formDetailMismatched.IsSummaryReviewedBy);

            // Only matched employee NetPay is saved
            Assert.Single(_inMemoryNetPays);
            Assert.Equal(empMatchedId, _inMemoryNetPays[0].EmployeeId);
            Assert.Equal(850.0, _inMemoryNetPays[0].NetPay);
        }

        [Fact]
        public async Task ExistingNetPayIsUpdated_AndMissingNetPayIsInserted()
        {
            // Arrange
            int dailyId = 106;
            string empExistingNetPay = "55555555555555";
            string empNewNetPay = "66666666666666";

            // Existing net pay in DB
            var existingRecord = new EmployeeNetPay
            {
                Id = 1,
                DailyId = dailyId,
                EmployeeId = empExistingNetPay,
                NetPay = 100.0 // old value
            };
            _inMemoryNetPays.Add(existingRecord);

            var fd1 = new FormDetails { Id = 2001, EmployeeId = empExistingNetPay, Amount = 500.0, IsActive = true };
            var fd2 = new FormDetails { Id = 2002, EmployeeId = empNewNetPay, Amount = 600.0, IsActive = true };
            _inMemoryFormDetails.AddRange(new[] { fd1, fd2 });

            var summary = new DailyBeneficiarySummaryResponse
            {
                Beneficiaries = new List<BeneficiarySummaryDto>
                {
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = empExistingNetPay,
                        TotalAmount = 500.0,
                        Details = new List<BeneficiaryDetailDto> { new BeneficiaryDetailDto { FormDetailId = 2001, Amount = 500.0 } }
                    },
                    new BeneficiarySummaryDto
                    {
                        EmployeeId = empNewNetPay,
                        TotalAmount = 600.0,
                        Details = new List<BeneficiaryDetailDto> { new BeneficiaryDetailDto { FormDetailId = 2002, Amount = 600.0 } }
                    }
                }
            };
            _dailyServiceMock.Setup(s => s.GetBeneficiariesSummary(dailyId))
                .ReturnsAsync(Result.Success(summary));

            var pdfRecords = new List<PdfEmployeeRecord>
            {
                new PdfEmployeeRecord { NationalId = empExistingNetPay, TotalEntitlements = 500.0, NetPay = 450.0 },
                new PdfEmployeeRecord { NationalId = empNewNetPay, TotalEntitlements = 600.0, NetPay = 550.0 }
            };
            _pdfParserMock.Setup(p => p.ParseFullEmployeeDataFromPdf(It.IsAny<Stream>()))
                .Returns(pdfRecords);

            var service = CreateService();
            using var dummyPdfStream = new MemoryStream(new byte[] { 1, 2, 3 });

            // Act
            var result = await service.VerifyPdfAgainstSummary(dailyId, dummyPdfStream, "user-netpay");

            // Assert
            Assert.True(result.IsSuccess);
            _uowMock.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

            // Existing record was updated
            _netPayRepoMock.Verify(r => r.Update(existingRecord), Times.Once);
            Assert.Equal(450.0, existingRecord.NetPay);

            // New record was inserted
            _netPayRepoMock.Verify(r => r.Insert(It.Is<EmployeeNetPay>(n => n.EmployeeId == empNewNetPay && n.NetPay == 550.0)), Times.Once);
        }
    }
}
