using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Auth.Api.Middleware
{
    public class ReadOnlyModeMiddleware
    {
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
            var isReadOnly = _configuration.GetValue<bool>("LocalFirst:ReadOnlyMode", false);
            if (!isReadOnly)
            {
                await _next(context);
                return;
            }

            var method = context.Request.Method;
            var path = context.Request.Path.Value?.TrimEnd('/') ?? "";

            // 1. Block known mutating GET routes (e.g. archiving/copying forms)
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
                    message = "النظام يعمل حالياً في وضع القراءة المحلية فقط. جميع عمليات الإضافة والتعديل والحذف والأرشفة معطلة.",
                    code = "READ_ONLY_MODE_BLOCKED",
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

            // 3. Exact allowlist for permitted POST operations (Login, Logout, and read-only Export)
            if (HttpMethods.IsPost(method))
            {
                if (string.Equals(path, "/api/account/login", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/account/logout", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(path, "/api/form/download-form", StringComparison.OrdinalIgnoreCase))
                {
                    await _next(context);
                    return;
                }
            }

            // All other mutating requests (POST, PUT, DELETE, PATCH) are rejected
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
        }
    }
}
