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
        [HttpPost("UploadDailyReference")]

        public async Task<IActionResult> UploadDailyReference([FromForm] DailyReferenceFileUploadRequest request)
        {
            if (request.File == null || request.File.Length == 0)
            {
                return HandleResult(Result.Failure(new Error("400", "الملف فارغ.")));
            }

            var result = await _dailyReferenceService.UploadReference(request);

            return HandleResult(result);
        }
    }
}