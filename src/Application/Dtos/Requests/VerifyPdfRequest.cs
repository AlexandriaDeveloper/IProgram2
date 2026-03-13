using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class VerifyPdfRequest
    {
        public IFormFile File { get; set; }
    }
}
