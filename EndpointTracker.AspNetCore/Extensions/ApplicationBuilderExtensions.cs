using EndpointTracker.AspNetCore.Middleware;
using EndpointTracker.AspNetCore.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointTracker.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring endpoint tracking middleware and routes.
/// </summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Adds endpoint tracking middleware to the application pipeline.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    /// <remarks>Must be called after UseRouting() and before UseEndpoints()</remarks>
    public static IApplicationBuilder UseEndpointTracker(this IApplicationBuilder app)
    {
        if (app == null)
            throw new ArgumentNullException(nameof(app));

        app.UseMiddleware<EndpointTrackerMiddleware>();

        return app;
    }
    /// <summary>
    /// Map the endpoint tracker metrics routes
    /// </summary>
    /// <param name="endpoints"></param>
    /// <param name="isAuthRequired"></param>
    /// <remarks> Don`t use it if you don`t want to have metrics endpoints exposed and want to use only the services</remarks>
    /// <exception cref="ArgumentNullException"></exception>
    public static IEndpointRouteBuilder MapEndpointTrackerMetrics(
    this IEndpointRouteBuilder endpoints,
    bool isAuthRequired = true)
    {
        if (endpoints == null)
            throw new ArgumentNullException(nameof(endpoints));

        // Create a route group for all metrics endpoints
        var group = endpoints.MapGroup("/metrics")
                             .WithTags("Metrics");

        // Apply authorization only if the flag is TRUE
        if (isAuthRequired)
            group.RequireAuthorization();

        // GET /metrics/endpoints
        group.MapGet("/endpoints", (IEndpointTrackerService tracker) =>
        {
            var metrics = tracker.GetMetrics();
            return Results.Ok(metrics);
        })
        .WithName("GetEndpointMetrics")
        .Produces<Models.EndpointMetricsResponse>(StatusCodes.Status200OK);

        // GET /metrics/unused
        group.MapGet("/unused", (IEndpointTrackerService tracker) =>
        {
            var unusedEndpoints = tracker.GetUnusedEndpoints();
            return Results.Ok(new
            {
                Count = unusedEndpoints.Count(),
                Endpoints = unusedEndpoints
            });
        })
        .WithName("GetUnusedEndpoints")
        .Produces(StatusCodes.Status200OK);

        return endpoints;
    }


    /// <summary>
    /// Registers all mapped endpoints with the tracker service.
    /// Should be called after all endpoints have been mapped.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseEndpointTrackerRegistration(this IApplicationBuilder app)
    {
        if (app == null)
            throw new ArgumentNullException(nameof(app));

        var trackerService = app.ApplicationServices.GetRequiredService<IEndpointTrackerService>();
        var endpointDataSources = app.ApplicationServices.GetServices<EndpointDataSource>();

        foreach (var dataSource in endpointDataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                var routeEndpoint = endpoint as RouteEndpoint;
                var routePattern = routeEndpoint?.RoutePattern?.RawText ?? endpoint.DisplayName ?? "Unknown";
                var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                var httpMethod = httpMethods != null ? string.Join(", ", httpMethods) : "ANY";
                var displayName = endpoint.DisplayName;

                // Register each HTTP method separately for clarity
                if (httpMethods != null && httpMethods.Any())
                {
                    foreach (var method in httpMethods)
                    {
                        var pattern = $"{method} {routePattern}";
                        trackerService.RegisterEndpoint(pattern, displayName, method);
                    }
                }
                else
                {
                    var pattern = $"ANY {routePattern}";
                    trackerService.RegisterEndpoint(pattern, displayName, httpMethod);
                }
            }
        }

        return app;
    }
}
