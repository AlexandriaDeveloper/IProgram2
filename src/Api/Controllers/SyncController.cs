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
        private readonly ISyncConnectionProvider _syncConnectionProvider;
        private readonly IConfiguration _configuration;
        private readonly ILogger<SyncController> _logger;

        public SyncController(
            ILocalOutboxPushService pushService,
            ISyncConnectionProvider syncConnectionProvider,
            IConfiguration configuration,
            ILogger<SyncController> logger)
        {
            _pushService = pushService ?? throw new ArgumentNullException(nameof(pushService));
            _syncConnectionProvider = syncConnectionProvider ?? throw new ArgumentNullException(nameof(syncConnectionProvider));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                    code = "SYNC_PUSH_ALREADY_RUNNING",
                    message = ex.Message
                });
            }
            catch (SyncVersionConflictException ex)
            {
                return StatusCode(StatusCodes.Status409Conflict, new
                {
                    statusCode = StatusCodes.Status409Conflict,
                    code = "SYNC_VERSION_CONFLICT",
                    expectedVersion = ex.ExpectedVersion,
                    currentServerVersion = ex.CurrentServerVersion,
                    message = ex.Message
                });
            }
            catch (SyncOperationIdReuseException ex)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = "SYNC_OPERATION_ID_REUSE",
                    message = ex.Message
                });
            }
            catch (SyncPushDisabledException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    code = "SYNC_PUSH_DISABLED",
                    message = ex.Message
                });
            }
            catch (Exception ex) when (ex is SyncEntityAlreadyExistsException or SyncEntityNotFoundException or SyncPayloadValidationException)
            {
                return StatusCode(StatusCodes.Status400BadRequest, new
                {
                    statusCode = StatusCodes.Status400BadRequest,
                    code = ex.GetType().Name,
                    message = ex.Message
                });
            }
        }
    }
}
