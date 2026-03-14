
using Application.Features;
using Application.Helpers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NPOI.Util;
using Persistence.Helpers;


namespace Api.Controllers
{
    public class DailyController : BaseApiController
    {
        private readonly DailyService _dailyService;
        private readonly PdfVerificationService _pdfVerificationService;

        public DailyController(DailyService dailyService, PdfVerificationService pdfVerificationService)
        {
            this._dailyService = dailyService;
            this._pdfVerificationService = pdfVerificationService;
        }

        [HttpPost]

        public async Task<IActionResult> AddDaily(DailyDto form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult<DailyDto>(Result.ValidationErrors<DailyDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _dailyService.AddDaily(form, cancellationToken);

            return HandleResult<DailyDto>(result);// result;
        }
        [HttpPut]

        public async Task<IActionResult> EditDaily(DailyDto form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult<DailyDto>(Result.ValidationErrors<DailyDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _dailyService.EditDaily(form, cancellationToken);

            return HandleResult<DailyDto>(result); ;
        }

        [HttpPost("copy/{dailyId}")]
        public async Task<IActionResult> CopyDaily(int dailyId, CancellationToken cancellationToken)
        {
            var result = await _dailyService.CopyDaily(dailyId, cancellationToken);
            return HandleResult(result);
        }

        [HttpPut("CloseDaily/{dailyId}")]

        public async Task<IActionResult> CloseDaily(int dailyId, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<DailyDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _dailyService.CloseDaily(dailyId, cancellationToken);

            return HandleResult(result); ;
        }


        [HttpGet()]

        public async Task<IActionResult> GetDailies([FromQuery] DailyParam form, CancellationToken cancellationToken)
        {

            var result = await _dailyService.GetDailies(form, cancellationToken);

            //  return result;

            return HandleResult<PaginatedResult<DailyDto>>(result);// result;
        }


        [HttpGet("{dailyId}")]

        public async Task<IActionResult> GetDailys(int dailyId, CancellationToken cancellationToken)
        {

            var result = await _dailyService.GetDaily(dailyId, cancellationToken);
            //  return result;
            return HandleResult<DailyDto>(result);// result;
        }


        [HttpDelete("softdelete/{id}")]
        public async Task<IActionResult> SoftDelete(int id, CancellationToken cancellationToken)
        {
            var result = await _dailyService.SoftDeleteDaily(id, cancellationToken);
            return HandleResult(result);// result;
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
        {
            var result = await _dailyService.DeleteDaily(id, cancellationToken);

            return HandleResult(result);// result;
        }

        [HttpGet("exportPdf/{dailyId}")]


        public async Task<IActionResult> GetDailyById(int dailyId)
        {
            // return await _dailyService.ExportPdf(dailyId);


            var pdf = await _dailyService.ExportPdf(dailyId);//.PrintFormWithDetailsPdf(formId);

            var path = Path.GetTempPath() + "test.pdf";


            var memory = new MemoryStream(pdf);
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/pdf", "test.pdf");
        }

        [HttpGet("exportIndexPdf/{dailyId}")]


        public async Task<IActionResult> ExportIndex(int dailyId)
        {
            // return await _dailyService.ExportPdf(dailyId);


            var pdf = await _dailyService.ExportIndexPdf(dailyId);//.PrintFormWithDetailsPdf(formId);

            var path = Path.GetTempPath() + "test.pdf";


            var memory = new MemoryStream(pdf);
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await stream.CopyToAsync(memory);
            }
            memory.Position = 0;
            return File(memory, "application/pdf", "test.pdf");
        }


        [HttpGet("download-daily/{dailyId}")]
        public async Task<FileResult> DownloadFile(int dailyId)
        {
            var ms = await _dailyService.CreateExcelFile(dailyId);

            return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "daily.xlsx");


        }
        [AllowAnonymous]
        [HttpGet("download-daily-json/{dailyId}")]
        public async Task<FileResult> DownloadJsonFile(int dailyId)
        {
            var ms = await _dailyService.CreateJSONFile(dailyId);
            return File(ms, "application/json", "daily.json");


        }

        [HttpGet("{dailyId}/beneficiaries-summary")]
        public async Task<IActionResult> GetBeneficiariesSummary(int dailyId)
        {
            var result = await _dailyService.GetBeneficiariesSummary(dailyId);
            return HandleResult(result);
        }

        [HttpPut("{dailyId}/beneficiary-comment")]
        public async Task<IActionResult> UpdateBeneficiaryComment(int dailyId, [FromBody] Application.Dtos.Requests.UpdateBeneficiaryCommentRequest request)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<Application.Dtos.Requests.UpdateBeneficiaryCommentRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _dailyService.UpdateBeneficiaryComment(dailyId, request);
            return HandleResult(result);
        }

        [HttpPut("{dailyId}/beneficiary-netpay")]
        public async Task<IActionResult> UpdateBeneficiaryNetPay(int dailyId, [FromBody] Application.Dtos.Requests.UpdateBeneficiaryNetPayRequest request)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<Application.Dtos.Requests.UpdateBeneficiaryNetPayRequest>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            var result = await _dailyService.UpdateBeneficiaryNetPay(dailyId, request);
            return HandleResult(result);
        }

        [HttpGet("exportSummaryExcel/{dailyId}")]
        public async Task<FileResult> ExportBeneficiariesSummaryExcel(int dailyId)
        {
            var ms = await _dailyService.CreateBeneficiarySummaryExcelFile(dailyId);
            return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"DailySummary_{dailyId}.xlsx");
        }

        [HttpPost("{dailyId}/verify-pdf")]
        public async Task<IActionResult> VerifyPdfAgainstSummary([FromRoute] int dailyId, [FromForm] Application.Dtos.Requests.VerifyPdfRequest request)
        {
            var file = request?.File;
            if (file == null || file.Length == 0)
                return BadRequest("يجب اختيار ملف PDF");

            var currentUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            using (var stream = file.OpenReadStream())
            {
                var result = await _pdfVerificationService.VerifyPdfAgainstSummary(dailyId, stream, currentUserId);

                if (!result.IsSuccess)
                {
                    return HandleResult(result);
                }

                return File(result.Value.ReportFile, "text/plain", $"VerifyReport_{dailyId}_{DateTime.Now:yyyyMMdd_HHmm}.txt");
            }
        }
    }
}