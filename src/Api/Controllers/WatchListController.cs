using Application.Dtos;
using Application.Features;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Persistence.Helpers;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    public class WatchListController : BaseApiController
    {
        private readonly WatchListService _watchListService;

        public WatchListController(WatchListService watchListService)
        {
            _watchListService = watchListService;
        }

        [HttpGet]
        public async Task<IActionResult> GetWatchList([FromQuery] WatchListParam param)
        {
            var result = await _watchListService.GetWatchList(param);
            return HandleResult(result);
        }

        [HttpPost]
        public async Task<IActionResult> AddToWatchList([FromBody] AddToWatchListRequest request)
        {
            var result = await _watchListService.AddToWatchList(request);
            return HandleResult(result);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> RemoveFromWatchList(int id)
        {
            var result = await _watchListService.RemoveFromWatchList(id);
            return HandleResult(result);
        }
    }
}
