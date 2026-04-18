using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace Application.Features
{
    public class PdfEmployeeRecord
    {
        public string NationalId { get; set; }
        public string TegaraCode { get; set; }
        public string Name { get; set; }
        public double NetPay { get; set; }
        public double TotalEntitlements { get; set; }
        public int PageNumber { get; set; }
        public double BoundingBoxBottom { get; set; }
        public double BoundingBoxTop { get; set; }
    }

    public class PayrollPdfParserService
    {
        private readonly ILogger<PayrollPdfParserService> _logger;

        public PayrollPdfParserService(ILogger<PayrollPdfParserService> logger)
        {
            _logger = logger;
        }

        private string NormalizeArabicNumerals(string input)
        {
            return input.Replace('٠', '0').Replace('١', '1').Replace('٢', '2')
                        .Replace('٣', '3').Replace('٤', '4').Replace('٥', '5')
                        .Replace('٦', '6').Replace('٧', '7').Replace('٨', '8').Replace('٩', '9')
                        .Replace('٫', '.');
        }

        public Dictionary<string, double> ParseNetPayFromPdf(Stream pdfStream)
        {
            // Legacy method - keeping for backwards compatibility if needed elsewhere
            var dict = new Dictionary<string, double>();
            var records = ParseFullEmployeeDataFromPdf(pdfStream);
            foreach (var record in records)
            {
                dict[record.NationalId] = record.NetPay;
            }
            return dict;
        }

        public List<PdfEmployeeRecord> ParseFullEmployeeDataFromPdf(Stream pdfStream)
        {
            var records = new List<PdfEmployeeRecord>();
            var debugLines = new List<string>();

            try
            {
                using (var document = PdfDocument.Open(pdfStream, new ParsingOptions { UseLenientParsing = true }))
                {
                    foreach (var page in document.GetPages())
                    {
                        var words = page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ToList();

                        if (page.Number == 1)
                        {
                            foreach (var w in words)
                            {
                                debugLines.Add($"Left: {w.BoundingBox.Left:F1}, Bottom: {w.BoundingBox.Bottom:F1}, Text: {w.Text}");
                            }
                        }

                        bool isOraclePayslip = words.Any(w => w.Text == "ﻞﻛﺍﺭﻭﺃ" || w.Text == "ﻰﻓﺎﺼﻟﺍ" || w.Text == "ﻲﻓﺎﺼﻟﺍ");

                        if (isOraclePayslip)
                        {
                            var record = new PdfEmployeeRecord();

                            var idWord = words.FirstOrDefault(w => Regex.IsMatch(NormalizeArabicNumerals(w.Text), @"^\d{14}$"));
                            if (idWord == null) continue;

                            record.NationalId = NormalizeArabicNumerals(idWord.Text);
                            record.PageNumber = page.Number;
                            record.BoundingBoxTop = idWord.BoundingBox.Top + 20;
                            record.BoundingBoxBottom = idWord.BoundingBox.Bottom - 20;
                            double headerBottom = idWord.BoundingBox.Bottom;

                            // In Arabic PDFs, words are typically drawn Left-to-Right by the engine, 
                            // but the visual characters are right to left. Reverting back to OrderByDescending.
                            // Words and their internal letters might be stored by the PDF generator in visual left-to-right order.
                            // First, we order the words right-to-left on the page.
                            // Second, because the characters within the words might also be backwards, we reverse the characters of each word.
                            var nameWordsNodes = words.Where(w => Math.Abs(w.BoundingBox.Bottom - headerBottom) < 10
                                                          && w.BoundingBox.Left > 400 && w.BoundingBox.Left < 600
                                                          && !w.Text.Contains("اسم")
                                                          && !w.Text.Contains("الموظف")
                                                          && !w.Text.Contains("ﻢﺳﺍ") // Reverse joined shapes from PDF
                                                          && !w.Text.Contains("ﻒﻇﻮﻤﻟﺍ")
                                                          && w.Text != ":"
                                                          && !Regex.IsMatch(NormalizeArabicNumerals(w.Text), @"^\d+(\.\d+)?$")
                                                          && !Regex.IsMatch(w.Text, @"^[٠١٢٣٤٥٦٧٨٩]+$"))
                                                      .OrderByDescending(w => w.BoundingBox.Left); // Ensure right-to-left physical order

                            var resolvedNameWords = nameWordsNodes.Select(w => new string(w.Text.Reverse().ToArray()));
                            record.Name = string.Join(" ", resolvedNameWords).Trim();

                            var codeWord = words.FirstOrDefault(w => Math.Abs(w.BoundingBox.Bottom - headerBottom) < 10
                                                              && w.BoundingBox.Left > 180 && w.BoundingBox.Left < 300);
                            if (codeWord != null)
                            {
                                record.TegaraCode = NormalizeArabicNumerals(codeWord.Text).Trim();
                            }

                            var netPayLabel = words.FirstOrDefault(w => w.Text == "ﻰﻓﺎﺼﻟﺍ" || w.Text == "ﻲﻓﺎﺼﻟﺍ" || w.Text.Contains("ﻰﻓﺎﺼﻟﺍ") || w.Text.Contains("ﻲﻓﺎﺼﻟﺍ"));
                            if (netPayLabel != null)
                            {
                                var netPayAmountWord = words.Where(w => Math.Abs(w.BoundingBox.Bottom - netPayLabel.BoundingBox.Bottom) < 10 && w.BoundingBox.Left < netPayLabel.BoundingBox.Left)
                                                            .OrderByDescending(w => w.BoundingBox.Left) // Closest to the label
                                                            .FirstOrDefault(w => Regex.IsMatch(NormalizeArabicNumerals(w.Text).Replace(",", "").Replace("،", ""), @"^\d+(\.\d{1,2})?$"));

                                if (netPayAmountWord != null && double.TryParse(NormalizeArabicNumerals(netPayAmountWord.Text).Replace(",", "").Replace("،", ""), out double netPay))
                                {
                                    record.NetPay = netPay;
                                }
                            }

                            var totalEntLabel = words.FirstOrDefault(w => w.Text.Contains("ﺕﺎﻗﺎﻘﺤﺘﺳﻻﺍ") && words.Any(w2 => Math.Abs(w2.BoundingBox.Bottom - w.BoundingBox.Bottom) < 10 && w2.Text.Contains("ﻰﻟﺎﻤﺟﺃ")));

                            // Fallback if exactly matching that line fails
                            if (totalEntLabel == null)
                            {
                                // Specifically look for "ﻰﻟﺎﻤﺟﺃ" (Total) that is close to the bottom part where totals usually are
                                totalEntLabel = words.Where(w => w.Text.Contains("ﻰﻟﺎﻤﺟﺃ") || w.Text.Contains("ﻰﻟﺎﻤﺟﺇ")).OrderBy(w => w.BoundingBox.Bottom).FirstOrDefault();
                            }

                            if (totalEntLabel != null)
                            {
                                var totalAmountWord = words.Where(w => Math.Abs(w.BoundingBox.Bottom - totalEntLabel.BoundingBox.Bottom) < 10 && w.BoundingBox.Left < totalEntLabel.BoundingBox.Left)
                                                           .OrderByDescending(w => w.BoundingBox.Left)
                                                           .FirstOrDefault(w => Regex.IsMatch(NormalizeArabicNumerals(w.Text).Replace(",", "").Replace("،", ""), @"^\d+(\.\d{1,2})?$"));

                                if (totalAmountWord != null && double.TryParse(NormalizeArabicNumerals(totalAmountWord.Text).Replace(",", "").Replace("،", ""), out double total))
                                {
                                    record.TotalEntitlements = total;
                                }
                            }

                            records.Add(record);
                        }
                        else
                        {
                            var allNumbers = new List<(double Amount, double Bottom, double Left)>();
                            var nationalIdParts = new List<(string Text, double Bottom, double Left)>();
                            var nameWordsNodes = new List<(string Text, double Bottom, double Left)>();
                            var codeCandidates = new List<(string Text, double Bottom, double Left)>();

                            foreach (var w in words)
                            {
                                string text = NormalizeArabicNumerals(w.Text).Replace(",", "").Replace("،", "");
                                double bottom = w.BoundingBox.Bottom;
                                double left = w.BoundingBox.Left;

                                if (Regex.IsMatch(text, @"^\d+(\.\d{1,2})?$"))
                                {
                                    if (double.TryParse(text, out double amount))
                                    {
                                        allNumbers.Add((amount, bottom, left));
                                    }
                                }

                                if (left > 700 && left < 780 && Regex.IsMatch(text, @"^\d+$"))
                                {
                                    if (text.Length >= 10 || text.Length == 2 || text.Length == 1)
                                    {
                                        nationalIdParts.Add((text, bottom, left));
                                    }
                                }

                                if (left >= 580 && left <= 715 && !Regex.IsMatch(text, @"^\d+([\.,]\d+)?$") && !text.Contains("_"))
                                {
                                    nameWordsNodes.Add((w.Text, bottom, left));
                                }

                                if (left >= 735 && left <= 760 && Regex.IsMatch(text, @"^\d{1,5}$"))
                                {
                                    codeCandidates.Add((text, bottom, left));
                                }
                            }

                            var reconstructedIds = new List<(string Id, double Bottom)>();
                            for (int i = 0; i < nationalIdParts.Count; i++)
                            {
                                string idStr = nationalIdParts[i].Text;
                                if (idStr.Length == 14)
                                {
                                    reconstructedIds.Add((idStr, nationalIdParts[i].Bottom));
                                }
                                else if (idStr.Length >= 10 && idStr.Length < 14)
                                {
                                    string fullId = idStr;
                                    int j = i + 1;
                                    while (j < nationalIdParts.Count && fullId.Length < 14)
                                    {
                                        if (nationalIdParts[i].Bottom - nationalIdParts[j].Bottom < 40)
                                        {
                                            fullId += nationalIdParts[j].Text;
                                        }
                                        j++;
                                    }

                                    if (fullId.Length >= 14)
                                    {
                                        reconstructedIds.Add((fullId.Substring(0, 14), nationalIdParts[i].Bottom));
                                    }
                                }
                            }

                            foreach (var idInfo in reconstructedIds)
                            {
                                var record = new PdfEmployeeRecord { NationalId = idInfo.Id, PageNumber = page.Number };
                                double topBoundary = idInfo.Bottom + 25;
                                double bottomBoundary = idInfo.Bottom - 45;
                                record.BoundingBoxTop = topBoundary;
                                record.BoundingBoxBottom = bottomBoundary;

                                var blockNameWords = nameWordsNodes
                                    .Where(n => n.Bottom <= topBoundary && n.Bottom >= bottomBoundary)
                                    .OrderByDescending(n => n.Left)
                                    .Select(n => n.Text)
                                    .ToList();
                                record.Name = string.Join(" ", blockNameWords);

                                var codeNode = codeCandidates
                                    .Where(c => c.Bottom <= topBoundary + 20 && c.Bottom >= idInfo.Bottom - 10)
                                    .OrderByDescending(c => c.Bottom)
                                    .FirstOrDefault();

                                if (codeNode.Text != null)
                                {
                                    record.TegaraCode = codeNode.Text;
                                }

                                var blockNumbers = allNumbers.Where(n => n.Bottom <= topBoundary && n.Bottom >= bottomBoundary).ToList();

                                if (blockNumbers.Any())
                                {
                                    var netPayNode = blockNumbers.Where(n => n.Left < 80).OrderBy(n => n.Left).FirstOrDefault();
                                    if (netPayNode != default) record.NetPay = netPayNode.Amount;

                                    var entitlementsNode = blockNumbers.Where(n => n.Left >= 80 && n.Left < 120).OrderBy(n => n.Left).FirstOrDefault();
                                    if (entitlementsNode != default) record.TotalEntitlements = entitlementsNode.Amount;
                                }

                                records.Add(record);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while parsing PDF for full employee data.");
            }

            if (records.Count == 0 && debugLines.Any())
            {
                try
                {
                    File.WriteAllLines(@"f:\Prog-Projects\IProgram\tests\pdf_debug.txt", debugLines);
                    _logger.LogWarning("Saved PDF debug dump because 0 records were found.");
                }
                catch (Exception debugEx)
                {
                    _logger.LogError(debugEx, "Failed to write debug dump.");
                }
            }

            return records;
        }
    }
}
