using Microsoft.AspNetCore.Http;

namespace Application.Dtos
{
    public class UploadJsonFormRequest
    {
        public IFormFile File { get; set; }
    }
}