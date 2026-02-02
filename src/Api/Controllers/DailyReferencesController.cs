using System.Threading.Tasks;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    public class DailyReferencesController : BaseApiController
    {
        private readonly DailyReferenceService _dailyReferenceService;

        public DailyReferencesController(DailyReferenceService dailyReferenceService)
        {
            _dailyReferenceService = dailyReferenceService;
        }
   
      
        [AllowAnonymous]
        [HttpGet("TestConnection")]
        public async Task<IActionResult> TestConnection()
        {
            Console.WriteLine("[DEBUG] TestConnection hit! Testing Cloudinary...");
            var result = await _dailyReferenceService.TestCloudinaryConnection();
            Console.WriteLine($"[DEBUG] Cloudinary Test Result: {result}");
            
            if (result.StartsWith("FAILED"))
                 return BadRequest(new { message = result });

            return Ok(new { message = "Connection Successful & Cloudinary Uploaded", url = result });
        }

        [AllowAnonymous]
        [HttpPost("UploadDailyReference")]
        public async Task<IActionResult> UploadDailyReference([FromForm] DailyReferenceFileUploadRequest request)
        {
            Console.WriteLine($"[DEBUG] UploadDailyReference called. Files: {(request.File != null ? "1" : "0")}, DailyId: {request.DailyId}");
            if (request.File == null || request.File.Length == 0)
            {
                Console.WriteLine("[DEBUG] File is empty or null.");
                return HandleResult(Result.Failure(new Error("400", "الملف فارغ.")));
            }

            var result = await _dailyReferenceService.UploadReference(request);
            Console.WriteLine($"[DEBUG] Service result: Success={result.IsSuccess}, Error={result.Error?.Message}");

            return HandleResult(result);
        }

        [AllowAnonymous]
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteDailyReference(int id)
        {
            var result = await _dailyReferenceService.DeleteReference(id);

            return HandleResult(result);
        }
    }
}