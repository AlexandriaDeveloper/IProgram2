using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Application.Helpers
{
    public static class FileSecurityValidator
    {
        public const long MaxPdfVerificationBytes = 52428800L; // 50 MB
        public const long MaxDailyReferenceBytes = 31457280L;   // 30 MB
        public const long MaxExcelJsonBytes = 20971520L;        // 20 MB
        public const long MaxEmployeeUploadBytes = 10485760L;   // 10 MB

        private static readonly byte[] PdfMagic = new byte[] { 0x25, 0x50, 0x44, 0x46 }; // %PDF
        private static readonly byte[] PngMagic = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        private static readonly byte[] JpegMagic = new byte[] { 0xFF, 0xD8, 0xFF };
        private static readonly byte[] ZipMagic = new byte[] { 0x50, 0x4B, 0x03, 0x04 }; // XLSX is a ZIP package
        private static readonly byte[] OleMagic = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }; // Legacy XLS

        public static Result ValidateFile(
            IFormFile file,
            long maxSizeBytes,
            string[] allowedExtensions,
            bool validateContentSignature = true)
        {
            if (file == null || file.Length == 0)
            {
                return Result.Failure(new Error("400", "الملف فارغ أو غير موجود."));
            }

            if (file.Length > maxSizeBytes)
            {
                var maxMb = maxSizeBytes / (1024 * 1024);
                return Result.Failure(new Error("400", $"حجم الملف يتجاوز الحد المسموح به ({maxMb} ميجابايت)."));
            }

            var extension = Path.GetExtension(file.FileName)?.ToLowerInvariant();
            if (string.IsNullOrEmpty(extension) || !allowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                var allowed = string.Join(", ", allowedExtensions);
                return Result.Failure(new Error("400", $"نوع الملف غير مسموح به. الامتدادات المسموحة: {allowed}"));
            }

            if (validateContentSignature)
            {
                var signatureValid = ValidateContentSignature(file, extension);
                if (!signatureValid)
                {
                    return Result.Failure(new Error("400", "محتوى الملف لا يتطابق مع نوع الامتداد المسموح به."));
                }
            }

            return Result.Success();
        }

        private static bool ValidateContentSignature(IFormFile file, string extension)
        {
            try
            {
                using var stream = file.OpenReadStream();
                if (extension == ".json")
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                    char[] buffer = new char[512];
                    int charsRead = reader.Read(buffer, 0, buffer.Length);
                    if (charsRead <= 0) return false;
                    
                    var text = new string(buffer, 0, charsRead).TrimStart();
                    if (string.IsNullOrEmpty(text)) return false;
                    return text.StartsWith("{") || text.StartsWith("[");
                }

                byte[] header = new byte[8];
                int bytesRead = stream.Read(header, 0, header.Length);
                if (bytesRead < 3) return false;

                switch (extension)
                {
                    case ".pdf":
                        return bytesRead >= 4 && header.Take(4).SequenceEqual(PdfMagic);

                    case ".png":
                        return bytesRead >= 8 && header.SequenceEqual(PngMagic);

                    case ".jpg":
                    case ".jpeg":
                        return bytesRead >= 3 && header.Take(3).SequenceEqual(JpegMagic);

                    case ".xlsx":
                        return bytesRead >= 4 && header.Take(4).SequenceEqual(ZipMagic);

                    case ".xls":
                        return bytesRead >= 8 && header.SequenceEqual(OleMagic);

                    default:
                        return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
