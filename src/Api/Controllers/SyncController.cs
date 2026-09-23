#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Auth.Infrastructure.Sync.Push;
using Core.Exceptions;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Api.Controllers
{
    [Authorize(AuthenticationSchemes = "Bearer", Roles = "Admin")]
    [ApiController]
    [Route("api/[controller]")]
    public class SyncController : ControllerBase
    {
        private readonly ILocalOutboxPushService _pushService;
        private readonly ILocalDailyPullService _pullService;
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SyncController> _logger;
        private readonly ILocalScopeBaselineService _scopeBaselineService;

        public SyncController(
            ILocalOutboxPushService pushService,
            ILocalDailyPullService pullService,
            ISyncConnectionProvider syncConnectionProvider,
            IConfiguration configuration,
            ILogger<SyncController> logger,
            ILocalScopeBaselineService scopeBaselineService)
        {
            _pushService = pushService ?? throw new ArgumentNullException(nameof(pushService));
            _pullService = pullService ?? throw new ArgumentNullException(nameof(pullService));
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scopeBaselineService = scopeBaselineService ?? throw new ArgumentNullException(nameof(scopeBaselineService));
        }

        [HttpPost("push")]
        public async Task<IActionResult> PushOutbox(CancellationToken cancellationToken)
        {
            // 1. Feature gate check: Fail-Closed if Sync:PushEnabled != true
            var isPushEnabled = _configuration.GetValue<bool>("Sync:PushEnabled", false);
            if (!isPushEnabled)
            {
                _logger.LogWarning("POST /api/sync/push rejected: Sync:PushEnabled is false.");
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = "SYNC_PUSH_DISABLED",
                    message = "ميزة مزامنة الرفع معطلة حالياً (Sync:PushEnabled = false)."
                });
            }

            // 2. Runtime mode verification: Push is exclusively permitted in OfflineReadWritePilot
            if (!_syncConnectionProvider.IsLocalFirstEnabled || _syncConnectionProvider.IsReadOnlyMode)
            {
                _logger.LogWarning(
                    "POST /api/sync/push rejected: Not in OfflineReadWritePilot mode. (LocalFirst: {LocalFirst}, ReadOnly: {ReadOnly})",
                    _syncConnectionProvider.IsLocalFirstEnabled, _syncConnectionProvider.IsReadOnlyMode);

                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = _syncConnectionProvider.IsReadOnlyMode ? "READ_ONLY_MODE_BLOCKED" : "OFFLINE_WRITE_SCOPE_BLOCKED",
                    message = "العملية المطلوبة غير مصرح بها خارج وضع OfflineReadWritePilot."
                });
            }

            try
            {
                var result = await _pushService.PushPendingOutboxAsync(cancellationToken);
                return Ok(result);
            }
            catch (SyncPushAlreadyRunningException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncVersionConflictException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    expectedVersion = ex.ExpectedVersion,
                    currentServerVersion = ex.CurrentServerVersion,
                    message = ex.Message
                });
            }
            catch (SyncLeaseExpiredException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncOperationIdReuseException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPushDisabledException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPayloadValidationException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncMetadataMismatchException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncEntityAlreadyExistsException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncEntityNotFoundException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncLocalStateMissingException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncCorruptResponseJsonException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncDomainException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during push outbox execution.");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = "SYNC_INTERNAL_ERROR",
                    message = "An error occurred during push processing."
                });
            }
        }

        [HttpPost("pull")]
        public async Task<IActionResult> PullDaily(CancellationToken cancellationToken)
        {
            // 1. Feature gate check: Fail-Closed if Sync:PullEnabled != true
            var isPullEnabled = _configuration.GetValue<bool>("Sync:PullEnabled", false);
            if (!isPullEnabled)
            {
                _logger.LogWarning("POST /api/sync/pull rejected: Sync:PullEnabled is false.");
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = "SYNC_PULL_DISABLED",
                    message = "ميزة مزامنة السحب معطلة حالياً (Sync:PullEnabled = false)."
                });
            }

            // 2. Runtime mode verification: Pull allowed only if ReadOnlyMode == false
            if (_syncConnectionProvider.IsReadOnlyMode)
            {
                _logger.LogWarning("POST /api/sync/pull rejected: In ReadOnlyMode.");
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = "READ_ONLY_MODE_BLOCKED",
                    message = "العملية المطلوبة غير مصرح بها أثناء وضع ReadOnly."
                });
            }

            // 3. DatabaseId context verification
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId) || (databaseId != "2026" && databaseId != "2027"))
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = "INVALID_DATABASE_SELECTION",
                    message = $"Invalid canonical database ID '{databaseId}'. Expected '2026' or '2027'."
                });
            }

            try
            {
                var result = await _pullService.PullDailyChangesAsync(cancellationToken);
                return Ok(result);
            }
            catch (SyncPullAlreadyRunningException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullBlockedLocalChangesPendingException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullCheckpointAheadOfServerException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    localVersion = ex.LocalVersion,
                    serverVersion = ex.ServerVersion,
                    message = ex.Message
                });
            }
            catch (SyncPullLocalCheckpointChangedException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncLeaseExpiredException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullFeedGapException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullDuplicateVersionException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullTombstoneValidationException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullAuthoritativeRowMissingException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullTombstoneEntityStillActiveException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullBatchDatabaseMismatchException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullBatchMalformedException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullFeedMalformedException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullAuthoritativeStateMismatchException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullUnsupportedEntityTypeException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncPullUnsupportedOperationTypeException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncLocalStateMissingException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (SyncDomainException ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = ex.ErrorCode,
                    message = ex.Message
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during pull execution.");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    statusCode = StatusCodes.Status500InternalServerError,
                    code = "SYNC_INTERNAL_ERROR",
                    message = "An error occurred during pull processing."
                });
            }
        }

        [HttpGet("scopes")]
        public async Task<IActionResult> GetScopeReadiness(CancellationToken cancellationToken)
        {
            var databaseId = _syncConnectionProvider.GetSelectedDatabaseId();
            if (string.IsNullOrWhiteSpace(databaseId) || (databaseId != "2026" && databaseId != "2027"))
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = "INVALID_DATABASE_SELECTION",
                    message = $"Invalid canonical database ID '{databaseId}'. Expected '2026' or '2027'."
                });
            }

            var localConnStr = _syncConnectionProvider.GetLocalConnectionString(databaseId);
            await using var conn = new Microsoft.Data.SqlClient.SqlConnection(localConnStr);
            await conn.OpenAsync(cancellationToken);


            var dailyStatus = await _scopeBaselineService.GetScopeStatusAsync(conn, null, databaseId, "Daily", cancellationToken);
            var formsStatus = await _scopeBaselineService.GetScopeStatusAsync(conn, null, databaseId, "Forms", cancellationToken);

            return Ok(new
            {
                databaseId,
                scopes = new[]
                {
                    new { scope = "Daily", status = dailyStatus == Core.Interfaces.SyncScopeBaselineStatus.Baselined ? "BASELINED" : "NOT_BASELINED" },
                    new { scope = "Forms", status = formsStatus == Core.Interfaces.SyncScopeBaselineStatus.Baselined ? "BASELINED" : "NOT_BASELINED" }
                }
            });
        }
    }
}
