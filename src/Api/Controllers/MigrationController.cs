using Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MigrationController : ControllerBase
    {
        private readonly DataMigrationService _migrationService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MigrationController> _logger;

        public MigrationController(
            DataMigrationService migrationService,
            IConfiguration configuration,
            ILogger<MigrationController> logger)
        {
            _migrationService = migrationService;
            _configuration = configuration;
            _logger = logger;
        }

        private bool IsMigrationEnabled() => _configuration.GetValue<bool>("LegacyMigration:Enabled", false);

        /// <summary>
        /// Full sync from SQL Server to Supabase (Insert + Update + Delete)
        /// </summary>
        [Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
        [HttpPost("sync")]
        public async Task<IActionResult> FullSync([FromQuery] bool force = false)
        {
            if (!IsMigrationEnabled())
            {
                return NotFound(new { success = false, message = "Legacy migration feature is disabled." });
            }

            try
            {
                var result = await _migrationService.FullSyncToSupabaseAsync(force);

                if (result.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        message = "Full sync completed successfully!",
                        duration = $"{result.Duration.TotalSeconds:F2}s",
                        tables = result.Tables.Select(t => new
                        {
                            table = t.TableName,
                            source = t.SourceCount,
                            upserted = t.Upserted,
                            deleted = t.Deleted,
                            success = t.Success,
                            error = t.Error
                        })
                    });
                }
                else
                {
                    if (result.Error == "VERSION_CONFLICT")
                    {
                        return Conflict(new
                        {
                            success = false,
                            message = "VERSION_CONFLICT",
                            error = "Supabase has newer data than local database. Please pull changes or force sync.",
                        });
                    }

                    return BadRequest(new
                    {
                        success = false,
                        message = "Sync failed",
                        error = result.Error,
                        duration = $"{result.Duration.TotalSeconds:F2}s",
                        tables = result.Tables
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Full sync failed.");
                return BadRequest(new
                {
                    success = false,
                    message = "Sync failed"
                });
            }
        }

        /// <summary>
        /// Legacy migrate endpoint (calls full sync)
        /// </summary>
        [Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
        [HttpPost("migrate")]
        public async Task<IActionResult> Migrate()
        {
            if (!IsMigrationEnabled())
            {
                return NotFound(new { success = false, message = "Legacy migration feature is disabled." });
            }

            return await FullSync();
        }

        /// <summary>
        /// Pull data from Supabase to SQL Server (Reverse Sync)
        /// </summary>
        [Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")]
        [HttpPost("pull")]
        public async Task<IActionResult> PullFromCloud()
        {
            if (!IsMigrationEnabled())
            {
                return NotFound(new { success = false, message = "Legacy migration feature is disabled." });
            }

            try
            {
                var result = await _migrationService.PullFromSupabaseAsync();

                if (result.Success)
                {
                    return Ok(new
                    {
                        success = true,
                        message = "Pull from cloud completed successfully!",
                        duration = $"{result.Duration.TotalSeconds:F2}s",
                        tables = result.Tables.Select(t => new
                        {
                            table = t.TableName,
                            source = t.SourceCount,
                            upserted = t.Upserted,
                            deleted = t.Deleted,
                            success = t.Success,
                            error = t.Error
                        })
                    });
                }
                else
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Pull failed",
                        error = result.Error,
                        duration = $"{result.Duration.TotalSeconds:F2}s",
                        tables = result.Tables
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pull from cloud failed.");
                return BadRequest(new
                {
                    success = false,
                    message = "Pull failed"
                });
            }
        }
    }
}
