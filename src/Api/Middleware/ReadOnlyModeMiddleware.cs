#nullable enable
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Auth.Api.Middleware
{
    public class ReadOnlyModeMiddleware
    {
        private static readonly Regex CloseDailyRegex = new Regex(@"^/api/daily/closedaily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UncloseDailyRegex = new Regex(@"^/api/daily/unclosedaily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DeleteDailyRegex = new Regex(@"^/api/daily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SoftDeleteDailyRegex = new Regex(@"^/api/daily/softdelete/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly RequestDelegate _next;
        private readonly IConfiguration _configuration;
        private readonly ILogger<ReadOnlyModeMiddleware> _logger;

        public ReadOnlyModeMiddleware(
            RequestDelegate next,
            IConfiguration configuration,
            ILogger<ReadOnlyModeMiddleware> logger)
        {
            _next = next;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var isLocalFirst = _configuration.GetValue<bool>("LocalFirst:Enabled", false);
            var isReadOnly = _configuration.GetValue<bool>("LocalFirst:ReadOnlyMode", false);

            // Online mode: unhindered
            if (!isLocalFirst && !isReadOnly)
            {
                await _next(context);
                return;
            }

            var method = context.Request.Method;
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

            // 1. Block known mutating GET routes (e.g. archiving/copying forms) in all local modes
            if (path.StartsWith("/api/form/copyformtoarchive", StringComparison.OrdinalIgnoreCase))
            {
                var blockedTraceId = Activity.Current?.Id ?? context.TraceIdentifier;
                _logger.LogWarning(
                    "Mutating GET request blocked by ReadOnlyModeMiddleware: {Method} {Path} (TraceId: {TraceId})",
                    method, path, blockedTraceId);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";

                var blockedPayload = new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    message = "العملية المطلوبة غير مسموحة في الوضع المحلي. جميع عمليات الأرشفة معطلة.",
                    code = isReadOnly ? "READ_ONLY_MODE_BLOCKED" : "OFFLINE_WRITE_SCOPE_BLOCKED",
                    traceId = blockedTraceId
                };

                await context.Response.WriteAsJsonAsync(blockedPayload);
                return;
            }

            // 2. Safe read-only HTTP methods are permitted
            if (HttpMethods.IsGet(method) ||
                HttpMethods.IsHead(method) ||
                HttpMethods.IsOptions(method))
            {
                await _next(context);
                return;
            }

            // 3. Exact allowlist for permitted non-mutating POST operations (Login, Logout, and read-only Export)
            if (HttpMethods.IsPost(method))
            {
                if (string.Equals(path, "/api/account/login", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/account/logout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/form/download-form", StringComparison.OrdinalIgnoreCase) ||
                    (_configuration.GetValue<bool>("E2E:DiagnosticsEnabled", false) && string.Equals(path, "/api/diagnostics/connection-audit/clear", StringComparison.OrdinalIgnoreCase)))
                {
                    await _next(context);
                    return;
                }
            }

            // 4. OfflineReadOnly mode: all other mutating requests are blocked fail-closed
            if (isReadOnly)
            {
                var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
                _logger.LogWarning(
                    "Write request blocked by ReadOnlyModeMiddleware: {Method} {Path} (TraceId: {TraceId})",
                    method, path, traceId);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";

                var responsePayload = new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    message = "النظام يعمل حالياً في وضع القراءة المحلية فقط. جميع عمليات الإضافة والتعديل والحذف والمراجعة والإغلاق وإدارة المستخدمين والمرفقات معطلة.",
                    code = "READ_ONLY_MODE_BLOCKED",
                    traceId
                };

                await context.Response.WriteAsJsonAsync(responsePayload);
                return;
            }

            // 5. OfflineReadWritePilot mode (isLocalFirst == true && isReadOnly == false):
            // Strictly check allowlist for permitted Daily pilot operations
            if (IsPermittedOfflineWritePilotRoute(method, path, _configuration))
            {
                await _next(context);
                return;
            }

            // All non-pilot mutating requests in OfflineReadWritePilot are rejected fail-closed
            var blockedScopeTraceId = Activity.Current?.Id ?? context.TraceIdentifier;
            _logger.LogWarning(
                "Write request blocked by OfflineWritePilot scope guard: {Method} {Path} (TraceId: {TraceId})",
                method, path, blockedScopeTraceId);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";

            var scopeResponsePayload = new
            {
                statusCode = StatusCodes.Status403Forbidden,
                message = "العملية المطلوبة غير مسموحة في وضع Offline Read-Write Pilot. العمليات المصرح بها محصورة في اليوميات (Daily) فقط.",
                code = "OFFLINE_WRITE_SCOPE_BLOCKED",
                traceId = blockedScopeTraceId
            };

            await context.Response.WriteAsJsonAsync(scopeResponsePayload);
        }

        public static bool IsPermittedOfflineWritePilotRoute(string method, string path, IConfiguration? configuration = null)
        {
            if (HttpMethods.IsPost(method))
            {
                if (string.Equals(path, "/api/daily", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(path, "/api/sync/push", StringComparison.OrdinalIgnoreCase) &&
                    configuration?.GetValue<bool>("Sync:PushEnabled", false) == true)
                {
                    return true;
                }

                return false;
            }

            if (HttpMethods.IsPut(method))
            {
                if (string.Equals(path, "/api/daily", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (CloseDailyRegex.IsMatch(path) || UncloseDailyRegex.IsMatch(path))
                {
                    return true;
                }

                return false;
            }

            if (HttpMethods.IsDelete(method))
            {
                if (DeleteDailyRegex.IsMatch(path) || SoftDeleteDailyRegex.IsMatch(path))
                {
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
