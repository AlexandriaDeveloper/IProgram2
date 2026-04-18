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
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Colors;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Extgstate;
using iText.IO.Font;
using iText.Kernel.Font;

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
            // 1. Get Summary from DB
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

            // 2. Parse PDF
            using var parseStream = new MemoryStream(pdfBytes);
            var pdfRecords = _pdfParserService.ParseFullEmployeeDataFromPdf(parseStream);
            if (!pdfRecords.Any())
            {
                return Result.Failure<PdfVerificationResult>(new Error("400", "لم يتم العثور على أي بيانات في ملف الـ PDF"));
            }

            var annotations = new List<PdfAnnotation>();

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

            var missingFromDaily = new List<string>();

            foreach (var pdfRecord in pdfRecords)
            {
                bool hasError = false;
                var errorsForEmployee = new List<string>();

                // 2. Extact Employee Code
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
                    missingFromDaily.Add($"- الرقم القومي: {pdfRecord.NationalId} | الاسم: {pdfRecord.Name} | كود الموظف: {pdfCodeStr}");
                    annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "NotFound", Message = "ﺩﻮﺟﻮﻣ ﺮﻴﻏ" }); // غير موجود
                    errorCount++;
                    continue; // Skip further checks for this record
                }

                // Skip if already reviewed by PDF or manually
                if (dbRecord.Details.Any() && dbRecord.Details.All(d => d.IsSummaryReviewed))
                {
                    annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Matched", Message = "(ﺎﻘﺒﺴﻣ) ﺔﻘﺑﺎﻄﻤﻟﺍ ﺖﻤﺗ" }); // تمت المطابقة (مسبقا)
                    matchedCount++;
                    continue;
                }

                string dbCodeStr = dbRecord.TegaraCode?.Trim() ?? "";

                if (dbCodeStr != pdfCodeStr && !string.IsNullOrEmpty(pdfCodeStr))
                {
                    errorsForEmployee.Add($"- اختلاف الكود المؤسسي | باليومية: {dbCodeStr} | بالملف: {originalPdfCodeStr} | المقتطع: {pdfCodeStr}");
                    hasError = true;
                }

                // 4. Check Total Entitlements
                // Allow a small epsilon for floating point comparison (e.g., 0.05)
                if (Math.Abs(dbRecord.TotalAmount - pdfRecord.TotalEntitlements) > 0.05)
                {
                    errorsForEmployee.Add($"- اختلاف إجمالي الاستحقاقات | باليومية: {dbRecord.TotalAmount} | بالملف: {pdfRecord.TotalEntitlements}");
                    hasError = true;
                }

                if (hasError)
                {
                    reportBuilder.AppendLine($"الموظف: {dbRecord.EmployeeName} | الرقم القومي: {pdfRecord.NationalId}");
                    foreach (var err in errorsForEmployee)
                    {
                        reportBuilder.AppendLine(err);
                    }
                    reportBuilder.AppendLine("--------------------------------------------------");
                    annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Error", Message = "ﺔﻘﺑﺎﻄﻤﻟﺍ ﻢﺘﺗ ﻢﻟ" }); // لم تتم المطابقة
                    errorCount++;
                }
                else
                {
                    annotations.Add(new PdfAnnotation { PageNumber = pdfRecord.PageNumber, Top = pdfRecord.BoundingBoxTop, Bottom = pdfRecord.BoundingBoxBottom, State = "Matched", Message = "ﺔﻘﺑﺎﻄﻤﻟﺍ ﺖﻤﺗ" }); // تمت المطابقة
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

            if (missingFromDaily.Any())
            {
                reportBuilder.AppendLine();
                reportBuilder.AppendLine("=== موظفون مسجلون في الملف وغير موجودين في اليومية ===");
                foreach (var missing in missingFromDaily)
                {
                    reportBuilder.AppendLine(missing);
                }
                reportBuilder.AppendLine();
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

            byte[] annotatedPdfBytes;
            try
            {
                using var itextInputStream = new MemoryStream(pdfBytes);
                using var outStream = new MemoryStream();
                using (var pdfReader = new PdfReader(itextInputStream))
                using (var pdfWriter = new PdfWriter(outStream))
                using (var pdfDoc = new PdfDocument(pdfReader, pdfWriter))
                {
                    var fontPath = @"C:\Windows\Fonts\arial.ttf";
                    PdfFont font = null;
                    if (File.Exists(fontPath))
                    {
                        font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);
                    }

                    var pageGroups = annotations.GroupBy(a => a.PageNumber);

                    foreach (var group in pageGroups)
                    {
                        if (group.Key <= 0 || group.Key > pdfDoc.GetNumberOfPages()) continue;

                        var page = pdfDoc.GetPage(group.Key);
                        var canvas = new PdfCanvas(page);
                        var rect = page.GetPageSize();
                        
                        float boxWidth = 250;
                        float boxHeight = 40;
                        float x = (rect.GetWidth() - boxWidth) / 2; // Center horizontally
                        
                        // Start near the bottom, moved up by 10 cm (10 cm = ~283.5 points)
                        float yOffset = 30 + 283.5f;

                        foreach (var ann in group)
                        {
                            var extGState = new PdfExtGState().SetFillOpacity(0.7f);
                            canvas.SetExtGState(extGState);

                            if (ann.State == "Matched")
                                canvas.SetFillColor(ColorConstants.GREEN);
                            else if (ann.State == "Error")
                                canvas.SetFillColor(ColorConstants.RED);
                            else
                                canvas.SetFillColor(ColorConstants.YELLOW);

                            // Draw centered box at the bottom
                            canvas.Rectangle(x, yOffset, boxWidth, boxHeight);
                            canvas.Fill();

                            if (font != null) 
                            {
                                canvas.SetExtGState(new PdfExtGState().SetFillOpacity(1.0f));
                                canvas.SetFillColor(ColorConstants.BLACK);
                                canvas.BeginText();
                                canvas.SetFontAndSize(font, 14);
                                
                                // Approximate centering for the text inside the box
                                float textX = x + 70; 
                                float textY = yOffset + (boxHeight / 2) - 5;
                                canvas.MoveText(textX, textY);
                                
                                canvas.ShowText(ann.Message);
                                canvas.EndText();
                            }

                            // Stack upwards if there are multiple annotations on the same page
                            yOffset += boxHeight + 10;
                        }
                    }
                    pdfDoc.Close();
                }
                annotatedPdfBytes = outStream.ToArray();

                try 
                {
                    // Debug save
                    System.IO.File.WriteAllBytes(@"f:\Prog-Projects\IProgram\test_debug.pdf", annotatedPdfBytes);
                }
                catch { }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to annotate PDF.");
                return Result.Failure<PdfVerificationResult>(new Error("500", "حدث خطأ أثناء تعديل ملف الـ PDF: " + ex.Message));
            }

            byte[] reportBytes = Encoding.UTF8.GetBytes(reportBuilder.ToString());
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] fullTextBytes = new byte[bom.Length + reportBytes.Length];
            Buffer.BlockCopy(bom, 0, fullTextBytes, 0, bom.Length);
            Buffer.BlockCopy(reportBytes, 0, fullTextBytes, bom.Length, reportBytes.Length);

            return Result.Success(new PdfVerificationResult
            {
                Success = true,
                ReportFile = annotatedPdfBytes,
                TextReportFile = fullTextBytes,
                MatchedCount = matchedCount,
                ErrorCount = errorCount
            });
        }
    }
}
