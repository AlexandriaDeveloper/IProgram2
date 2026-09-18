using System.IO;
using System.Text;
using Application.Helpers;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace Auth.UnitTests
{
    public class FileUploadSecurityValidatorTests
    {
        private IFormFile CreateMockFormFile(string fileName, byte[] content, string contentType = "application/octet-stream")
        {
            var stream = new MemoryStream(content);
            var fileMock = new Mock<IFormFile>();
            fileMock.Setup(f => f.FileName).Returns(fileName);
            fileMock.Setup(f => f.Length).Returns(content.Length);
            fileMock.Setup(f => f.ContentType).Returns(contentType);
            fileMock.Setup(f => f.OpenReadStream()).Returns(() => new MemoryStream(content));
            return fileMock.Object;
        }

        [Fact]
        public void ValidateFile_Fails_WhenFileIsNull()
        {
            var result = FileSecurityValidator.ValidateFile(null, 10 * 1024 * 1024, new[] { ".pdf" });

            Assert.False(result.IsSuccess);
            Assert.Equal("400", result.Error.Code);
        }

        [Fact]
        public void ValidateFile_Fails_WhenFileIsEmpty()
        {
            var file = CreateMockFormFile("test.pdf", new byte[0]);

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".pdf" });

            Assert.False(result.IsSuccess);
            Assert.Equal("400", result.Error.Code);
        }

        [Fact]
        public void ValidateFile_Fails_WhenFileSizeExceedsLimit()
        {
            var content = new byte[1024 * 1024 + 1]; // 1MB + 1 byte
            var file = CreateMockFormFile("test.pdf", content);

            var result = FileSecurityValidator.ValidateFile(file, 1024 * 1024, new[] { ".pdf" });

            Assert.False(result.IsSuccess);
            Assert.Contains("يتجاوز الحد المسموح", result.Error.Message);
        }

        [Fact]
        public void ValidateFile_Fails_WhenExtensionIsNotAllowed()
        {
            var content = Encoding.UTF8.GetBytes("malicious script");
            var file = CreateMockFormFile("script.exe", content);

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".pdf", ".xlsx" });

            Assert.False(result.IsSuccess);
            Assert.Contains("نوع الملف غير مسموح به", result.Error.Message);
        }

        [Fact]
        public void ValidateFile_Fails_WhenPdfSignatureIsSpoofed()
        {
            // Disguised text file with .pdf extension
            var content = Encoding.UTF8.GetBytes("This is not a real PDF file");
            var file = CreateMockFormFile("fake.pdf", content);

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".pdf" });

            Assert.False(result.IsSuccess);
            Assert.Contains("محتوى الملف لا يتطابق", result.Error.Message);
        }

        [Fact]
        public void ValidateFile_Succeeds_ForGenuinePdf()
        {
            // PDF magic bytes: %PDF-1.4
            var content = Encoding.ASCII.GetBytes("%PDF-1.4\n%dummy pdf content");
            var file = CreateMockFormFile("genuine.pdf", content, "application/pdf");

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".pdf" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ValidateFile_Succeeds_ForGenuineExcelXlsx()
        {
            // ZIP magic bytes: PK\x03\x04
            var content = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00 };
            var file = CreateMockFormFile("employees.xlsx", content);

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".xlsx" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ValidateFile_Succeeds_ForGenuineJson()
        {
            var content = Encoding.UTF8.GetBytes("{\"employees\": [{\"name\": \"test\"}]}");
            var file = CreateMockFormFile("form.json", content, "application/json");

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".json" });

            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ValidateFile_Fails_ForInvalidJsonContent()
        {
            var content = Encoding.UTF8.GetBytes("NOT JSON CONTENT");
            var file = CreateMockFormFile("form.json", content, "application/json");

            var result = FileSecurityValidator.ValidateFile(file, 10 * 1024 * 1024, new[] { ".json" });

            Assert.False(result.IsSuccess);
            Assert.Contains("محتوى الملف لا يتطابق", result.Error.Message);
        }
    }
}
