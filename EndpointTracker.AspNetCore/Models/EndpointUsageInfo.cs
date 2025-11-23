namespace EndpointTracker.AspNetCore.Models;

/// <summary>
/// Represents usage statistics for a single endpoint.
/// </summary>
public class EndpointUsageInfo
{
    /// <summary>
    /// Gets or sets the endpoint route pattern (e.g., "GET /api/users/{id}").
    /// </summary>
    public string EndpointPattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name of the endpoint.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the HTTP method(s) for this endpoint.
    /// </summary>
    public string? HttpMethod { get; set; }

    /// <summary>
    /// Gets or sets the number of times this endpoint has been accessed.
    /// </summary>
    public int HitCount { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last access to this endpoint.
    /// </summary>
    public DateTime? LastAccessedUtc { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when this endpoint was first registered.
    /// </summary>
    public DateTime RegisteredUtc { get; set; }
}
