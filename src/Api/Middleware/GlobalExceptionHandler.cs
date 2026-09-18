#nullable enable
using System;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auth.Api.Middleware
{
    public class GlobalExceptionHandler : IExceptionHandler
    {
        private readonly ILogger<GlobalExceptionHandler> _logger;

        public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
        {
            _logger = logger;
        }

        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
            var requestPath = httpContext.Request.Path.Value ?? string.Empty;

            // Resolve database identifier for structured diagnostic logging WITHOUT exposing connection strings
            string databaseId = "unknown";
            try
            {
                var dbProvider = httpContext.RequestServices?.GetService<IDbConnectionProvider>();
                if (dbProvider != null)
                {
                    var resolvedId = dbProvider.GetSelectedDatabaseId();
                    if (!string.IsNullOrWhiteSpace(resolvedId))
                    {
                        databaseId = resolvedId;
                    }
                }
            }
            catch
            {
                // Fallback silently if service resolution fails during error handling
            }

            if (exception is Core.Exceptions.InvalidDatabaseSelectionException)
            {
                int badRequestCode = StatusCodes.Status400BadRequest;
                string badRequestMessage = "قاعدة البيانات المحددة غير صالحة.";

                _logger.LogWarning(
                    "Invalid database selection at {RequestPath} with TraceId {TraceId}. StatusCode: {StatusCode}, ExceptionType: {ExceptionType}",
                    requestPath, traceId, badRequestCode, exception.GetType().Name);

                httpContext.Response.StatusCode = badRequestCode;
                httpContext.Response.ContentType = "application/json; charset=utf-8";

                var badRequestPayload = new ErrorResponseDto
                {
                    StatusCode = badRequestCode,
                    Message = badRequestMessage,
                    TraceId = traceId
                };

                await httpContext.Response.WriteAsJsonAsync(badRequestPayload, cancellationToken);
                return true;
            }

            bool isTransientDbFailure = IsTransientDatabaseFailure(exception);
            int statusCode = isTransientDbFailure
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status500InternalServerError;

            string userMessage = isTransientDbFailure
                ? "الخدمة غير متوفرة مؤقتًا بسبب انقطاع الاتصال بقاعدة البيانات. يرجى المحاولة بعد قليل."
                : "حدث خطأ غير متوقع أثناء تنفيذ الطلب.";

            // Structured logging: Log databaseId, requestPath, traceId, exception types and HResult.
            // DO NOT pass the raw Exception object to logger to prevent leaking connection strings, secrets, or stack traces.
            // DO NOT log exception.Message, exception.ToString(), or raw InnerException messages.
            string innerExceptionType = exception.InnerException?.GetType().Name ?? "None";

            if (isTransientDbFailure || exception is DbUpdateException || exception is SqlException)
            {
                _logger.LogError(
                    "Database operation failed for DatabaseId {DatabaseId} at {RequestPath} with TraceId {TraceId}. Transient: {IsTransient}, StatusCode: {StatusCode}, ExceptionType: {ExceptionType}, InnerType: {InnerType}, HResult: {HResult}",
                    databaseId, requestPath, traceId, isTransientDbFailure, statusCode, exception.GetType().Name, innerExceptionType, exception.HResult);
            }
            else
            {
                _logger.LogError(
                    "Unhandled exception occurred at {RequestPath} with TraceId {TraceId} for DatabaseId {DatabaseId}. StatusCode: {StatusCode}, ExceptionType: {ExceptionType}, InnerType: {InnerType}, HResult: {HResult}",
                    requestPath, traceId, databaseId, statusCode, exception.GetType().Name, innerExceptionType, exception.HResult);
            }

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = "application/json; charset=utf-8";

            var responsePayload = new ErrorResponseDto
            {
                StatusCode = statusCode,
                Message = userMessage,
                TraceId = traceId
            };

            await httpContext.Response.WriteAsJsonAsync(responsePayload, cancellationToken);
            return true;
        }

        public static bool IsTransientDatabaseFailure(Exception? ex)
        {
            var current = ex;
            while (current != null)
            {
                if (current is SqlException sqlEx)
                {
                    if (sqlEx.IsTransient)
                        return true;

                    if (IsTransientSqlErrorCode(sqlEx.Number))
                        return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        public static bool IsTransientSqlErrorCode(int errorCode)
        {
            // Transient SQL Server & Azure SQL error numbers
            switch (errorCode)
            {
                case -2:    // Execution Timeout Expired
                case 64:    // An error occurred while establishing a connection
                case 233:   // Connection initialization error / broken pipe
                case 1205:  // Transaction deadlock victim
                case 10053: // Transport-level connection aborted
                case 10054: // Connection reset by peer
                case 10060: // Network connection failed / host unreachable
                case 10928: // Resource limit reached in Azure SQL
                case 10929: // Resource governor queued request in Azure SQL
                case 40197: // Azure SQL transient error processing request
                case 40501: // Azure SQL service busy
                case 40613: // Azure SQL database unavailable
                case 49918: // Cannot process request, not enough resources
                case 49919: // Cannot process request, service busy
                case 49920: // Cannot process request, service busy
                    return true;
                default:
                    return false;
            }
        }
    }

    public class ErrorResponseDto
    {
        [JsonPropertyName("statusCode")]
        public int StatusCode { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("traceId")]
        public string TraceId { get; set; } = string.Empty;
    }
}
