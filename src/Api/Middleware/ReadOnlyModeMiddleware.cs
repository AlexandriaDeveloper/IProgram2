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

            // Safe read-only HTTP methods are always permitted
            if (HttpMethods.IsGet(method) ||
                HttpMethods.IsHead(method) ||
                HttpMethods.IsOptions(method))
            {
                await _next(context);
                return;
            }

            // Explicit allowlist for authentication flows against local Identity
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api/account/login", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWithSegments("/api/account/logout", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
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
