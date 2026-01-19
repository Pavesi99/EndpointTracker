using System.Collections.Concurrent;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Redis-backed service for tracking endpoint usage with in-memory buffer and periodic flushing.
/// </summary>
public class RedisEndpointTrackerService : IEndpointTrackerService
{
    private readonly IConnectionMultiplexer _redisConnection;
    private readonly IDatabase _redisDb;
    private readonly string _keyPrefix;
    private readonly ILogger<RedisEndpointTrackerService> _logger;
    
    // In-memory buffer for hit counts before flushing to Redis
    private readonly ConcurrentDictionary<string, long> _hitBuffer = new();
    
    // In-memory cache of endpoint metadata
    private readonly ConcurrentDictionary<string, EndpointUsageInfo> _endpointMetadata = new();
    
    // Track total requests in memory (since Redis doesn't easily provide this)
    private long _totalRequests;
    
    private const string EndpointHashKey = "endpoints:metadata";
    private const string HitCountKeyFormat = "hits:{0}";
    private const string LastAccessedKeyFormat = "last-accessed:{0}";

    /// <summary>
    /// Exposes current UTC time for extensibility/testing.
    /// </summary>
    protected virtual DateTime UtcNow => DateTime.UtcNow;

    public RedisEndpointTrackerService(
        IConnectionMultiplexer redisConnection,
        EndpointTrackerOptions options,
        ILogger<RedisEndpointTrackerService> logger)
    {
        _redisConnection = redisConnection ?? throw new ArgumentNullException(nameof(redisConnection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyPrefix = (options?.RedisKeyPrefix ?? "endpoint-tracker:").TrimEnd(':') + ":";
        
        _redisDb = _redisConnection.GetDatabase(options?.RedisDatabase ?? 0);
        
        // Load existing endpoint metadata from Redis on startup
        LoadEndpointMetadata();
    }

    /// <summary>
    /// Registers an endpoint for tracking.
    /// </summary>
    public void RegisterEndpoint(string endpointPattern, string? displayName, string? httpMethod)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        var usageInfo = new EndpointUsageInfo
        {
            EndpointPattern = endpointPattern,
            DisplayName = displayName,
            HttpMethod = httpMethod,
            HitCount = 0,
            LastAccessedUtc = null,
            RegisteredUtc = UtcNow
        };

        _endpointMetadata.TryAdd(endpointPattern, usageInfo);

        // Persist to Redis
        try
        {
            var metadataJson = System.Text.Json.JsonSerializer.Serialize(usageInfo);
            _redisDb.HashSet(_keyPrefix + EndpointHashKey, endpointPattern, metadataJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist endpoint metadata for {EndpointPattern} to Redis", endpointPattern);
        }
    }

    /// <summary>
    /// Records a hit to an endpoint in a thread-safe manner.
    /// </summary>
    public virtual void RecordHit(string endpointPattern)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        Interlocked.Increment(ref _totalRequests);
        
        // Add to in-memory buffer instead of directly writing to Redis
        _hitBuffer.AddOrUpdate(endpointPattern, 1, (_, current) => current + 1);
    }

    /// <summary>
    /// Gets all endpoint usage statistics.
    /// </summary>
    public IEnumerable<EndpointUsageInfo> GetAllEndpointUsage()
    {
        var results = new List<EndpointUsageInfo>();

        foreach (var endpointPattern in _endpointMetadata.Keys)
        {
            if (_endpointMetadata.TryGetValue(endpointPattern, out var metadata))
            {
                var hitCount = GetHitCountFromBuffer(endpointPattern);
                var lastAccessed = GetLastAccessedFromRedis(endpointPattern);

                var usage = new EndpointUsageInfo
                {
                    EndpointPattern = metadata.EndpointPattern,
                    DisplayName = metadata.DisplayName,
                    HttpMethod = metadata.HttpMethod,
                    HitCount = hitCount,
                    LastAccessedUtc = lastAccessed,
                    RegisteredUtc = metadata.RegisteredUtc
                };

                results.Add(usage);
            }
        }

        return results
            .OrderByDescending(e => e.HitCount)
            .ThenBy(e => e.EndpointPattern)
            .ToList();
    }

    /// <summary>
    /// Gets endpoints that have never been accessed.
    /// </summary>
    public IEnumerable<EndpointUsageInfo> GetUnusedEndpoints()
    {
        var allUsage = GetAllEndpointUsage();
        return allUsage
            .Where(e => e.HitCount == 0)
            .OrderBy(e => e.EndpointPattern)
            .ToList();
    }

    /// <summary>
    /// Gets comprehensive metrics about endpoint usage.
    /// </summary>
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
    /// Resets all tracking data.
    /// </summary>
    public void Reset()
    {
        _hitBuffer.Clear();
        _endpointMetadata.Clear();
        Interlocked.Exchange(ref _totalRequests, 0);

        try
        {
            var keysToDelete = _redisDb.HashKeys(_keyPrefix + EndpointHashKey);
            foreach (var key in keysToDelete)
            {
                _redisDb.KeyDelete(_keyPrefix + HitCountKeyFormat.Replace("{0}", key.ToString()));
                _redisDb.KeyDelete(_keyPrefix + LastAccessedKeyFormat.Replace("{0}", key.ToString()));
            }
            _redisDb.KeyDelete(_keyPrefix + EndpointHashKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset Redis data");
        }
    }

    /// <summary>
    /// Flushes the in-memory hit buffer to Redis.
    /// Called periodically by the RedisFlushHostedService.
    /// </summary>
    public void FlushHitBuffer()
    {
        if (_hitBuffer.IsEmpty)
            return;

        try
        {
            var batch = _redisDb.CreateBatch();
            var now = UtcNow;

            foreach (var kvp in _hitBuffer)
            {
                var endpointPattern = kvp.Key;
                var hitCount = kvp.Value;

                if (hitCount > 0)
                {
                    // Increment hit count
                    batch.StringIncrementAsync(
                        _keyPrefix + HitCountKeyFormat.Replace("{0}", endpointPattern),
                        hitCount);

                    // Update last accessed time
                    batch.StringSetAsync(
                        _keyPrefix + LastAccessedKeyFormat.Replace("{0}", endpointPattern),
                        now.Ticks.ToString());
                }
            }

            // Execute all operations in a batch
            batch.Execute();

            // Clear buffer after successful flush
            _hitBuffer.Clear();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush hit buffer to Redis. Will retry on next interval.");
        }
    }

    private int GetHitCountFromBuffer(string endpointPattern)
    {
        try
        {
            var bufferHits = _hitBuffer.TryGetValue(endpointPattern, out var hits) ? hits : 0;
            
            // Also get from Redis
            var redisValue = _redisDb.StringGet(_keyPrefix + HitCountKeyFormat.Replace("{0}", endpointPattern));
            var redisHits = redisValue.IsNull ? 0 : int.Parse(redisValue.ToString());

            return (int)(bufferHits + redisHits);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get hit count for {EndpointPattern}", endpointPattern);
            return 0;
        }
    }

    private DateTime? GetLastAccessedFromRedis(string endpointPattern)
    {
        try
        {
            var redisValue = _redisDb.StringGet(_keyPrefix + LastAccessedKeyFormat.Replace("{0}", endpointPattern));
            if (redisValue.IsNull)
                return null;

            if (long.TryParse(redisValue.ToString(), out var ticks))
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get last accessed time for {EndpointPattern}", endpointPattern);
            return null;
        }
    }

    private void LoadEndpointMetadata()
    {
        try
        {
            var metadataEntries = _redisDb.HashGetAll(_keyPrefix + EndpointHashKey);
            foreach (var entry in metadataEntries)
            {
                if (!entry.Value.IsNull)
                {
                    try
                    {
                        var metadata = System.Text.Json.JsonSerializer.Deserialize<EndpointUsageInfo>(entry.Value.ToString());
                        if (metadata != null)
                        {
                            _endpointMetadata.TryAdd(entry.Name.ToString(), metadata);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize endpoint metadata for {EndpointPattern}", entry.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load endpoint metadata from Redis");
        }
    }
}
