using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class FormRefernceFileUploadRequest
    {
        public int FormId { get; set; }
        public IFormFile File { get; set; }
    }

}