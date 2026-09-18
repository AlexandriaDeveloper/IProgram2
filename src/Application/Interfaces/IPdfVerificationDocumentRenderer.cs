using System.Collections.Generic;
using Application.Features;

namespace Application.Interfaces
{
    public interface IPdfVerificationDocumentRenderer
    {
        byte[] RenderAnnotatedPdf(byte[] pdfBytes, IReadOnlyList<PdfAnnotation> annotations);
    }
}
