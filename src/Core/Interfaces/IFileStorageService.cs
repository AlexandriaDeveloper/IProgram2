using System.IO;
using System.Threading.Tasks;

namespace Core.Interfaces
{
    public interface IFileStorageService
    {
        Task<string> UploadFileAsync(Stream fileStream, string fileName, string folderName = "DailyReferences");
        Task<bool> DeleteFileAsync(string fileUrl, string folderName = "DailyReferences");
        string GetProtectedUrl(string fileUrl, string folderName = "DailyReferences");
        Task<(Stream stream, string contentType, string fileName)?> DownloadFileStreamAsync(string fileUrl, string folderName = "DailyReferences");
    }
}
