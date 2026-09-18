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
   
      
        [Authorize(Roles = "Admin")]
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

        [Authorize(Roles = "Admin")]
        [HttpPost("SyncLocalReferencesToCloudinary")]
        [Microsoft.AspNetCore.OutputCaching.OutputCache(NoStore = true)]
        [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
        public async Task<IActionResult> SyncLocalReferencesToCloudinary([FromQuery] int? dailyId = null)
        {
            var results = await _dailyReferenceService.SyncLocalReferencesToCloudinary(dailyId);
            return Ok(new { message = "Sync process completed", details = results });
        }

        [HttpPost("UploadDailyReference")]
        public async Task<IActionResult> UploadDailyReference([FromForm] DailyReferenceFileUploadRequest request)
        {
            var validation = FileSecurityValidator.ValidateFile(
                request?.File,
                FileSecurityValidator.MaxDailyReferenceBytes,
                new[] { ".pdf", ".jpg", ".jpeg", ".png" });

            if (!validation.IsSuccess)
            {
                return HandleResult(validation);
            }

            var result = await _dailyReferenceService.UploadReference(request);
            return HandleResult(result);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteDailyReference(int id)
        {
            var result = await _dailyReferenceService.DeleteReference(id);

            return HandleResult(result);
        }
    }
}