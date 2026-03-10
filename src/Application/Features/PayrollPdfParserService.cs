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
            var netPayDict = new Dictionary<string, double>();

            try
            {
                using (var document = PdfDocument.Open(pdfStream, new ParsingOptions { UseLenientParsing = true }))
                {
                    foreach (var page in document.GetPages())
                    {
                        var words = page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ToList();
                        
                        var allNumbers = new List<(double Amount, double Bottom, double Left)>();
                        var nationalIdParts = new List<(string Text, double Bottom)>();

                        foreach (var w in words)
                        {
                            string text = NormalizeArabicNumerals(w.Text).Replace(",", "");
                            
                            // 1. Collect all amounts
                            if (Regex.IsMatch(text, @"^\d+(\.\d{1,2})?$"))
                            {
                                if (double.TryParse(text, out double amount))
                                {
                                    allNumbers.Add((amount, w.BoundingBox.Bottom, w.BoundingBox.Left));
                                }
                            }

                            // 2. Collect National ID parts: Right column (Left > 700) and consists of 12, 13, 14, 2, or 1 digits
                            if (w.BoundingBox.Left > 700 && w.BoundingBox.Left < 780 && Regex.IsMatch(text, @"^\d+$"))
                            {
                                if (text.Length >= 10 || text.Length == 2 || text.Length == 1)
                                {
                                    nationalIdParts.Add((text, w.BoundingBox.Bottom));
                                }
                            }
                        }

                        // Reconstruct National IDs
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
                                // Look ahead for the remaining digits (usually below it on the next line)
                                // They should be physically close, vertically.
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

                        // Match Net Pays to Reconstructed IDs by physical proximity (Y-axis)
                        foreach (var idInfo in reconstructedIds)
                        {
                            // Find all numbers belonging to this employee's row block
                            // The ID is usually around the top-mid of the block, and the numbers span from Id.Bottom + 25 down to Id.Bottom - 25
                            var blockNumbers = allNumbers.Where(n => n.Bottom <= idInfo.Bottom + 25 && n.Bottom >= idInfo.Bottom - 25).ToList();
                            
                            if (blockNumbers.Any())
                            {
                                // Net Pay is the leftmost column ("الصافى")
                                var netPay = blockNumbers.OrderBy(n => n.Left).First();
                                netPayDict[idInfo.Id] = netPay.Amount;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while parsing PDF for Net Pay.");
            }

            return netPayDict;
        }
    }
}
