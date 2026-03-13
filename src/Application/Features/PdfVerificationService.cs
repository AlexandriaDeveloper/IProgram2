using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application.Dtos;
using Core.Interfaces;
using Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Persistence.Helpers;
using Application.Helpers;

namespace Application.Features
{
    public class PdfVerificationResult
    {
        public bool Success { get; set; }
        public byte[] ReportFile { get; set; }
        public int MatchedCount { get; set; }
        public int ErrorCount { get; set; }
    }

    public class PdfVerificationService
    {
        private readonly PayrollPdfParserService _pdfParserService;
        private readonly DailyService _dailyService;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IGenericRepository<EmployeeNetPay> _netPayRepo;
        private readonly IGenericRepository<FormDetails> _formDetailsRepo;
        private readonly ILogger<PdfVerificationService> _logger;

        public PdfVerificationService(
            PayrollPdfParserService pdfParserService,
            DailyService dailyService,
            IUnitOfWork unitOfWork,
            IGenericRepository<EmployeeNetPay> netPayRepo,
            IGenericRepository<FormDetails> formDetailsRepo,
            ILogger<PdfVerificationService> logger)
        {
            _pdfParserService = pdfParserService;
            _dailyService = dailyService;
            _unitOfWork = unitOfWork;
            _netPayRepo = netPayRepo;
            _formDetailsRepo = formDetailsRepo;
            _logger = logger;
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
            {'ﺹ', "ص"}, {'ﺻ', "ص"}, {'ﺼ', "ص"}, {'ﺺ', "ص"}, {'ﺽ', "ض"}, {'ﺿ', "ض"},
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
            // 1. Get Summary from DB
            var summaryResult = await _dailyService.GetBeneficiariesSummary(dailyId);
            if (!summaryResult.IsSuccess)
            {
                return Result.Failure<PdfVerificationResult>(summaryResult.Error);
            }

            var summary = summaryResult.Value;
            var summaryDict = summary.Beneficiaries.ToDictionary(b => b.EmployeeId);

            // 2. Parse PDF
            var pdfRecords = _pdfParserService.ParseFullEmployeeDataFromPdf(pdfStream);
            if (!pdfRecords.Any())
            {
                return Result.Failure<PdfVerificationResult>(new Error("400", "لم يتم العثور على أي بيانات في ملف الـ PDF"));
            }

            var reportBuilder = new StringBuilder();
            reportBuilder.AppendLine("=== تقرير أخطاء مراجعة ملف الـ PDF ===");
            reportBuilder.AppendLine($"اليومية: {summary.DailyName}");
            reportBuilder.AppendLine($"تاريخ المراجعة: {DateTime.Now:yyyy-MM-dd HH:mm}");
            reportBuilder.AppendLine("==================================================");
            reportBuilder.AppendLine();

            int matchedCount = 0;
            int errorCount = 0;
            var detailsToUpdate = new List<FormDetails>();
            var newNetPays = new List<EmployeeNetPay>();

            // Clean up existing net pays before we add new ones for successfully matched
            var existingNetPays = await _netPayRepo.GetQueryable()
                .Where(n => n.DailyId == dailyId).ToListAsync();

            // Load all form details into memory to update them efficiently
            var formDetailIds = summary.Beneficiaries.SelectMany(b => b.Details.Select(d => d.FormDetailId)).ToList();
            var dbFormDetails = await _formDetailsRepo.GetQueryable()
                .Where(fd => formDetailIds.Contains(fd.Id))
                .ToDictionaryAsync(fd => fd.Id);

            foreach (var pdfRecord in pdfRecords)
            {
                bool hasError = false;
                var errorsForEmployee = new List<string>();

                // 1. Check National ID
                if (!summaryDict.TryGetValue(pdfRecord.NationalId, out var dbRecord))
                {
                    reportBuilder.AppendLine($"[خطأ في الرقم القومي] الرقم القومي {pdfRecord.NationalId} (الاسم المستخرج من الملف: {pdfRecord.Name} | كود المؤسسة: {pdfRecord.TegaraCode}) موجود في الـ PDF ولكنه غير موجود في اليومية.");
                    errorCount++;
                    continue; // Skip further checks for this record
                }

                // Skip if already reviewed by PDF or manually
                if (dbRecord.Details.Any() && dbRecord.Details.All(d => d.IsSummaryReviewed))
                {
                    matchedCount++;
                    continue;
                }

                // 2. Check Code (TegaraCode)
                string dbCodeStr = dbRecord.TegaraCode?.Trim() ?? "";
                string pdfCodeStr = pdfRecord.TegaraCode?.Trim() ?? "";
                string originalPdfCodeStr = pdfCodeStr;

                // Handle compound codes like "30200105-44001" (InstitutionalCode-EmployeeCode)
                if (pdfCodeStr.Contains("-"))
                {
                    pdfCodeStr = pdfCodeStr.Split('-').Last().Trim();
                }

                if (dbCodeStr != pdfCodeStr && !string.IsNullOrEmpty(pdfCodeStr))
                {
                    errorsForEmployee.Add($"- اختلاف في الكود المؤسسي: موجود باليومية ({dbCodeStr}) وموجود بالملف ({originalPdfCodeStr}) المقتطع منه ({pdfCodeStr})");
                    hasError = true;
                }

                // 4. Check Total Entitlements
                // Allow a small epsilon for floating point comparison (e.g., 0.01)
                if (Math.Abs(dbRecord.TotalAmount - pdfRecord.TotalEntitlements) > 0.05)
                {
                    errorsForEmployee.Add($"- اختلاف في إجمالي الاستحقاقات: موجود باليومية ({dbRecord.TotalAmount}) وموجود بالملف ({pdfRecord.TotalEntitlements})");
                    hasError = true;
                }

                if (hasError)
                {
                    reportBuilder.AppendLine($"الموظف: {dbRecord.EmployeeName} (الرقم القومي: {pdfRecord.NationalId})");
                    foreach (var err in errorsForEmployee)
                    {
                        reportBuilder.AppendLine(err);
                    }
                    reportBuilder.AppendLine("--------------------------------------------------");
                    errorCount++;
                }
                else
                {
                    // Success!
                    matchedCount++;

                    // Mark related form details as Summary Reviewed
                    foreach (var det in dbRecord.Details)
                    {
                        if (dbFormDetails.TryGetValue(det.FormDetailId, out var fdToUpdate))
                        {
                            fdToUpdate.IsSummaryReviewed = true;
                            fdToUpdate.IsSummaryReviewedBy = currentUserId;
                            fdToUpdate.SummaryReviewedAt = DateTime.Now;
                            // Adding to list isn't strictly necessary since it's tracked by EF, but good practice
                        }
                    }

                    // Prepare NetPay for saving
                    var existingNetPay = existingNetPays.FirstOrDefault(n => n.EmployeeId == pdfRecord.NationalId);
                    if (existingNetPay != null)
                    {
                        existingNetPay.NetPay = pdfRecord.NetPay;
                        _netPayRepo.Update(existingNetPay);
                    }
                    else
                    {
                        newNetPays.Add(new EmployeeNetPay
                        {
                            DailyId = dailyId,
                            EmployeeId = pdfRecord.NationalId,
                            NetPay = pdfRecord.NetPay
                        });
                    }
                }
            }

            // Check if there are employees in the DB that weren't in the PDF
            var pdfNationalIds = new HashSet<string>(pdfRecords.Select(r => r.NationalId));

            // Missing employees are those strictly not in the PDF, AND have not been already reviewed
            var missingFromPdf = summary.Beneficiaries
                .Where(b => !pdfNationalIds.Contains(b.EmployeeId)
                            && !(b.Details.Any() && b.Details.All(d => d.IsSummaryReviewed)))
                .ToList();

            if (missingFromPdf.Any())
            {
                reportBuilder.AppendLine();
                reportBuilder.AppendLine("=== موظفون موجودون باليومية ولم يتم العثور عليهم في الـ PDF ===");
                foreach (var missing in missingFromPdf)
                {
                    reportBuilder.AppendLine($"- {missing.EmployeeName} (الرقم القومي: {missing.EmployeeId})");
                    errorCount++;
                }
            }

            if (errorCount == 0)
            {
                reportBuilder.AppendLine("تمت المطابقة بنجاح بنسبة 100%. لا توجد أي أخطاء.");
            }

            // Save new NetPays
            if (newNetPays.Any())
            {
                foreach (var np in newNetPays)
                {
                    await _netPayRepo.Insert(np);
                }
            }

            // Save all tracked changes
            await _unitOfWork.SaveChangesAsync();

            byte[] reportBytes = Encoding.UTF8.GetBytes(reportBuilder.ToString());
            // Add UTF-8 BOM so Excel/Notepad open Arabic correctly
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            var fullReportBytes = new byte[bom.Length + reportBytes.Length];
            Buffer.BlockCopy(bom, 0, fullReportBytes, 0, bom.Length);
            Buffer.BlockCopy(reportBytes, 0, fullReportBytes, bom.Length, reportBytes.Length);

            return Result.Success(new PdfVerificationResult
            {
                Success = true,
                ReportFile = fullReportBytes,
                MatchedCount = matchedCount,
                ErrorCount = errorCount
            });
        }
    }
}
