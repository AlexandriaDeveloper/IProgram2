#nullable enable
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.Api.Middleware
{
    public class ReadOnlyModeMiddleware
    {
        private static readonly Regex CloseDailyRegex = new Regex(@"^/api/daily/closedaily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UncloseDailyRegex = new Regex(@"^/api/daily/unclosedaily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DeleteDailyRegex = new Regex(@"^/api/daily/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SoftDeleteDailyRegex = new Regex(@"^/api/daily/softdelete/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Form Regexes
        private static readonly Regex FormIdRegex = new Regex(@"^/api/form/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HideFormRegex = new Regex(@"^/api/form/hide-form/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex RestoreFormRegex = new Regex(@"^/api/form/restore-form/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex UpdateDescriptionRegex = new Regex(@"^/api/form/updatedescription/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SoftDeleteFormRegex = new Regex(@"^/api/form/softdelete/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CopyFormToArchiveRegex = new Regex(@"^/api/form/copyformtoarchive/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // FormDetails Regexes
        private static readonly Regex ReOrderRowsRegex = new Regex(@"^/api/formdetails/reorderrows/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkAsReviewedRegex = new Regex(@"^/api/formdetails/markasreviewed/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkAsSummaryReviewedRegex = new Regex(@"^/api/formdetails/markassummaryreviewed/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex DeleteFormDetailsRegex = new Regex(@"^/api/formdetails/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // FormArchived Regexes
        private static readonly Regex DeleteFormArchivedRegex = new Regex(@"^/api/formarchived/\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
            var isLocalOnlyProduction = _configuration.GetValue<bool>("LocalFirst:LocalOnlyProduction", false) ||
                                       string.Equals(_configuration.GetValue<string>("LocalFirst:Mode"), "LocalOnlyProduction", StringComparison.OrdinalIgnoreCase);

            var method = context.Request.Method;
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

            // In LocalOnlyProduction mode: all normal business operations run unhindered;
            // only sync trigger operations (Pull / Push / CheckOnline) are blocked fail-closed.
            if (isLocalOnlyProduction)
            {
                if (path.StartsWith("/api/sync/pull", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/sync/push", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/sync/check-online", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/sync/status/check-online", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/api/migration", StringComparison.OrdinalIgnoreCase))
                {
                    var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
                    _logger.LogWarning(
                        "Sync/migration operation blocked in LocalOnlyProduction mode: {Method} {Path} (TraceId: {TraceId})",
                        method, path, traceId);

                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/json; charset=utf-8";

                    var responsePayload = new
                    {
                        statusCode = StatusCodes.Status403Forbidden,
                        message = "عمليات المزامنة مع السحابة معطلة تماماً في وضع التشغيل المحلي الكامل (Local-Only Production). قاعدة البيانات المحلية هي مصدر الحقيقة الوحيد.",
                        code = "SYNC_DISABLED_IN_LOCAL_ONLY_PRODUCTION",
                        traceId
                    };

                    await context.Response.WriteAsJsonAsync(responsePayload);
                    return;
                }

                await _next(context);
                return;
            }

            // Online mode: unhindered
            if (!isLocalFirst && !isReadOnly)
            {
                await _next(context);
                return;
            }

            // 1. Block mutating GET routes (e.g. archiving/copying forms) in OfflineReadOnly mode
            if (isReadOnly && path.StartsWith("/api/form/copyformtoarchive", StringComparison.OrdinalIgnoreCase))
            {
                var blockedTraceId = Activity.Current?.Id ?? context.TraceIdentifier;
                _logger.LogWarning(
                    "Mutating GET request blocked by ReadOnlyModeMiddleware in ReadOnly mode: {Method} {Path} (TraceId: {TraceId})",
                    method, path, blockedTraceId);

                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";

                var blockedPayload = new
                {
                    statusCode = StatusCodes.Status403Forbidden,
                    message = "العملية المطلوبة غير مسموحة في وضع القراءة المحلية فقط. جميع عمليات الأرشفة معطلة.",
                    code = "READ_ONLY_MODE_BLOCKED",
                    traceId = blockedTraceId
                };

                await context.Response.WriteAsJsonAsync(blockedPayload);
                return;
            }

            // 2. Safe read-only HTTP methods are permitted (excluding mutating GET routes like CopyFormToArchive)
            if ((HttpMethods.IsGet(method) && !path.StartsWith("/api/form/copyformtoarchive", StringComparison.OrdinalIgnoreCase)) ||
                HttpMethods.IsHead(method) ||
                HttpMethods.IsOptions(method))
            {
                await _next(context);
                return;
            }

            // 3. Exact allowlist for permitted non-mutating POST operations (Login, Logout, read-only Export, and read-only Sync Status Check)
            if (HttpMethods.IsPost(method))
            {
                if (string.Equals(path, "/api/account/login", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/account/logout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/form/download-form", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/sync/status/check-online", StringComparison.OrdinalIgnoreCase) ||
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
            // Strictly check allowlist for permitted Daily and Forms pilot operations
            if (IsPermittedOfflineWritePilotRoute(method, path, _configuration))
            {
                // If the route belongs to Forms scope, enforce Forms scope baseline readiness (fail-closed)
                if (IsFormsScopeRoute(method, path))
                {
                    var baselineService = context.RequestServices.GetService<Core.Interfaces.ILocalScopeBaselineService>();
                    var syncConnectionProvider = context.RequestServices.GetService<Core.Interfaces.ISyncConnectionProvider>();
                    if (baselineService == null || syncConnectionProvider == null)
                    {
                        var blockedDepTraceId = Activity.Current?.Id ?? context.TraceIdentifier;
                        _logger.LogError(
                            "Forms write request blocked: scope baseline security dependencies unavailable (TraceId: {TraceId})",
                            blockedDepTraceId);

                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        context.Response.ContentType = "application/json; charset=utf-8";

                        var unavailablePayload = new
                        {
                            statusCode = StatusCodes.Status503ServiceUnavailable,
                            message = "خدمة التحقق من تأصيل النطاقات غير متوفرة (SCOPE_BASELINE_SERVICE_UNAVAILABLE). العمليات المحلية على النماذج معطلة احترازياً.",
                            code = "SCOPE_BASELINE_SERVICE_UNAVAILABLE",
                            traceId = blockedDepTraceId
                        };

                        await context.Response.WriteAsJsonAsync(unavailablePayload);
                        return;
                    }

                    var databaseId = syncConnectionProvider.GetSelectedDatabaseId();
                    var localConnStr = syncConnectionProvider.GetLocalConnectionString(databaseId);
                    await using var baselineConn = new Microsoft.Data.SqlClient.SqlConnection(localConnStr);
                    await baselineConn.OpenAsync(context.RequestAborted);

                    var baselineStatus = await baselineService.GetScopeStatusAsync(
                        baselineConn, null, databaseId, "Forms", context.RequestAborted);

                    if (baselineStatus != Core.Interfaces.SyncScopeBaselineStatus.Baselined)
                    {
                        var blockedBaselineTraceId = Activity.Current?.Id ?? context.TraceIdentifier;
                        _logger.LogWarning(
                            "Forms write request blocked by ScopeBaseline guard: {Method} {Path} (DatabaseId: {DatabaseId}, Status: {Status}, TraceId: {TraceId})",
                            method, path, databaseId, baselineStatus, blockedBaselineTraceId);

                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        context.Response.ContentType = "application/json; charset=utf-8";

                        var notBaselinedPayload = new
                        {
                            statusCode = StatusCodes.Status403Forbidden,
                            message = "نطاق النماذج (Forms) غير مؤصل محلياً حتى الآن (NOT_BASELINED). العمليات المحلية على النماذج معطلة لحين إتمام التأصيل المعتمد.",
                            code = "FORMS_SCOPE_NOT_BASELINED",
                            traceId = blockedBaselineTraceId
                        };

                        await context.Response.WriteAsJsonAsync(notBaselinedPayload);
                        return;
                    }
                }

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
                message = "العملية المطلوبة غير مسموحة في وضع Offline Read-Write Pilot. العمليات المصرح بها محصورة في اليوميات (Daily) والاستمارات (Forms) المصرح بها فقط.",
                code = "OFFLINE_WRITE_SCOPE_BLOCKED",
                traceId = blockedScopeTraceId
            };

            await context.Response.WriteAsJsonAsync(scopeResponsePayload);
        }

        public static bool IsFormsScopeRoute(string method, string path)
        {
            if (HttpMethods.IsPost(method))
            {
                return string.Equals(path, "/api/form", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/form/upload-excel-form", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/form/upload-json-form", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/formdetails/AddEmployeeToFormDetails", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/formarchived/deleteMultiForms", StringComparison.OrdinalIgnoreCase);
            }

            if (HttpMethods.IsPut(method))
            {
                return string.Equals(path, "/api/form/MoveFormDailyArchives", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/formdetails/EditEmployeeToFormDetails", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(path, "/api/formarchived/MoveFormArchiveToDaily", StringComparison.OrdinalIgnoreCase) ||
                       FormIdRegex.IsMatch(path) ||
                       HideFormRegex.IsMatch(path) ||
                       RestoreFormRegex.IsMatch(path) ||
                       UpdateDescriptionRegex.IsMatch(path) ||
                       ReOrderRowsRegex.IsMatch(path) ||
                       MarkAsReviewedRegex.IsMatch(path) ||
                       MarkAsSummaryReviewedRegex.IsMatch(path);
            }

            if (HttpMethods.IsDelete(method))
            {
                return SoftDeleteFormRegex.IsMatch(path) ||
                       FormIdRegex.IsMatch(path) ||
                       DeleteFormDetailsRegex.IsMatch(path) ||
                       DeleteFormArchivedRegex.IsMatch(path);
            }

            if (HttpMethods.IsGet(method))
            {
                return CopyFormToArchiveRegex.IsMatch(path);
            }

            return false;
        }

        public static bool IsPermittedOfflineWritePilotRoute(string method, string path, IConfiguration? configuration = null)
        {
            if (IsFormsScopeRoute(method, path))
            {
                return true;
            }

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

                if (string.Equals(path, "/api/sync/pull", StringComparison.OrdinalIgnoreCase) &&
                    configuration?.GetValue<bool>("Sync:PullEnabled", false) == true)
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
