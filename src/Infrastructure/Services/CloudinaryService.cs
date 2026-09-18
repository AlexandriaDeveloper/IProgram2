using CloudinaryDotNet;
using CloudinaryDotNet.Actions;
using Core.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Auth.Infrastructure.Services
{
    public class CloudinaryService : IFileStorageService
    {
        private readonly Cloudinary _cloudinary;
        private readonly IConfiguration _configuration;

        private static readonly Regex CloudinaryPublicIdRegex = new Regex(
            @"/(?:raw|image|video)/(?:authenticated|upload|private)/(?:s--[^/]+--/)?(?:v\d+/)?(.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
                    _cloudinary.Api.Timeout = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
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
                throw;
            }
        }

        public static string ExtractPublicIdFromUrl(string fileUrl, string folderName = "DailyReferences")
        {
            if (string.IsNullOrWhiteSpace(fileUrl)) return string.Empty;

            if (!fileUrl.Contains("://") && !fileUrl.StartsWith("/"))
            {
                return fileUrl;
            }

            try
            {
                var uri = new Uri(fileUrl, UriKind.RelativeOrAbsolute);
                var path = uri.IsAbsoluteUri ? uri.AbsolutePath : fileUrl;

                var match = CloudinaryPublicIdRegex.Match(path);
                if (match.Success && match.Groups.Count > 1)
                {
                    var rawId = match.Groups[1].Value.Trim('/');
                    return Uri.UnescapeDataString(rawId);
                }

                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    var fileName = Uri.UnescapeDataString(segments[segments.Length - 1]);
                    return !string.IsNullOrEmpty(folderName) ? $"{folderName}/{fileName}" : fileName;
                }
            }
            catch
            {
                var simpleName = Path.GetFileName(fileUrl);
                return !string.IsNullOrEmpty(folderName) ? $"{folderName}/{simpleName}" : simpleName;
            }

            return fileUrl;
        }

        public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string folderName = "DailyReferences")
        {
            try
            {
                if (fileStream.Position > 0)
                    fileStream.Position = 0;

                var safeFileName = Path.GetFileName(fileName);

                var uploadParams = new RawUploadParams()
                {
                    File = new FileDescription(safeFileName, fileStream),
                    Folder = folderName,
                    PublicId = safeFileName, // Cloudinary Raw assets require preserving the file extension in public ID
                    Overwrite = true,
                    UseFilename = true,
                    UniqueFilename = false,
                    Type = "authenticated"
                };

                RawUploadResult uploadResult;
                if (fileStream.Length > 2 * 1024 * 1024)
                {
                    uploadResult = await _cloudinary.UploadLargeAsync(uploadParams);
                }
                else
                {
                    uploadResult = await _cloudinary.UploadAsync(uploadParams);
                }

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

        public string GetProtectedUrl(string fileUrl, string folderName = "DailyReferences")
        {
            if (string.IsNullOrEmpty(fileUrl)) return string.Empty;

            if (!fileUrl.Contains("cloudinary.com", StringComparison.OrdinalIgnoreCase))
            {
                return fileUrl;
            }

            try
            {
                var uri = new Uri(fileUrl);
                // Backward compatibility: If asset was uploaded with legacy public delivery ('/raw/upload/' or '/image/upload/'), return as is
                if (uri.AbsolutePath.Contains("/raw/upload/", StringComparison.OrdinalIgnoreCase) ||
                    uri.AbsolutePath.Contains("/image/upload/", StringComparison.OrdinalIgnoreCase))
                {
                    return fileUrl;
                }

                var publicId = ExtractPublicIdFromUrl(fileUrl, folderName);
                if (string.IsNullOrEmpty(publicId))
                {
                    return fileUrl;
                }

                if (_cloudinary != null)
                {
                    var signedUrl = _cloudinary.Api.UrlImgUp
                        .ResourceType("raw")
                        .Action("authenticated")
                        .Signed(true)
                        .BuildUrl(publicId);

                    return signedUrl;
                }

                return fileUrl;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Cloudinary GetProtectedUrl Failed: {ex.Message}");
                return fileUrl;
            }
        }

        public async Task<(Stream stream, string contentType, string fileName)?> DownloadFileStreamAsync(string fileUrl, string folderName = "DailyReferences")
        {
            if (string.IsNullOrEmpty(fileUrl)) return null;

            try
            {
                var downloadUrl = GetProtectedUrl(fileUrl, folderName);
                using var httpClient = new System.Net.Http.HttpClient();
                var response = await httpClient.GetAsync(downloadUrl);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[ERROR] Cloudinary download failed with status {response.StatusCode} for folder: {folderName}");
                    return null;
                }

                var memoryStream = new MemoryStream();
                await response.Content.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                var publicId = ExtractPublicIdFromUrl(fileUrl, folderName);
                var fileName = Path.GetFileName(publicId);
                if (string.IsNullOrEmpty(fileName))
                {
                    fileName = Path.GetFileName(new Uri(fileUrl).AbsolutePath);
                }

                return (memoryStream, contentType, fileName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Cloudinary DownloadFileStreamAsync Failed: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> DeleteFileAsync(string fileUrl, string folderName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fileUrl)) return false;

                // 1. Extract public ID from URL if possible
                var extractedPublicId = ExtractPublicIdFromUrl(fileUrl, folderName);

                // Build candidate public IDs
                var candidates = new List<string>();

                if (!string.IsNullOrWhiteSpace(extractedPublicId))
                {
                    // Primary candidate: exactly as extracted (e.g. "DailyReferences/file.pdf")
                    candidates.Add(extractedPublicId);

                    // Candidate without extension (e.g. "DailyReferences/file") for legacy uploads
                    var lastSlash = extractedPublicId.LastIndexOf('/');
                    var folderPart = lastSlash >= 0 ? extractedPublicId.Substring(0, lastSlash) : "";
                    var filePart = lastSlash >= 0 ? extractedPublicId.Substring(lastSlash + 1) : extractedPublicId;
                    var filePartWithoutExt = Path.GetFileNameWithoutExtension(filePart);
                    var candidateWithoutExt = string.IsNullOrEmpty(folderPart) ? filePartWithoutExt : $"{folderPart}/{filePartWithoutExt}";

                    if (!candidates.Contains(candidateWithoutExt, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(candidateWithoutExt);
                    }
                }

                // Fallback candidate using folderName + filename from URL
                try
                {
                    if (Uri.TryCreate(fileUrl, UriKind.RelativeOrAbsolute, out var uri))
                    {
                        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : fileUrl;
                        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length > 0)
                        {
                            var rawFileName = Uri.UnescapeDataString(segments[segments.Length - 1]);
                            var candidateWithExt = !string.IsNullOrEmpty(folderName) ? $"{folderName}/{rawFileName}" : rawFileName;
                            if (!candidates.Contains(candidateWithExt, StringComparer.OrdinalIgnoreCase))
                            {
                                candidates.Add(candidateWithExt);
                            }

                            var rawFileNameWithoutExt = Path.GetFileNameWithoutExtension(rawFileName);
                            var candidateWithoutExt = !string.IsNullOrEmpty(folderName) ? $"{folderName}/{rawFileNameWithoutExt}" : rawFileNameWithoutExt;
                            if (!candidates.Contains(candidateWithoutExt, StringComparer.OrdinalIgnoreCase))
                            {
                                candidates.Add(candidateWithoutExt);
                            }
                        }
                    }
                }
                catch
                {
                    // ignore URL parsing error for candidate generation
                }

                // Determine delivery types:
                // If the URL explicitly contains "/raw/authenticated/" or "/authenticated/", try "authenticated" first.
                // If it explicitly contains "/raw/upload/" or "/upload/", try "upload" first.
                // Otherwise try "authenticated" then "upload".
                bool isExplicitUpload = fileUrl.Contains("/upload/", StringComparison.OrdinalIgnoreCase);
                var deliveryTypes = isExplicitUpload
                    ? new[] { "upload", "authenticated" }
                    : new[] { "authenticated", "upload" };

                // Try deleting using candidates
                foreach (var deliveryType in deliveryTypes)
                {
                    // 1. First try Raw assets (new authenticated assets with extension & legacy raw)
                    foreach (var candidate in candidates)
                    {
                        var destroyParamsRaw = new DeletionParams(candidate)
                        {
                            ResourceType = ResourceType.Raw,
                            Type = deliveryType
                        };

                        var resultRaw = await _cloudinary.DestroyAsync(destroyParamsRaw);
                        if (resultRaw?.Result == "ok")
                        {
                            return true;
                        }
                    }

                    // 2. Fallback to Image assets (legacy images uploaded as image)
                    foreach (var candidate in candidates)
                    {
                        var destroyParamsImg = new DeletionParams(candidate)
                        {
                            ResourceType = ResourceType.Image,
                            Type = deliveryType
                        };

                        var resultImg = await _cloudinary.DestroyAsync(destroyParamsImg);
                        if (resultImg?.Result == "ok")
                        {
                            return true;
                        }
                    }
                }

                return false;
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
