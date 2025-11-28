using System.Collections.Concurrent;
using EndpointTracker.AspNetCore.Models;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Thread-safe service for tracking endpoints usage across the application.
/// </summary>
public class EndpointTrackerService : IEndpointTrackerService
{
    private readonly ConcurrentDictionary<string, EndpointUsageInfo> _endpointUsage = new();
    private long _totalRequests;

    /// <summary>
    /// Registers an endpoint for tracking.
    /// </summary>
    /// <param name="endpointPattern">The endpoint route pattern.</param>
    /// <param name="displayName">The display name of the endpoint.</param>
    /// <param name="httpMethod">The HTTP method(s) for this endpoint.</param>
    public void RegisterEndpoint(string endpointPattern, string? displayName, string? httpMethod)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        _endpointUsage.TryAdd(endpointPattern, new EndpointUsageInfo
        {
            EndpointPattern = endpointPattern,
            DisplayName = displayName,
            HttpMethod = httpMethod,
            HitCount = 0,
            LastAccessedUtc = null,
            RegisteredUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Records a hit to an endpoint in a thread-safe manner.
    /// </summary>
    /// <param name="endpointPattern">The endpoint route pattern.</param>
    public void RecordHit(string endpointPattern)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        Interlocked.Increment(ref _totalRequests);

        _endpointUsage.AddOrUpdate(
            endpointPattern,
            // Add new endpoint if not registered (fallback)
            key => new EndpointUsageInfo
            {
                EndpointPattern = key,
                HitCount = 1,
                LastAccessedUtc = DateTime.UtcNow,
                RegisteredUtc = DateTime.UtcNow
            },
            // Update existing endpoint
            (key, existing) =>
            {
                existing.HitCount++;
                existing.LastAccessedUtc = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Gets all endpoint usage statistics.
    /// </summary>
    /// <returns>A collection of endpoint usage information ordered by hit count descending.</returns>
    public IEnumerable<EndpointUsageInfo> GetAllEndpointUsage()
    {
        return _endpointUsage.Values
            .OrderByDescending(e => e.HitCount)
            .ThenBy(e => e.EndpointPattern)
            .ToList();
    }

    /// <summary>
    /// Gets endpoints that have never been accessed.
    /// </summary>
    /// <returns>A collection of unused endpoints information.</returns>
    public IEnumerable<EndpointUsageInfo> GetUnusedEndpoints()
    {
        return _endpointUsage.Values
            .Where(e => e.HitCount == 0)
            .OrderBy(e => e.EndpointPattern)
            .ToList();
    }

    /// <summary>
    /// Gets comprehensive metrics about endpoints usage.
    /// </summary>
    /// <returns>Endpoints metrics response.</returns>
    public EndpointMetricsResponse GetMetrics()
    {
        var allEndpoints = GetAllEndpointUsage().ToList();
        var usedCount = allEndpoints.Count(e => e.HitCount > 0);

        return new EndpointMetricsResponse
        {
            TotalEndpoints = allEndpoints.Count,
            UsedEndpoints = usedCount,
            UnusedEndpoints = allEndpoints.Count - usedCount,
            TotalRequests = Interlocked.Read(ref _totalRequests),
            Endpoints = allEndpoints
        };
    }

    /// <summary>
    /// Resets all tracking data. Use with caution in production.
    /// </summary>
    public void Reset()
    {
        _endpointUsage.Clear();
        Interlocked.Exchange(ref _totalRequests, 0);
    }
}
