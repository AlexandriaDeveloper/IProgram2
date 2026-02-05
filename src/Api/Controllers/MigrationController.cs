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
        public async Task<IActionResult> FullSync([FromQuery] bool force = false)
        {
            try
            {
                var result = await _migrationService.FullSyncToSupabaseAsync(force);
                
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
                    if (result.Error == "VERSION_CONFLICT")
                    {
                         return Conflict(new { 
                            success = false,
                            message = "VERSION_CONFLICT",
                            error = "Supabase has newer data than local database. Please pull changes or force sync.",
                        });
                    }

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

        /// <summary>
        /// Pull data from Supabase to SQL Server (Reverse Sync)
        /// </summary>
        // [Authorize(Roles = "Admin", AuthenticationSchemes = "Bearer")] // Temporarily disabled for debugging
        [HttpPost("pull")]
        public async Task<IActionResult> PullFromCloud()
        {
            try
            {
                var result = await _migrationService.PullFromSupabaseAsync();
                
                if (result.Success)
                {
                    return Ok(new { 
                        success = true,
                        message = "Pull from cloud completed successfully!",
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
                        message = "Pull failed",
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
                    message = "Pull failed", 
                    error = ex.Message, 
                    stackTrace = ex.StackTrace 
                });
            }
        }
    }
}
