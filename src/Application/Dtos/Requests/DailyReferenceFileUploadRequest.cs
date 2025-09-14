using Microsoft.AspNetCore.Http;

namespace Application.Dtos.Requests
{
    public class DailyReferenceFileUploadRequest
    {
        public int DailyId { get; set; }
        public string Description { get; set; }
        public IFormFile File { get; set; }
    }
}
