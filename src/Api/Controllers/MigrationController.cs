using Auth.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MigrationController : ControllerBase
    {
        private readonly DataMigrationService _migrationService;

        public MigrationController(DataMigrationService migrationService)
        {
            _migrationService = migrationService;
        }

        /// <summary>
        /// Full sync from SQL Server to Supabase (Insert + Update + Delete)
        /// </summary>
        // [Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")] // Temporarily disabled for debugging
        [HttpPost("sync")]
        public async Task<IActionResult> FullSync()
        {
            try
            {
                var result = await _migrationService.FullSyncToSupabaseAsync();
                
                if (result.Success)
                {
                    return Ok(new { 
                        success = true,
                        message = "Full sync completed successfully!",
                        duration = $"{result.Duration.TotalSeconds:F2}s",
                        tables = result.Tables.Select(t => new {
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
                    return BadRequest(new { 
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
                return BadRequest(new { 
                    success = false,
                    message = "Sync failed", 
                    error = ex.Message, 
                    stackTrace = ex.StackTrace 
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
            return await FullSync();
        }
    }
}
