using Microsoft.AspNetCore.Http;

namespace Application.Dtos
{
    public class UploadEmployeesToFormRequest
    {
        public int FormId { get; set; }
        public IFormFile File { get; set; }
    }
}