
using Application.Features;
using Application.Helpers;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Persistence.Helpers;


namespace Api.Controllers
{
    public class DailyController : BaseApiController
    {
        private readonly DailyService _dailyService;

        public DailyController(DailyService dailyService)
        {
            this._dailyService = dailyService;
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

        [HttpDelete("delete/{id}")]
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
    }
}