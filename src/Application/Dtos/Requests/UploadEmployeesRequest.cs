using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class UploadEmployeesRequest
    {
        public IFormFile[] Files { get; set; }
    }
}