using StackExchange.Redis;

namespace EndpointTracker.AspNetCore.Options;

/// <summary>
/// Configuration options for EndpointTracker service.
/// </summary>
public class EndpointTrackerOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether to use Redis for storage.
    /// Default is false (uses in-memory storage).
    /// </summary>
    public bool UseRedis { get; set; } = false;

    /// <summary>
    /// Gets or sets the Redis connection configuration.
    /// Only required if UseRedis is true.
    /// </summary>
    public IConnectionMultiplexer? RedisConnection { get; set; }

    /// <summary>
    /// Gets or sets the Redis database number to use.
    /// Default is 0.
    /// </summary>
    public int RedisDatabase { get; set; } = 0;

    /// <summary>
    /// Gets or sets the flush interval in milliseconds for the Redis buffer.
    /// Default is 1000ms.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Gets or sets the key prefix for Redis keys.
    /// Default is "endpoint-tracker:".
    /// </summary>
    public string RedisKeyPrefix { get; set; } = "endpoint-tracker:";
}
