using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Application.Features;
using Application.Interfaces;
using iText.IO.Font;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;

namespace Application.Services
{
    public class PdfVerificationDocumentRenderer : IPdfVerificationDocumentRenderer
    {
        public byte[] RenderAnnotatedPdf(byte[] pdfBytes, IReadOnlyList<PdfAnnotation> annotations)
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                throw new ArgumentException("PDF bytes cannot be null or empty.", nameof(pdfBytes));
            }

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
                    try
                    {
                        font = PdfFontFactory.CreateFont(fontPath, PdfEncodings.IDENTITY_H);
                    }
                    catch
                    {
                        // Fallback gracefully if font file cannot be read
                        font = null;
                    }
                }

                var pageGroups = (annotations ?? Array.Empty<PdfAnnotation>()).GroupBy(a => a.PageNumber);

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

                            canvas.ShowText(ann.Message ?? string.Empty);
                            canvas.EndText();
                        }

                        // Stack upwards if there are multiple annotations on the same page
                        yOffset += boxHeight + 10;
                    }
                }
                pdfDoc.Close();
            }

            return outStream.ToArray();
        }
    }
}
