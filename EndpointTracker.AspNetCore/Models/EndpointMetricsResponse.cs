namespace EndpointTracker.AspNetCore.Models;

/// <summary>
/// Response model for endpoint metrics.
/// </summary>
public class EndpointMetricsResponse
{
    /// <summary>
    /// Gets or sets the total number of registered endpoints.
    /// </summary>
    public int TotalEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the number of endpoints that have been accessed at least once.
    /// </summary>
    public int UsedEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the number of endpoints that have never been accessed.
    /// </summary>
    public int UnusedEndpoints { get; set; }

    /// <summary>
    /// Gets or sets the total number of requests tracked.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Gets or sets the collection of endpoint usage information.
    /// </summary>
    public IEnumerable<EndpointUsageInfo> Endpoints { get; set; } = Array.Empty<EndpointUsageInfo>();
}
