
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Microsoft.AspNetCore.Mvc;
using Persistence.Helpers;

namespace Api.Controllers
{
    public class FormArchivedController : BaseApiController
    {
        private readonly FormArchivedService _formArchivedService;
        public FormArchivedController(FormArchivedService formService)
        {
            this._formArchivedService = formService;

        }

        [HttpPut("MoveFormArchiveToDaily")]
        public async Task<IActionResult> MoveFormDailyToArchives([FromBody] MoveFromArchiveToDaily request)
        {
            if (!ModelState.IsValid)
            {
                return HandleResult(Result.ValidationErrors<FormArchivedDto>(ModelState.SelectMany(x => x.Value.Errors)));
            }
            return HandleResult(await _formArchivedService.MoveFormArchiveToDaily(request));
        }
        [HttpGet("getArchivedForms")]

        public async Task<IActionResult> getArchivedForms([FromQuery] FormArchivedParam param)
        {
            var result = await _formArchivedService.GetArchivedForms(param);
            return HandleResult<PaginatedResult<FormArchivedDto>>(result);// result;
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> SoftDelete(int id)
        {
            var result = await _formArchivedService.SoftDelete(id);
            return HandleResult(result);// result;
        }

        [HttpPost("deleteMultiForms")]
        public async Task<IActionResult> SoftDeleteMultiForms([FromBody] int[] ids)
        {
            var result = await _formArchivedService.SoftDeleteMultiForms(ids);
            return HandleResult(result);// result;
        }
    }
}