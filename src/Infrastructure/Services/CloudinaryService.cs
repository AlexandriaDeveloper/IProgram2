using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Auth.Infrastructure.Services
{
    public class CloudinaryService : IFileStorageService
    {
        private readonly Cloudinary _cloudinary;
        private readonly IConfiguration _configuration;

        public CloudinaryService(IConfiguration configuration)
        {
            _configuration = configuration;
            try 
            {
                var cloudName = _configuration["Cloudinary:CloudName"];
                var apiKey = _configuration["Cloudinary:ApiKey"];
                var apiSecret = _configuration["Cloudinary:ApiSecret"];

                if (!string.IsNullOrEmpty(cloudName) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
                {
                    var account = new Account(cloudName, apiKey, apiSecret);
                    _cloudinary = new Cloudinary(account);
                    _cloudinary.Api.Secure = true;
                }
                else
                {
                    Console.WriteLine("Cloudinary configuration is missing in appsettings.json");
                    throw new Exception("Cloudinary configuration is missing in appsettings.json");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CRITICAL] Failed to initialize CloudinaryService: {ex.Message}");
                // We don't throw here to allow the service to be created, but methods will fail.
                // Or we throw to see the 500. Let's log and rethrow or handle gracefully? 
                // Better to throw but ensuring it's logged to Console is active.
                throw;
            }
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string folderName = "DailyReferences")
        {
            try
            {
                if (fileStream.Position > 0)
                    fileStream.Position = 0;

                // Use RawUploadParams to allow any file type (PDF, TXT, etc.)
                // Or ImageUploadParams with ResourceType = "auto" if we want Cloudinary to decide.
                // For PDFs specifically, they can be treated as images (for thumbnails) or raw.
                // Using RawUploadParams is safer for generic file storage.
                
                var uploadParams = new RawUploadParams()
                {
                    File = new FileDescription(fileName, fileStream),
                    Folder = folderName,
                    PublicId = Path.GetFileNameWithoutExtension(fileName),
                    Overwrite = true,
                    UseFilename = true,
                    UniqueFilename = false
                };

                // Note: UploadAsync is overloaded. We need to cast or use specific method if needed, 
                // but Cloudinary .NET SDK handles params polymorphism.
                var uploadResult = await _cloudinary.UploadAsync(uploadParams);

                if (uploadResult.Error != null)
                {
                     throw new Exception($"Cloudinary Error: {uploadResult.Error.Message}");
                }

                return uploadResult.SecureUrl.ToString();
            }
            catch (Exception ex)
            {
                // Log exception if needed, or propagate
                throw new Exception($"Cloudinary Upload Failed: {ex.Message}", ex);
            }
        }

        public async Task<bool> DeleteFileAsync(string fileUrl, string folderName)
        {
            try
            {
                Console.WriteLine($"[DEBUG] Attempting to delete file logic. Url: {fileUrl}, Folder: {folderName}");

                if (string.IsNullOrEmpty(fileUrl)) return false;

                var uri = new Uri(fileUrl);
                var pathSegments = uri.AbsolutePath.Split('/');
                
                // Construct Public ID candidates
                var fileNameWithExt = pathSegments[pathSegments.Length - 1];
                var fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileNameWithExt);
                
                // For Raw files, Public ID usually includes extension
                var publicIdWithExt = $"{folderName}/{fileNameWithExt}";
                
                // For Image files, Public ID usually excludes extension
                var publicIdWithoutExt = $"{folderName}/{fileNameWithoutExt}";

                Console.WriteLine($"[DEBUG] Public Id With Ext: {publicIdWithExt}");
                Console.WriteLine($"[DEBUG] Public Id Without Ext: {publicIdWithoutExt}");

                // Try Delete as Raw (using ID with Extension)
                var deletionParamsRaw = new DeletionParams(publicIdWithExt)
                {
                    ResourceType = ResourceType.Raw
                };
                Console.WriteLine($"[DEBUG] Sending DestroyAsync (RAW) for: {publicIdWithExt}");
                var resultRaw = await _cloudinary.DestroyAsync(deletionParamsRaw);
                Console.WriteLine($"[DEBUG] Raw Delete Result: {resultRaw.Result}");

                if (resultRaw.Result == "ok") return true;

                // Fallback: Try Delete as Image (using ID without Extension)
                // Sometimes PDFs are uploaded as 'image' type, they strip extension
                var deletionParamsImg = new DeletionParams(publicIdWithoutExt)
                {
                    ResourceType = ResourceType.Image
                };
                Console.WriteLine($"[DEBUG] Sending DestroyAsync (IMAGE) for: {publicIdWithoutExt}");
                var resultImg = await _cloudinary.DestroyAsync(deletionParamsImg);
                Console.WriteLine($"[DEBUG] Image Delete Result: {resultImg.Result}");

                return resultImg.Result == "ok";
            }
            catch (Exception ex)
            {
                 Console.WriteLine($"[ERROR] Cloudinary Delete Failed: {ex.Message}");
                 return false;
            }
        }

        // Removed helper method to simplify logic inline above for clarity

    }
}
