using EndpointTracker.AspNetCore.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointTracker.AspNetCore.Internal;

/// <summary>
/// Hosted service that registers all mapped endpoints with the tracker after application startup.
/// </summary>
internal class EndpointRegistrationHostedService : IHostedService
{
    private readonly IEndpointTrackerService _trackerService;
    private readonly EndpointDataSource _endpointDataSource;
    private readonly ILogger<EndpointRegistrationHostedService> _logger;

    public EndpointRegistrationHostedService(
        IEndpointTrackerService trackerService,
        EndpointDataSource endpointDataSource,
        ILogger<EndpointRegistrationHostedService> logger)
    {
        _trackerService = trackerService;
        _endpointDataSource = endpointDataSource;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Register a callback for when endpoints change
        RegisterEndpoints();

        // Also listen for changes (in case endpoints are added dynamically)
        _endpointDataSource.GetChangeToken().RegisterChangeCallback(_ => RegisterEndpoints(), null);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void RegisterEndpoints()
    {
        var registeredCount = 0;

        foreach (var endpoint in _endpointDataSource.Endpoints)
        {
            var routeEndpoint = endpoint as RouteEndpoint;
            var routePattern = routeEndpoint?.RoutePattern?.RawText ?? endpoint.DisplayName ?? "Unknown";
            var httpMethods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            var displayName = endpoint.DisplayName;

            // Register each HTTP method separately for clarity
            if (httpMethods != null && httpMethods.Any())
            {
                foreach (var method in httpMethods)
                {
                    var pattern = $"{method} {routePattern}";
                    _trackerService.RegisterEndpoint(pattern, displayName, method);
                    registeredCount++;
                }
            }
            else
            {
                var pattern = $"ANY {routePattern}";
                _trackerService.RegisterEndpoint(pattern, displayName, "ANY");
                registeredCount++;
            }
        }

        _logger.LogInformation("EndpointTracker registered {Count} endpoints", registeredCount);
    }
}
