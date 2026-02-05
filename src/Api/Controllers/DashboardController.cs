using Application.Interfaces;
using Application.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    public class DashboardController : BaseApiController
    {
        private readonly IDashboardService _dashboardService;

        public DashboardController(IDashboardService dashboardService)
        {
            _dashboardService = dashboardService;
        }

        [HttpGet]
        
        public async Task<IActionResult> GetDashboardStats([FromQuery] DashboardFilterRequest request)
        {
            var stats = await _dashboardService.GetDashboardStatsAsync(request);
            return Ok(stats);
        }
    }
}
