using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class EmployeeFileUploadRequest
    {
        public IFormFile File { get; set; }
    }

}