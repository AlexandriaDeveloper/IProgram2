using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class EmployeeRefernceFileUploadRequest
    {
        public string EmployeeId { get; set; }
        public IFormFile File { get; set; }
    }

}