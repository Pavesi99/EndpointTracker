using EndpointTracker.AspNetCore.Services;
using Microsoft.AspNetCore.Http;

namespace EndpointTracker.AspNetCore.Middleware;

/// <summary>
/// Middleware that intercepts HTTP requests and tracks endpoint usage.
/// </summary>
public class EndpointTrackerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IEndpointTrackerService _trackerService;

    /// <summary>
    /// Initializes a new instance of the <see cref="EndpointTrackerMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="trackerService">The endpoint tracker service.</param>
    public EndpointTrackerMiddleware(RequestDelegate next, IEndpointTrackerService trackerService)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _trackerService = trackerService ?? throw new ArgumentNullException(nameof(trackerService));
    }

    /// <summary>
    /// Invokes the middleware to track endpoint usage.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        // Continue processing the request
        await _next(context);

        // After the endpoint is matched, track it
        var endpoint = context.GetEndpoint();
        if (endpoint != null)
        {
            var routePattern = GetEndpointPattern(context, endpoint);
            if (!string.IsNullOrWhiteSpace(routePattern))
            {
                _trackerService.RecordHit(routePattern);
            }
        }
    }

    /// <summary>
    /// Extracts the endpoint pattern from the matched endpoint.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <param name="endpoint">The matched endpoint.</param>
    /// <returns>A string representing the endpoint pattern.</returns>
    private static string GetEndpointPattern(HttpContext context, Endpoint endpoint)
    {
        var routeEndpoint = endpoint as Microsoft.AspNetCore.Routing.RouteEndpoint;
        var httpMethod = context.Request.Method;

        if (routeEndpoint?.RoutePattern?.RawText != null)
        {
            return $"{httpMethod} {routeEndpoint.RoutePattern.RawText}";
        }

        // Fallback to path for non-route endpoints
        var displayName = endpoint.DisplayName;
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return $"{httpMethod} {displayName}";
        }

        return $"{httpMethod} {context.Request.Path}";
    }
}
