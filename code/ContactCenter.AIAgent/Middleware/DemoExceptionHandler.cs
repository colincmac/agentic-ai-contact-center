using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace ContactCenter.AIAgent.Middleware;

public sealed class DemoExceptionHandler(ILogger<DemoExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            JsonException or ArgumentException or BadHttpRequestException => (400, "Invalid request."),
            UnauthorizedAccessException => (403, "Operation not permitted."),
            KeyNotFoundException => (404, "Resource not found."),
            Agents.AI.ContactCenter.Exceptions.CapacityExhaustedException => (503, "Demo call capacity is exhausted."),
            TimeoutException => (503, "The operation timed out."),
            Azure.RequestFailedException => (502, "An upstream communication operation failed."),
            InvalidOperationException => (409, "The operation conflicts with current call state."),
            _ => (500, "An unexpected error occurred."),
        };
        logger.LogWarning("Demo request failed: {ExceptionType}, status={Status}.", exception.GetType().Name, status);
        if (http.Response.HasStarted) { return false; }
        await TypedResults.Problem(statusCode: status, title: title).ExecuteAsync(http).ConfigureAwait(false);
        return true;
    }
}
