
using Application.Features;
using Application.Helpers;
using Application.Shared;
using Application.Shared.ErrorResult;
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

        public async Task<Result<DailyDto>> AddDaily(DailyDto form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure<DailyDto>(new Error("400", ModelState.Values.SelectMany(x => x.Errors).Select(x => x.ErrorMessage).FirstOrDefault()));
            }
            var result = await _dailyService.AddDaily(form, cancellationToken);

            return result;
        }
        [HttpPut]

        public async Task<Result<DailyDto>> EditDaily(DailyDto form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure<DailyDto>(new Error("400", ModelState.Values.SelectMany(x => x.Errors).Select(x => x.ErrorMessage).FirstOrDefault()));
            }
            var result = await _dailyService.EditDaily(form, cancellationToken);

            return result;
        }

        [HttpGet()]

        public async Task<Result<PaginatedResult<DailyDto>>> GetDailies([FromQuery] DailyParam form, CancellationToken cancellationToken)
        {
            if (!ModelState.IsValid)
            {
                return Result.Failure<PaginatedResult<DailyDto>>(new Error("400", ModelState.Values.SelectMany(x => x.Errors).Select(x => x.ErrorMessage).FirstOrDefault()));
            }
            var result = await _dailyService.GetDailies(form, cancellationToken);

            //  return result;

            return result;
        }

        [HttpDelete("softdelete/{id}")]
        public async Task<Result> SoftDelete(int id, CancellationToken cancellationToken)
        {
            var result = await _dailyService.SoftDeleteDaily(id, cancellationToken);
            return result;
        }

        [HttpGet("exportPdf/{dailyId}")]


        public async Task<ActionResult> GetDailyById(int dailyId)
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
    }
}