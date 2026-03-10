using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

class Program
{
    static void Main()
    {
        var path = @"F:\Prog-Projects\IProgram\src\Api\Content\DailyReferences\86_20260311003225729.pdf";
        StringBuilder sb = new StringBuilder();
        
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read))
        using (var document = PdfDocument.Open(stream, new ParsingOptions { UseLenientParsing = true }))
        {
            var page = document.GetPage(1);
            var words = page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ToList();
            
            var allNumbers = new List<(double Amount, double Bottom, double Left)>();
            var nationalIdParts = new List<(string Text, double Bottom, double Left)>();

            foreach (var w in words)
            {
                string text = NormalizeArabicNumerals(w.Text).Replace(",", "");
                
                // Collect all amounts
                if (Regex.IsMatch(text, @"^\d+(\.\d{1,2})?$"))
                {
                    if (double.TryParse(text, out double amount))
                    {
                        allNumbers.Add((amount, w.BoundingBox.Bottom, w.BoundingBox.Left));
                    }
                }

                // Collect ID parts (Right column)
                if (w.BoundingBox.Left > 700 && w.BoundingBox.Left < 780 && Regex.IsMatch(text, @"^\d+$"))
                {
                    if (text.Length >= 10 || text.Length == 2 || text.Length == 1)
                    {
                        nationalIdParts.Add((text, w.BoundingBox.Bottom, w.BoundingBox.Left));
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

            sb.AppendLine("=== EXTRACTED NET PAYS ===");
            foreach (var idInfo in reconstructedIds)
            {
                // Find all numbers belonging to this employee's row block
                // Usually the ID is at the top right of the block, and the numbers span from Id.Bottom + 10 down to Id.Bottom - 60
                var blockNumbers = allNumbers.Where(n => n.Bottom <= idInfo.Bottom + 15 && n.Bottom >= idInfo.Bottom - 60).ToList();
                
                if (blockNumbers.Any())
                {
                    // Net Pay is the leftmost column
                    var netPay = blockNumbers.OrderBy(n => n.Left).First();
                    sb.AppendLine($"ID: {idInfo.Id} => NET PAY: {netPay.Amount} (Bottom: {netPay.Bottom:F2}, Left: {netPay.Left:F2})");
                }
                else
                {
                    sb.AppendLine($"ID: {idInfo.Id} => NO NUMBERS FOUND IN BLOCK");
                }
            }
        }
        File.WriteAllText("output_utf8.txt", sb.ToString(), Encoding.UTF8);
    }

    static string NormalizeArabicNumerals(string input)
    {
        return input.Replace('٠', '0').Replace('١', '1').Replace('٢', '2')
                    .Replace('٣', '3').Replace('٤', '4').Replace('٥', '5')
                    .Replace('٦', '6').Replace('٧', '7').Replace('٨', '8').Replace('٩', '9')
                    .Replace('٫', '.'); // Replace Arabic decimal separator too
    }
}
