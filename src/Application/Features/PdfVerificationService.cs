using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application.Dtos;
using Application.Helpers;
using Application.Interfaces;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Features
{
    public class PdfVerificationResult
    {
        public bool Success { get; set; }
        public byte[] ReportFile { get; set; }
        public byte[] TextReportFile { get; set; }
        public int MatchedCount { get; set; }
        public int ErrorCount { get; set; }
    }

    public class PdfAnnotation
    {
        public int PageNumber { get; set; }
        public double Top { get; set; }
        public double Bottom { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
    }

    public class PdfVerificationPlan
    {
        public List<int> FormDetailIdsToReview { get; set; } = new();
        public List<NetPayPlanItem> NetPaysToUpsert { get; set; } = new();
        public List<PdfAnnotation> Annotations { get; set; } = new();
        public int MatchedCount { get; set; }
        public int ErrorCount { get; set; }
        public StringBuilder ReportBuilder { get; set; } = new();
        public List<string> MissingFromDaily { get; set; } = new();
    }

    public class NetPayPlanItem
    {
        public string EmployeeId { get; set; } = null!;
        public double NetPay { get; set; }
    }

    public class PdfVerificationService
    {
        private readonly PayrollPdfParserService _pdfParserService;
        private readonly DailyService _dailyService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IGenericRepository<EmployeeNetPay> _netPayRepo;
        private readonly IGenericRepository<FormDetails> _formDetailsRepo;
        private readonly ILogger<PdfVerificationService> _logger;
        private readonly IDailyClosureGuard _dailyClosureGuard;
        private readonly IPdfVerificationDocumentRenderer _documentRenderer;

        public PdfVerificationService(
            PayrollPdfParserService pdfParserService,
            DailyService dailyService,
            IUnitOfWork unitOfWork,
            IGenericRepository<EmployeeNetPay> netPayRepo,
            IGenericRepository<FormDetails> formDetailsRepo,
            ILogger<PdfVerificationService> logger,
            IDailyClosureGuard dailyClosureGuard,
            IPdfVerificationDocumentRenderer documentRenderer)
        {
            _pdfParserService = pdfParserService;
            _dailyService = dailyService;
            _unitOfWork = unitOfWork;
            _netPayRepo = netPayRepo;
            _formDetailsRepo = formDetailsRepo;
            _logger = logger;
            _dailyClosureGuard = dailyClosureGuard;
            _documentRenderer = documentRenderer;
        }

        // Dictionary for mapping Arabic Presentation Forms (isolated, medial, final, initial) to base characters
        private static readonly Dictionary<char, string> ArabicPresentationFormsMap = new Dictionary<char, string>
        {
            {'ﺍ', "ا"}, {'ﺎ', "ا"}, {'ﺏ', "ب"}, {'ﺑ', "ب"}, {'ﺒ', "ب"}, {'ﺐ', "ب"},
            {'ﺕ', "ت"}, {'ﺗ', "ت"}, {'ﺘ', "ت"}, {'ﺖ', "ت"}, {'ﺙ', "ث"}, {'ﺛ', "ث"},
            {'ﺜ', "ث"}, {'ﺚ', "ث"}, {'ﺝ', "ج"}, {'ﺟ', "ج"}, {'ﺠ', "ج"}, {'ﺞ', "ج"},
            {'ﺡ', "ح"}, {'ﺣ', "ح"}, {'ﺤ', "ح"}, {'ﺢ', "ح"}, {'ﺥ', "خ"}, {'ﺧ', "خ"},
            {'ﺨ', "خ"}, {'ﺦ', "خ"}, {'ﺩ', "د"}, {'ﺪ', "د"}, {'ﺫ', "ذ"}, {'ﺬ', "ذ"},
            {'ﺭ', "ر"}, {'ﺮ', "ر"}, {'ﺯ', "ز"}, {'ﺰ', "ز"}, {'ﺱ', "س"}, {'ﺳ', "س"},
            {'ﺴ', "س"}, {'ﺲ', "س"}, {'ﺵ', "ش"}, {'ﺷ', "ش"}, {'ﺸ', "ش"}, {'ﺶ', "ش"},
            {'ﺹ', "ص"}, {'ﺻ', "ص"}, {'ﺼ', "ص"}, {'ﺺ', "ص"}, {'ﺽ', "ض"}, {'ﺽ', "ض"},
            {'ﻀ', "ض"}, {'ﺾ', "ض"}, {'ﻁ', "ط"}, {'ﻃ', "ط"}, {'ﻄ', "ط"}, {'ﻂ', "ط"},
            {'ﻅ', "ظ"}, {'ﻇ', "ظ"}, {'ﻈ', "ظ"}, {'ﻆ', "ظ"}, {'ﻉ', "ع"}, {'ﻋ', "ع"},
            {'ﻌ', "ع"}, {'ﻊ', "ع"}, {'ﻍ', "غ"}, {'ﻏ', "غ"}, {'ﻐ', "غ"}, {'ﻎ', "غ"},
            {'ﻑ', "ف"}, {'ﻓ', "ف"}, {'ﻔ', "ف"}, {'ﻒ', "ف"}, {'ﻕ', "ق"}, {'ﻗ', "ق"},
            {'ﻘ', "ق"}, {'ﻖ', "ق"}, {'ﻙ', "ك"}, {'ﻛ', "ك"}, {'ﻜ', "ك"}, {'ﻚ', "ك"},
            {'ﻝ', "ل"}, {'ﻟ', "ل"}, {'ﻠ', "ل"}, {'ﻞ', "ل"}, {'ﻡ', "م"}, {'ﻣ', "م"},
            {'ﻤ', "م"}, {'ﻢ', "م"}, {'ﻥ', "ن"}, {'ﻧ', "ن"}, {'ﻨ', "ن"}, {'ﻦ', "ن"},
            {'ﻩ', "ه"}, {'ﻫ', "ه"}, {'ﻬ', "ه"}, {'ﻪ', "ه"}, {'ﻭ', "و"}, {'ﻮ', "و"},
            {'ﻱ', "ي"}, {'ﻳ', "ي"}, {'ﻴ', "ي"}, {'ﻲ', "ي"}, {'ﻯ', "ى"}, {'ﻰ', "ى"},
            {'ﺓ', "ة"}, {'ﺔ', "ة"}, {'ﺃ', "أ"}, {'ﺄ', "أ"}, {'ﺇ', "إ"}, {'ﺈ', "إ"},
            {'ﺁ', "آ"}, {'ﺂ', "آ"}, {'ﺅ', "ؤ"}, {'ﺆ', "ؤ"}, {'ﺉ', "ئ"}, {'ﺋ', "ئ"},
            {'ﺌ', "ئ"}, {'ﺊ', "ئ"},
            {'ﻻ', "لا"}, {'ﻼ', "لا"}, {'ﺀ', "ء"},
            {'ﻷ', "لا"}, {'ﻸ', "لا"}, {'ﻹ', "لا"}, {'ﻶ', "لا"}, {'ﻵ', "لا"}, {'ﻺ', "لا"}
        };

        private bool IsNameMatch(string dbNorm, string pdfNorm)
        {
            if (dbNorm == pdfNorm) return true;
            if (dbNorm.Length >= 3 && pdfNorm.Length >= 3)
            {
                if (dbNorm.StartsWith(pdfNorm) || pdfNorm.StartsWith(dbNorm)) return true;
                if (dbNorm.Substring(0, 3) == pdfNorm.Substring(0, 3)) return true;
            }
            return false;
        }

        private string NormalizeArabicText(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            // Normalize spaces
            input = string.Join(" ", input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            // Map presentation forms
            var sb = new StringBuilder();
            foreach (char c in input)
            {
                if (ArabicPresentationFormsMap.TryGetValue(c, out string mappedValue))
                {
                    sb.Append(mappedValue);
                }
                else
                {
                    sb.Append(c);
                }
            }
            input = sb.ToString();

            // Normalize Arabic letters based on User request: أ-ا-إ-ي-ى-ه-ة-ل-ا-أ
            return input
                .Replace("أ", "ا")
                .Replace("إ", "ا")
                .Replace("آ", "ا")
                .Replace("ٱ", "ا")
                .Replace("ى", "ي")  // Replace Alef Maksura with Yeh
                .Replace("ة", "ه")  // Replace Teh Marbuta with Heh
                .Replace("ؤ", "و")
                .Replace("ئ", "ي")
                .Replace("لا", "لا")
                .Replace("ﻷ", "لا")
                .Replace("ﻹ", "لا")
                .Replace("ﻵ", "لا");
        }

        public async Task<Result<PdfVerificationResult>> VerifyPdfAgainstSummary(int dailyId, Stream pdfStream, string currentUserId)
        {
            // ==========================================
            // Phase A: Validation & Read (No DB Mutations)
            // ==========================================
            var guard = await _dailyClosureGuard.EnsureDailyOpenAsync(dailyId);
            if (guard.IsFailure)
            {
                return Result.Failure<PdfVerificationResult>(guard.Error);
            }

            var summaryResult = await _dailyService.GetBeneficiariesSummary(dailyId);
            if (!summaryResult.IsSuccess)
            {
                return Result.Failure<PdfVerificationResult>(summaryResult.Error);
            }

            var summary = summaryResult.Value;
            var summaryDict = summary.Beneficiaries.ToDictionary(b => b.EmployeeId);

            // Copy to byte array to avoid stream closure issues between libraries
            using var initialMs = new MemoryStream();
            await pdfStream.CopyToAsync(initialMs);
            byte[] pdfBytes = initialMs.ToArray();

            using var parseStream = new MemoryStream(pdfBytes);
            var pdfRecords = _pdfParserService.ParseFullEmployeeDataFromPdf(parseStream);
            if (!pdfRecords.Any())
            {
                return Result.Failure<PdfVerificationResult>(new Error("400", "لم يتم العثور على أي بيانات في ملف الـ PDF"));
            }

            // ==========================================
            // Phase B: Build Verification Plan in Memory
            // ==========================================
            var plan = new PdfVerificationPlan();
            plan.ReportBuilder.AppendLine("=== تقرير أخطاء مراجعة ملف الـ PDF ===");
            plan.ReportBuilder.AppendLine($"اليومية: {summary.DailyName}");
            plan.ReportBuilder.AppendLine($"تاريخ المراجعة: {DateTime.Now:yyyy-MM-dd HH:mm}");
            plan.ReportBuilder.AppendLine("==================================================");
            plan.ReportBuilder.AppendLine();

            foreach (var pdfRecord in pdfRecords)
            {
                bool hasError = false;
                var errorsForEmployee = new List<string>();

                // Extract Employee Code
                string pdfCodeStr = pdfRecord.TegaraCode?.Trim() ?? "";
                string originalPdfCodeStr = pdfCodeStr;

                // Handle compound codes like "30200105-44001" (InstitutionalCode-EmployeeCode)
                if (pdfCodeStr.Contains("-"))
                {
                    pdfCodeStr = pdfCodeStr.Split('-').Last().Trim();
                }

                // 1. Check National ID
                if (!summaryDict.TryGetValue(pdfRecord.NationalId, out var dbRecord))
                {
                    plan.MissingFromDaily.Add($"- الرقم القومي: {pdfRecord.NationalId} | الاسم: {pdfRecord.Name} | كود الموظف: {pdfCodeStr}");
                    plan.Annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "NotFound", Message = "ﺩﻮﺟﻮﻣ ﺮﻴﻏ" }); // غير موجود
                    plan.ErrorCount++;
                    continue; // Skip further checks for this record
                }

                // Skip if already reviewed by PDF or manually
                if (dbRecord.Details.Any() && dbRecord.Details.All(d => d.IsSummaryReviewed))
                {
                    plan.Annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Matched", Message = "(ﺎﻘﺒﺴﻣ) ﺔﻘﺑﺎﻄﻤﻟﺍ ﺖﻤﺗ" }); // تمت المطابقة (مسبقا)
                    plan.MatchedCount++;
                    continue;
                }

                string dbCodeStr = dbRecord.TegaraCode?.Trim() ?? "";

                if (dbCodeStr != pdfCodeStr && !string.IsNullOrEmpty(pdfCodeStr))
                {
                    errorsForEmployee.Add($"- اختلاف الكود المؤسسي | باليومية: {dbCodeStr} | بالملف: {originalPdfCodeStr} | المقتطع: {pdfCodeStr}");
                    hasError = true;
                }

                // Check Total Entitlements
                // Allow a small epsilon for floating point comparison (e.g., 0.05)
                if (Math.Abs(dbRecord.TotalAmount - pdfRecord.TotalEntitlements) > 0.05)
                {
                    errorsForEmployee.Add($"- اختلاف إجمالي الاستحقاقات | باليومية: {dbRecord.TotalAmount} | بالملف: {pdfRecord.TotalEntitlements}");
                    hasError = true;
                }

                if (hasError)
                {
                    plan.ReportBuilder.AppendLine($"الموظف: {dbRecord.EmployeeName} | الرقم القومي: {pdfRecord.NationalId}");
                    foreach (var err in errorsForEmployee)
                    {
                        plan.ReportBuilder.AppendLine(err);
                    }
                    plan.ReportBuilder.AppendLine("--------------------------------------------------");
                    plan.Annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Error", Message = "ﺔﻘﺑﺎﻄﻤﻟﺍ ﻢﺘﺗ ﻢﻟ" }); // لم تتم المطابقة
                    plan.ErrorCount++;
                }
                else
                {
                    plan.Annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Matched", Message = "ﺔﻘﺑﺎﻄﻤﻟﺍ ﺖﻤﺗ" }); // تمت المطابقة
                    plan.MatchedCount++;

                    // Mark related form details as Summary Reviewed
                    foreach (var det in dbRecord.Details)
                    {
                        plan.FormDetailIdsToReview.Add(det.FormDetailId);
                    }

                    // Prepare NetPay for plan
                    plan.NetPaysToUpsert.Add(new NetPayPlanItem
                    {
                        EmployeeId = pdfRecord.NationalId,
                        NetPay = pdfRecord.NetPay
                    });
                }
            }

            if (plan.MissingFromDaily.Any())
            {
                plan.ReportBuilder.AppendLine();
                plan.ReportBuilder.AppendLine("=== موظفون مسجلون في الملف وغير موجودين في اليومية ===");
                foreach (var missing in plan.MissingFromDaily)
                {
                    plan.ReportBuilder.AppendLine(missing);
                }
                plan.ReportBuilder.AppendLine();
            }

            if (plan.ErrorCount == 0)
            {
                plan.ReportBuilder.AppendLine("تمت المطابقة بنجاح بنسبة 100%. لا توجد أي أخطاء.");
            }

            // ==========================================
            // Phase C: Generate All Output in Memory (Before DB Commit)
            // ==========================================
            byte[] annotatedPdfBytes;
            try
            {
                annotatedPdfBytes = _documentRenderer.RenderAnnotatedPdf(pdfBytes, plan.Annotations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to render annotated PDF for DailyId: {DailyId}", dailyId);
                return Result.Failure<PdfVerificationResult>(new Error("500", "حدث خطأ أثناء إنشاء نتيجة مراجعة ملف الـ PDF."));
            }

            byte[] reportBytes = Encoding.UTF8.GetBytes(plan.ReportBuilder.ToString());
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] fullTextBytes = new byte[bom.Length + reportBytes.Length];
            Buffer.BlockCopy(bom, 0, fullTextBytes, 0, bom.Length);
            Buffer.BlockCopy(reportBytes, 0, fullTextBytes, bom.Length, reportBytes.Length);

            // ==========================================
            // Phase D: Re-check Daily Closure Guard (Before Persistence)
            // ==========================================
            var recheckGuard = await _dailyClosureGuard.EnsureDailyOpenAsync(dailyId);
            if (recheckGuard.IsFailure)
            {
                _logger.LogWarning("Daily {DailyId} was closed during verification processing. Persistence aborted.", dailyId);
                return Result.Failure<PdfVerificationResult>(recheckGuard.Error);
            }

            // ==========================================
            // Phase E: Atomic Database Persistence (Single SaveChanges)
            // ==========================================
            if (plan.FormDetailIdsToReview.Any() || plan.NetPaysToUpsert.Any())
            {
                if (plan.FormDetailIdsToReview.Any())
                {
                    var distinctDetailIds = plan.FormDetailIdsToReview.Distinct().ToList();
                    var formDetailsToUpdate = await _formDetailsRepo.GetQueryable()
                        .Where(fd => distinctDetailIds.Contains(fd.Id))
                        .ToListAsync();

                    foreach (var fd in formDetailsToUpdate)
                    {
                        fd.IsSummaryReviewed = true;
                        fd.IsSummaryReviewedBy = currentUserId;
                        fd.SummaryReviewedAt = DateTime.Now;
                        fd.SummaryReviewMethod = "Auto";
                    }
                }

                if (plan.NetPaysToUpsert.Any())
                {
                    var existingNetPays = await _netPayRepo.GetQueryable()
                        .Where(n => n.DailyId == dailyId)
                        .ToListAsync();

                    foreach (var npItem in plan.NetPaysToUpsert)
                    {
                        var existing = existingNetPays.FirstOrDefault(n => n.EmployeeId == npItem.EmployeeId);
                        if (existing != null)
                        {
                            existing.NetPay = npItem.NetPay;
                            _netPayRepo.Update(existing);
                        }
                        else
                        {
                            await _netPayRepo.Insert(new EmployeeNetPay
                            {
                                DailyId = dailyId,
                                EmployeeId = npItem.EmployeeId,
                                NetPay = npItem.NetPay
                            });
                        }
                    }
                }

                try
                {
                    await _unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist PDF verification changes for DailyId: {DailyId}", dailyId);
                    return Result.Failure<PdfVerificationResult>(new Error("500", "حدث خطأ أثناء حفظ بيانات مراجعة اليومية."));
                }
            }

            return Result.Success(new PdfVerificationResult
            {
                Success = true,
                ReportFile = annotatedPdfBytes,
                TextReportFile = fullTextBytes,
                MatchedCount = plan.MatchedCount,
                ErrorCount = plan.ErrorCount
            });
        }
    }
}
