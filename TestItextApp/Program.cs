using System;
using System.IO;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Colors;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Font;
using iText.IO.Font;

class Program {
    static void Main() {
        try {
            byte[] pdfBytes = File.ReadAllBytes(@"F:\Prog-Projects\IProgram\src\Api\bin\Debug\net10.0\Content\DailyReferences\1097_20260417184734755.pdf");
            using var itextInputStream = new MemoryStream(pdfBytes);
            using var outStream = new MemoryStream();
            using (var pdfReader = new PdfReader(itextInputStream))
            using (var pdfWriter = new PdfWriter(outStream))
            using (var pdfDoc = new PdfDocument(pdfReader, pdfWriter))
            {
                var page = pdfDoc.GetPage(1);
                var canvas = new PdfCanvas(page);
                var extGState = new PdfExtGState().SetFillOpacity(0.3f);
                canvas.SetExtGState(extGState);
                canvas.SetFillColor(ColorConstants.GREEN);
                canvas.Rectangle(20, 20, 100, 100);
                canvas.Fill();
                
                var fontPath = @"C:\Windows\Fonts\arial.ttf";
                if (File.Exists(fontPath))
                {
                    var font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);
                    canvas.SetExtGState(new PdfExtGState().SetFillOpacity(1.0f));
                    canvas.SetFillColor(ColorConstants.BLACK);
                    canvas.BeginText();
                    canvas.SetFontAndSize(font, 14);
                    canvas.MoveText(50, 50);
                    canvas.ShowText("Test");
                    canvas.EndText();
                }
                pdfDoc.Close();
            }
            Console.WriteLine("Success! Size: " + outStream.ToArray().Length);
        } catch (Exception ex) {
            Console.WriteLine("Error: " + ex.ToString());
        }
    }
}
