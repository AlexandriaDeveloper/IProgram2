
using Microsoft.AspNetCore.Http;

namespace Application.Services
{
    public class UploadFile
    {
        private readonly IFormFile _file;

        public UploadFile(IFormFile file)
        {
            this._file = file;
        }


        public async Task<string> UploadFileToTempPath()
        {
            UploadFile UploadFile = new UploadFile(_file);
            var tempPath = CopyFile(_file);
            using (var fileStream = new FileStream(tempPath, FileMode.Create))
            {
                await _file.CopyToAsync(fileStream);
            }
            return tempPath;

        }

        private string CopyFile(IFormFile file)
        {
            var ext = Path.GetExtension(file.FileName);
            var tempPath = Path.GetTempPath();
            var fileName = Guid.NewGuid().ToString() + ext;
            tempPath = Path.Combine(tempPath, fileName);
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            return tempPath;
        }

    }
}