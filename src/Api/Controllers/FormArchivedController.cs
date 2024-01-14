
using Application.Dtos;
using Application.Dtos.Requests;
using Application.Features;
using Application.Helpers;
using Application.Shared;
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
        public async Task<Result> MoveFormDailyToArchives([FromBody] MoveFromArchiveToDaily request)
        {
            return await _formArchivedService.MoveFormArchiveToDaily(request);
        }
        [HttpGet("getArchivedForms")]

        public async Task<Result<PaginatedResult<FormArchivedDto>>> getArchivedForms([FromQuery] FormArchivedParam param)
        {
            var result = await _formArchivedService.GetArchivedForms(param);
            return result;
        }

        [HttpDelete("{id}")]
        public async Task<Result> SoftDelete(int id)
        {
            var result = await _formArchivedService.SoftDelete(id);
            return result;
        }

        [HttpPost("deleteMultiForms")]
        public async Task<Result> SoftDeleteMultiForms([FromBody] int[] ids)
        {
            var result = await _formArchivedService.SoftDeleteMultiForms(ids);
            return result;
        }
    }
}