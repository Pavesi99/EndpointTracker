using EndpointTracker.AspNetCore.Models;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Service interface for tracking endpoint usage across the application.
/// </summary>
public interface IEndpointTrackerService
{
    /// <summary>
    /// Registers an endpoint for tracking.
    /// </summary>
    /// <param name="endpointPattern">The endpoint route pattern.</param>
    /// <param name="displayName">The display name of the endpoint.</param>
    /// <param name="httpMethod">The HTTP method(s) for this endpoint.</param>
    void RegisterEndpoint(string endpointPattern, string? displayName, string? httpMethod);

    /// <summary>
    /// Records a hit to an endpoint.
    /// </summary>
    /// <param name="endpointPattern">The endpoint route pattern.</param>
    void RecordHit(string endpointPattern);

    /// <summary>
    /// Gets all endpoint usage statistics.
    /// </summary>
    /// <returns>A collection of endpoint usage information.</returns>
    IEnumerable<EndpointUsageInfo> GetAllEndpointUsage();

    /// <summary>
    /// Gets endpoints that have never been accessed.
    /// </summary>
    /// <returns>A collection of unused endpoint information.</returns>
    IEnumerable<EndpointUsageInfo> GetUnusedEndpoints();

    /// <summary>
    /// Gets comprehensive metrics about endpoint usage.
    /// </summary>
    /// <returns>Endpoint metrics response.</returns>
    EndpointMetricsResponse GetMetrics();

    /// <summary>
    /// Resets all tracking data.
    /// </summary>
    void Reset();
}
