using System.Collections.Concurrent;
using System.Text.Json;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Redis-backed service for tracking endpoint usage with a small in-memory write buffer.
/// </summary>
public class RedisEndpointTrackerService : IEndpointTrackerService
{
    private const string EndpointHashKey = "endpoints:metadata";
    private const string MetadataDirtyKey = "endpoints:metadata-dirty";
    private const string HitCountKeyFormat = "hits:{0}";
    private const string LastAccessedKeyFormat = "last-accessed:{0}";
    private const string PendingBatchesKey = "sql-persistence:pending";
    private const string BatchMetadataKeyFormat = "sql-persistence:batch:{0}:metadata";
    private const string BatchHitsKeyFormat = "sql-persistence:batch:{0}:hits";
    private const string BatchLastAccessedKeyFormat = "sql-persistence:batch:{0}:last-accessed";
    private const string FlushBatchMarkerKeyFormat = "redis-buffer:batch:{0}";
    private const string PersistenceGenerationKey = "sql-persistence:generation";
    private const string PersistenceLockKey = "sql-persistence:lock";
    private const string PersistenceFenceKey = "sql-persistence:fence";
    private const string ResetFenceKey = "reset:fence";

    private static readonly TimeSpan PersistenceLockLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PersistenceLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PersistenceLockRenewInterval = TimeSpan.FromSeconds(10);

    private const string FlushHitScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 then
            return 0
        end

        local currentResetFence = redis.call('GET', KEYS[4]) or '0'
        if currentResetFence ~= ARGV[3] then
            return -1
        end

        local hitCount = tonumber(ARGV[1])
        local metadataCount = tonumber(ARGV[2])
        local argumentIndex = 4
        local keyIndex = 5

        for i = 1, hitCount do
            redis.call('INCRBY', KEYS[keyIndex], ARGV[argumentIndex])
            local previousTicks = redis.call('GET', KEYS[keyIndex + 1])
            local newTicks = ARGV[argumentIndex + 1]
            if (not previousTicks)
                or (#newTicks > #previousTicks)
                or (#newTicks == #previousTicks and newTicks > previousTicks) then
                redis.call('SET', KEYS[keyIndex + 1], newTicks)
            end
            argumentIndex = argumentIndex + 2
            keyIndex = keyIndex + 2
        end

        for i = 1, metadataCount do
            redis.call('HSET', KEYS[2], ARGV[argumentIndex], ARGV[argumentIndex + 1])
            argumentIndex = argumentIndex + 2
        end

        if metadataCount > 0 then
            redis.call('SET', KEYS[3], '1')
        end

        redis.call('SET', KEYS[1], '1', 'EX', ARGV[argumentIndex])
        return 1
        """;

    private const string CreateSnapshotScript = """
        local metadata = redis.call('HGETALL', KEYS[1])
        local metadataDirty = redis.call('EXISTS', KEYS[2])
        local capturedHits = 0

        for i = 1, #metadata, 2 do
            local pattern = metadata[i]
            local json = metadata[i + 1]
            local hitKey = ARGV[2] .. pattern
            local lastAccessedKey = ARGV[3] .. pattern
            local hitCount = redis.call('GET', hitKey)
            local lastAccessed = redis.call('GET', lastAccessedKey)

            if hitCount or metadataDirty == 1 then
                redis.call('HSET', KEYS[4], pattern, json)
            end

            if hitCount then
                redis.call('HSET', KEYS[5], pattern, hitCount)
                redis.call('DEL', hitKey)
                capturedHits = capturedHits + 1
            end

            if lastAccessed then
                redis.call('HSET', KEYS[6], pattern, lastAccessed)
                redis.call('DEL', lastAccessedKey)
            end
        end

        if capturedHits == 0 and metadataDirty == 0 then
            return 0
        end

        redis.call('DEL', KEYS[2])
        redis.call('SADD', KEYS[3], ARGV[1])
        redis.call('INCR', KEYS[7])
        return capturedHits + metadataDirty
        """;

    private const string CompleteSnapshotScript = """
        redis.call('DEL', KEYS[2], KEYS[3], KEYS[4])
        redis.call('SREM', KEYS[1], ARGV[1])
        redis.call('INCR', KEYS[5])
        return 1
        """;

    private const string ClearRedisScript = """
        if redis.call('GET', KEYS[1]) ~= ARGV[1] then
            return -1
        end

        local currentResetFence = tonumber(redis.call('GET', KEYS[3]) or '0')
        local requestedResetFence = tonumber(ARGV[2])
        if requestedResetFence < currentResetFence then
            return -2
        end

        for keyIndex = 4, #KEYS do
            redis.call('DEL', KEYS[keyIndex])
        end
        redis.call('SET', KEYS[3], ARGV[2])
        return redis.call('INCR', KEYS[2])
        """;

    private const string AcquirePersistenceLockScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then
            local currentFence = tonumber(redis.call('GET', KEYS[2]) or '0')
            local minimumFence = tonumber(ARGV[3])
            if currentFence < minimumFence then
                redis.call('SET', KEYS[2], ARGV[3])
            end
            local fence = redis.call('INCR', KEYS[2])
            redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
            return fence
        end
        return 0
        """;

    private const string RenewPersistenceLockScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    private const string ReleasePersistenceLockScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly IDatabase _redisDb;
    private readonly string _keyPrefix;
    private readonly ILogger<RedisEndpointTrackerService> _logger;
    private readonly SqlPersistenceStore? _sqlPersistenceStore;
    private readonly object _hitBufferLock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private ConcurrentDictionary<string, BufferedHit> _hitBuffer = new(StringComparer.Ordinal);
    private ConcurrentDictionary<string, EndpointUsageInfo> _metadataWriteBuffer = new(StringComparer.Ordinal);
    private readonly Queue<BufferedFlushBatch> _pendingFlushBatches = new();
    private readonly ConcurrentDictionary<string, EndpointUsageInfo> _endpointMetadata = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _rejectedEndpointPatterns = new(StringComparer.Ordinal);
    private long _observedResetFence;

    private readonly record struct BufferedHit(long Count, DateTime LastAccessedUtc);
    private sealed record BufferedFlushBatch(
        string BatchId,
        IReadOnlyDictionary<string, BufferedHit> Hits,
        IReadOnlyDictionary<string, EndpointUsageInfo> Metadata,
        long ResetFence);

    internal sealed class PersistenceLease : IAsyncDisposable
    {
        private readonly IDatabase _database;
        private readonly RedisKey _lockKey;
        private readonly RedisValue _ownerToken;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _renewalCancellation = new();
        private readonly CancellationTokenSource _leaseLost = new();
        private readonly Task _renewalTask;
        private int _disposed;

        internal PersistenceLease(
            IDatabase database,
            RedisKey lockKey,
            RedisValue ownerToken,
            long fenceToken,
            ILogger logger)
        {
            _database = database;
            _lockKey = lockKey;
            _ownerToken = ownerToken;
            FenceToken = fenceToken;
            _logger = logger;
            _renewalTask = RenewAsync();
        }

        internal CancellationToken LeaseLostToken => _leaseLost.Token;
        internal long FenceToken { get; }
        internal RedisValue OwnerToken => _ownerToken;

        internal void ThrowIfLeaseLost()
        {
            if (_leaseLost.IsCancellationRequested)
                throw new InvalidOperationException("The Redis persistence lease was lost before the operation completed.");
        }

        internal async Task<bool> VerifyOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_leaseLost.IsCancellationRequested)
                return false;

            var result = await _database.ScriptEvaluateAsync(
                    RenewPersistenceLockScript,
                    new RedisKey[] { _lockKey },
                    new RedisValue[]
                    {
                        _ownerToken,
                        (long)PersistenceLockLifetime.TotalMilliseconds
                    })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (long.TryParse(result.ToString(), out var renewed) && renewed == 1)
                return true;

            await _leaseLost.CancelAsync().ConfigureAwait(false);
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            await _renewalCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await _renewalTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_renewalCancellation.IsCancellationRequested)
            {
                // Normal lease disposal.
            }

            try
            {
                await _database.ScriptEvaluateAsync(
                    ReleasePersistenceLockScript,
                    new RedisKey[] { _lockKey },
                    new RedisValue[] { _ownerToken }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release the Redis persistence lease; it will expire automatically.");
            }

            _renewalCancellation.Dispose();
            _leaseLost.Dispose();
        }

        private async Task RenewAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(PersistenceLockRenewInterval, _renewalCancellation.Token).ConfigureAwait(false);
                    var result = await _database.ScriptEvaluateAsync(
                        RenewPersistenceLockScript,
                        new RedisKey[] { _lockKey },
                        new RedisValue[]
                        {
                            _ownerToken,
                            (long)PersistenceLockLifetime.TotalMilliseconds
                        }).ConfigureAwait(false);

                    if (!long.TryParse(result.ToString(), out var renewed) || renewed != 1)
                    {
                        _logger.LogError("The Redis persistence lease expired or was replaced.");
                        await _leaseLost.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (_renewalCancellation.IsCancellationRequested)
            {
                // Normal lease disposal.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to renew the Redis persistence lease.");
                await _leaseLost.CancelAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Exposes current UTC time for extensibility and testing.
    /// </summary>
    protected virtual DateTime UtcNow => DateTime.UtcNow;

    /// <summary>
    /// Creates a Redis-backed tracker without SQL persistence.
    /// </summary>
    public RedisEndpointTrackerService(
        IConnectionMultiplexer redisConnection,
        EndpointTrackerOptions options,
        ILogger<RedisEndpointTrackerService> logger)
        : this(redisConnection, options, null, logger)
    {
    }

    /// <summary>
    /// Creates a Redis-backed tracker with optional SQL persistence.
    /// </summary>
    public RedisEndpointTrackerService(
        IConnectionMultiplexer redisConnection,
        EndpointTrackerOptions options,
        SqlPersistenceStore? sqlPersistenceStore,
        ILogger<RedisEndpointTrackerService> logger)
    {
        ArgumentNullException.ThrowIfNull(redisConnection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _keyPrefix = (options.RedisKeyPrefix ?? "endpoint-tracker:").TrimEnd(':') + ":";
        _sqlPersistenceStore = sqlPersistenceStore;
        _redisDb = redisConnection.GetDatabase(options.RedisDatabase);

        if (IsRedisCluster(redisConnection))
        {
            throw new NotSupportedException(
                "Redis Cluster is not currently supported because durable transfers require multi-key atomic scripts. " +
                "Use a standalone or sentinel-managed Redis deployment.");
        }

        _observedResetFence = ReadResetFence();
        RefreshEndpointMetadataFromRedis();
    }

    /// <inheritdoc />
    public void RegisterEndpoint(string endpointPattern, string? displayName, string? httpMethod)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        lock (_hitBufferLock)
            RegisterEndpointCore(endpointPattern, displayName, httpMethod);
    }

    private bool RegisterEndpointCore(string endpointPattern, string? displayName, string? httpMethod)
    {
        if (_sqlPersistenceStore != null && endpointPattern.Length > 450)
        {
            if (_rejectedEndpointPatterns.TryAdd(endpointPattern, 0))
            {
                _logger.LogError(
                    "Endpoint {EndpointPattern} exceeds SQL persistence's 450-character route limit and will not be tracked.",
                    endpointPattern);
            }
            return false;
        }

        if (_sqlPersistenceStore != null)
        {
            displayName = Truncate(displayName, 1024);
            httpMethod = Truncate(httpMethod, 50);
        }

        var hadExistingMetadata = _endpointMetadata.TryGetValue(endpointPattern, out var previousMetadata);
        var now = EnsureUtc(UtcNow);
        var metadata = _endpointMetadata.AddOrUpdate(
            endpointPattern,
            _ => new EndpointUsageInfo
            {
                EndpointPattern = endpointPattern,
                DisplayName = displayName,
                HttpMethod = httpMethod,
                RegisteredUtc = now
            },
            (_, existing) => new EndpointUsageInfo
            {
                EndpointPattern = endpointPattern,
                DisplayName = displayName ?? existing.DisplayName,
                HttpMethod = httpMethod ?? existing.HttpMethod,
                RegisteredUtc = EarliestDateTime(existing.RegisteredUtc, now)
            });

        if (!hadExistingMetadata || !MetadataEquals(previousMetadata!, metadata))
        {
            _metadataWriteBuffer.AddOrUpdate(
                endpointPattern,
                metadata,
                (_, existing) => MergeMetadata(existing, metadata));
        }

        return true;
    }

    /// <inheritdoc />
    public virtual void RecordHit(string endpointPattern)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            return;

        var now = EnsureUtc(UtcNow);
        // Establish the reset epoch before accepting the hit. This deliberately happens
        // before the local lock: if a reset completes before RecordHit starts, its fence
        // is observed and the new hit cannot be discarded as stale on the next flush.
        var currentResetFence = ReadResetFence();
        lock (_hitBufferLock)
        {
            ReconcileResetFenceCore(currentResetFence);

            if (!_endpointMetadata.ContainsKey(endpointPattern) &&
                !RegisterEndpointCore(endpointPattern, null, null))
            {
                return;
            }

            _hitBuffer.AddOrUpdate(
                endpointPattern,
                _ => new BufferedHit(1, now),
                (_, existing) => new BufferedHit(existing.Count + 1, LatestDateTime(existing.LastAccessedUtc, now)));
        }
    }

    /// <inheritdoc />
    public IEnumerable<EndpointUsageInfo> GetAllEndpointUsage()
    {
        const int maximumSnapshotAttempts = 5;
        List<EndpointUsageInfo>? latestSnapshot = null;

        for (var attempt = 1; attempt <= maximumSnapshotAttempts; attempt++)
        {
            var generationBefore = ReadPersistenceGeneration();
            RefreshEndpointMetadataFromRedis();

            IReadOnlyList<RedisPersistenceBatch> pendingBatches;
            try
            {
                pendingBatches = GetPendingPersistenceBatchesAsync(CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read pending Redis-to-SQL batches for the metrics response.");
                pendingBatches = Array.Empty<RedisPersistenceBatch>();
            }

            var aggregate = new Dictionary<string, EndpointUsageInfo>(StringComparer.Ordinal);
            if (_sqlPersistenceStore != null)
            {
                try
                {
                    var sqlSnapshot = _sqlPersistenceStore
                        .GetEndpointUsageSnapshotAsync(
                            pendingBatches.Select(x => x.BatchId),
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    foreach (var sqlUsage in sqlSnapshot.EndpointUsage)
                        MergeAbsolute(aggregate, sqlUsage);

                    foreach (var batch in pendingBatches)
                    {
                        if (!sqlSnapshot.ProcessedBatchIds.Contains(batch.BatchId))
                            MergeBatchDelta(aggregate, batch);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to read persisted endpoint metrics from SQL. Returning all available Redis data.");
                    foreach (var batch in pendingBatches)
                        MergeBatchDelta(aggregate, batch);
                }
            }
            else
            {
                foreach (var batch in pendingBatches)
                    MergeBatchDelta(aggregate, batch);
            }

            foreach (var usage in GetLiveEndpointUsage())
                MergeDelta(aggregate, usage);

            foreach (var metadata in _endpointMetadata.Values)
            {
                if (!aggregate.ContainsKey(metadata.EndpointPattern))
                    MergeAbsolute(aggregate, metadata);
            }

            latestSnapshot = aggregate.Values
                .OrderByDescending(x => x.HitCount)
                .ThenBy(x => x.EndpointPattern, StringComparer.Ordinal)
                .ToList();

            if (generationBefore == ReadPersistenceGeneration())
                return latestSnapshot;

            _logger.LogDebug(
                "Redis-to-SQL state changed while metrics were being read; retrying a consistent snapshot ({Attempt}/{MaximumAttempts}).",
                attempt,
                maximumSnapshotAttempts);
        }

        _logger.LogWarning(
            "Redis-to-SQL state changed throughout {AttemptCount} metrics read attempts; returning the latest complete snapshot.",
            maximumSnapshotAttempts);
        return latestSnapshot is null
            ? Array.Empty<EndpointUsageInfo>()
            : latestSnapshot;
    }

    /// <inheritdoc />
    public IEnumerable<EndpointUsageInfo> GetUnusedEndpoints() => GetAllEndpointUsage()
        .Where(x => x.HitCount == 0)
        .OrderBy(x => x.EndpointPattern, StringComparer.Ordinal)
        .ToList();

    /// <inheritdoc />
    public EndpointMetricsResponse GetMetrics()
    {
        var endpoints = GetAllEndpointUsage().ToList();
        var used = endpoints.Count(x => x.HitCount > 0);
        return new EndpointMetricsResponse
        {
            TotalEndpoints = endpoints.Count,
            UsedEndpoints = used,
            UnusedEndpoints = endpoints.Count - used,
            TotalRequests = endpoints.Aggregate(0L, (total, endpoint) => checked(total + endpoint.HitCount)),
            Endpoints = endpoints
        };
    }

    /// <inheritdoc />
    public void Reset()
    {
        var lease = (_sqlPersistenceStore == null
                ? AcquirePersistenceLeaseAsync(CancellationToken.None)
                : AcquireSqlPersistenceLeaseAsync(CancellationToken.None))
            .GetAwaiter()
            .GetResult();
        try
        {
            if (_sqlPersistenceStore != null)
            {
                _sqlPersistenceStore
                    .ClearAllFencedAsync(lease.FenceToken, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            lease.ThrowIfLeaseLost();
            ClearRedisDataAsync(lease, CancellationToken.None).GetAwaiter().GetResult();
            lease.ThrowIfLeaseLost();
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Clears active and pending EndpointTracker data from Redis.
    /// </summary>
    public void ClearRedisData()
    {
        var lease = AcquirePersistenceLeaseAsync(CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            ClearRedisDataAsync(lease, CancellationToken.None).GetAwaiter().GetResult();
            lease.ThrowIfLeaseLost();
        }
        finally
        {
            lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Flushes buffered hit counts to Redis and waits for Redis to acknowledge every write.
    /// </summary>
    public void FlushHitBuffer() => FlushHitBufferAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Flushes buffered hit counts to Redis and waits for Redis to acknowledge every write.
    /// </summary>
    public async Task FlushHitBufferAsync(CancellationToken cancellationToken)
    {
        lock (_hitBufferLock)
        {
            if (_hitBuffer.IsEmpty && _metadataWriteBuffer.IsEmpty && _pendingFlushBatches.Count == 0)
                return;
        }

        await using var lease = await AcquirePersistenceLeaseAsync(cancellationToken).ConfigureAwait(false);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lease.LeaseLostToken);
        ReconcileResetFence();
        await FlushHitBufferCoreAsync(operationCancellation.Token).ConfigureAwait(false);
        lease.ThrowIfLeaseLost();
    }

    private async Task FlushHitBufferCoreAsync(CancellationToken cancellationToken)
    {
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_hitBufferLock)
            {
                if (!_hitBuffer.IsEmpty || !_metadataWriteBuffer.IsEmpty)
                {
                    _pendingFlushBatches.Enqueue(new BufferedFlushBatch(
                        Guid.NewGuid().ToString("N"),
                        _hitBuffer,
                        _metadataWriteBuffer,
                        _observedResetFence));
                    _hitBuffer = new ConcurrentDictionary<string, BufferedHit>(StringComparer.Ordinal);
                    _metadataWriteBuffer = new ConcurrentDictionary<string, EndpointUsageInfo>(StringComparer.Ordinal);
                }
            }

            while (true)
            {
                BufferedFlushBatch? batch;
                lock (_hitBufferLock)
                    batch = _pendingFlushBatches.Count == 0 ? null : _pendingFlushBatches.Peek();

                if (batch == null)
                    return;

                cancellationToken.ThrowIfCancellationRequested();
                var keys = new List<RedisKey>(4 + (batch.Hits.Count * 2))
                {
                    FlushBatchMarkerKey(batch.BatchId),
                    MetadataKey,
                    MetadataDirtyRedisKey,
                    ResetFenceRedisKey
                };
                var values = new List<RedisValue>(
                    4 + (batch.Hits.Count * 2) + (batch.Metadata.Count * 2))
                {
                    batch.Hits.Count,
                    batch.Metadata.Count,
                    batch.ResetFence
                };
                foreach (var pair in batch.Hits.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    keys.Add(HitKey(pair.Key));
                    keys.Add(LastAccessedKey(pair.Key));
                    values.Add(pair.Value.Count);
                    values.Add(pair.Value.LastAccessedUtc.Ticks);
                }
                foreach (var pair in batch.Metadata.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    values.Add(pair.Key);
                    values.Add(JsonSerializer.Serialize(pair.Value));
                }
                values.Add((long)TimeSpan.FromDays(7).TotalSeconds);

                var flushResult = await _redisDb
                    .ScriptEvaluateAsync(FlushHitScript, keys.ToArray(), values.ToArray())
                    .ConfigureAwait(false);
                if (long.TryParse(flushResult.ToString(), out var flushStatus) && flushStatus == -1)
                {
                    throw new InvalidOperationException(
                        "The buffered Redis write belongs to an older reset generation and was not applied.");
                }

                lock (_hitBufferLock)
                {
                    if (_pendingFlushBatches.Count > 0 &&
                        _pendingFlushBatches.Peek().BatchId == batch.BatchId)
                    {
                        _pendingFlushBatches.Dequeue();
                    }
                }

                try
                {
                    await _redisDb.KeyDeleteAsync(FlushBatchMarkerKey(batch.BatchId)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "The temporary Redis flush marker {BatchId} will expire automatically.",
                        batch.BatchId);
                }
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    internal async Task<PersistenceLease> AcquirePersistenceLeaseAsync(
        CancellationToken cancellationToken,
        long minimumFenceToken = 0)
    {
        if (minimumFenceToken < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumFenceToken));

        var ownerToken = Guid.NewGuid().ToString("N");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _redisDb.ScriptEvaluateAsync(
                    AcquirePersistenceLockScript,
                    new RedisKey[]
                    {
                        PersistenceLockRedisKey,
                        PersistenceFenceRedisKey
                    },
                    new RedisValue[]
                    {
                        ownerToken,
                        (long)PersistenceLockLifetime.TotalMilliseconds,
                        minimumFenceToken
                    })
                .ConfigureAwait(false);

            if (long.TryParse(result.ToString(), out var fenceToken) && fenceToken > 0)
            {
                return new PersistenceLease(
                    _redisDb,
                    PersistenceLockRedisKey,
                    ownerToken,
                    fenceToken,
                    _logger);
            }

            await Task.Delay(PersistenceLockRetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    internal async Task<PersistenceLease> AcquireSqlPersistenceLeaseAsync(CancellationToken cancellationToken)
    {
        if (_sqlPersistenceStore == null)
            throw new InvalidOperationException("SQL persistence is not configured.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var durableFence = await _sqlPersistenceStore
                .GetCurrentFenceAsync(cancellationToken)
                .ConfigureAwait(false);
            var lease = await AcquirePersistenceLeaseAsync(cancellationToken, durableFence).ConfigureAwait(false);

            try
            {
                var reserved = await _sqlPersistenceStore
                    .ReserveFenceTokenAsync(lease.FenceToken, cancellationToken)
                    .ConfigureAwait(false);
                if (reserved && await lease.VerifyOwnershipAsync(cancellationToken).ConfigureAwait(false))
                    return lease;
            }
            catch
            {
                await lease.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task<IReadOnlyList<RedisPersistenceBatch>> PreparePersistenceBatchesAsync(
        CancellationToken cancellationToken)
    {
        RefreshEndpointMetadataFromRedis();
        ReconcileResetFence();
        await FlushHitBufferCoreAsync(cancellationToken).ConfigureAwait(false);
        await CreatePersistenceSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return await GetPendingPersistenceBatchesAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task CompletePersistenceBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _redisDb.ScriptEvaluateAsync(
            CompleteSnapshotScript,
            new RedisKey[]
            {
                PendingBatchesRedisKey,
                BatchMetadataKey(batchId),
                BatchHitsKey(batchId),
                BatchLastAccessedKey(batchId),
                PersistenceGenerationRedisKey
            },
            new RedisValue[] { batchId }).ConfigureAwait(false);
    }

    private async Task CreatePersistenceSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batchId = Guid.NewGuid().ToString("N");
        await _redisDb.ScriptEvaluateAsync(
            CreateSnapshotScript,
            new RedisKey[]
            {
                MetadataKey,
                MetadataDirtyRedisKey,
                PendingBatchesRedisKey,
                BatchMetadataKey(batchId),
                BatchHitsKey(batchId),
                BatchLastAccessedKey(batchId),
                PersistenceGenerationRedisKey
            },
            new RedisValue[]
            {
                batchId,
                _keyPrefix + "hits:",
                _keyPrefix + "last-accessed:"
            }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<RedisPersistenceBatch>> GetPendingPersistenceBatchesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batchIds = await _redisDb.SetMembersAsync(PendingBatchesRedisKey).ConfigureAwait(false);
        var batches = new List<RedisPersistenceBatch>(batchIds.Length);

        foreach (var batchValue in batchIds.OrderBy(x => x.ToString(), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchId = batchValue.ToString();
            if (string.IsNullOrWhiteSpace(batchId))
                continue;

            var metadataTask = _redisDb.HashGetAllAsync(BatchMetadataKey(batchId));
            var hitsTask = _redisDb.HashGetAllAsync(BatchHitsKey(batchId));
            var lastAccessedTask = _redisDb.HashGetAllAsync(BatchLastAccessedKey(batchId));
            await Task.WhenAll(metadataTask, hitsTask, lastAccessedTask).ConfigureAwait(false);

            var metadata = DeserializeMetadata(metadataTask.Result);
            var hits = hitsTask.Result.ToDictionary(x => x.Name.ToString(), x => ParseLong(x.Value), StringComparer.Ordinal);
            var lastAccessed = lastAccessedTask.Result.ToDictionary(x => x.Name.ToString(), x => ParseUtcTicks(x.Value), StringComparer.Ordinal);
            var patterns = new HashSet<string>(metadata.Keys, StringComparer.Ordinal);
            patterns.UnionWith(hits.Keys);
            patterns.UnionWith(lastAccessed.Keys);

            var usage = patterns
                .Where(pattern => _sqlPersistenceStore == null || pattern.Length <= 450)
                .Select(pattern =>
            {
                metadata.TryGetValue(pattern, out var endpointMetadata);
                hits.TryGetValue(pattern, out var hitCount);
                lastAccessed.TryGetValue(pattern, out var lastAccessedUtc);
                return new EndpointUsageInfo
                {
                    EndpointPattern = pattern,
                    DisplayName = _sqlPersistenceStore == null
                        ? endpointMetadata?.DisplayName
                        : Truncate(endpointMetadata?.DisplayName, 1024),
                    HttpMethod = _sqlPersistenceStore == null
                        ? endpointMetadata?.HttpMethod
                        : Truncate(endpointMetadata?.HttpMethod, 50),
                    HitCount = hitCount,
                    LastAccessedUtc = lastAccessedUtc,
                    RegisteredUtc = endpointMetadata?.RegisteredUtc ?? EnsureUtc(UtcNow)
                };
            }).ToList();

            batches.Add(new RedisPersistenceBatch(batchId, usage));
        }

        return batches;
    }

    private IReadOnlyList<EndpointUsageInfo> GetLiveEndpointUsage()
    {
        _flushLock.Wait();
        try
        {
            ReconcileResetFence();
            return GetLiveEndpointUsageCore();
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private IReadOnlyList<EndpointUsageInfo> GetLiveEndpointUsageCore()
    {
        Dictionary<string, BufferedHit> buffered;
        lock (_hitBufferLock)
        {
            buffered = new Dictionary<string, BufferedHit>(_hitBuffer, StringComparer.Ordinal);
            foreach (var pendingBatch in _pendingFlushBatches)
            {
                foreach (var pair in pendingBatch.Hits)
                {
                    if (buffered.TryGetValue(pair.Key, out var existing))
                    {
                        buffered[pair.Key] = new BufferedHit(
                            checked(existing.Count + pair.Value.Count),
                            LatestDateTime(existing.LastAccessedUtc, pair.Value.LastAccessedUtc));
                    }
                    else
                    {
                        buffered[pair.Key] = pair.Value;
                    }
                }
            }
        }

        var patterns = new HashSet<string>(_endpointMetadata.Keys, StringComparer.Ordinal);
        patterns.UnionWith(buffered.Keys);
        if (patterns.Count == 0)
            return Array.Empty<EndpointUsageInfo>();

        var orderedPatterns = patterns.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        RedisValue[] redisHits;
        RedisValue[] redisLastAccessed;
        try
        {
            redisHits = _redisDb.StringGet(orderedPatterns.Select(HitKey).ToArray());
            redisLastAccessed = _redisDb.StringGet(orderedPatterns.Select(LastAccessedKey).ToArray());
            if (redisHits is null || redisHits.Length != orderedPatterns.Length)
                redisHits = new RedisValue[orderedPatterns.Length];
            if (redisLastAccessed is null || redisLastAccessed.Length != orderedPatterns.Length)
                redisLastAccessed = new RedisValue[orderedPatterns.Length];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read live endpoint metrics from Redis.");
            redisHits = new RedisValue[orderedPatterns.Length];
            redisLastAccessed = new RedisValue[orderedPatterns.Length];
        }

        var result = new List<EndpointUsageInfo>(orderedPatterns.Length);
        for (var index = 0; index < orderedPatterns.Length; index++)
        {
            var pattern = orderedPatterns[index];
            _endpointMetadata.TryGetValue(pattern, out var metadata);
            buffered.TryGetValue(pattern, out var bufferedHit);
            var redisLast = ParseUtcTicks(redisLastAccessed[index]);
            DateTime? bufferedLast = bufferedHit.Count == 0 ? null : bufferedHit.LastAccessedUtc;
            result.Add(new EndpointUsageInfo
            {
                EndpointPattern = pattern,
                DisplayName = metadata?.DisplayName,
                HttpMethod = metadata?.HttpMethod,
                HitCount = checked(ParseLong(redisHits[index]) + bufferedHit.Count),
                LastAccessedUtc = LatestNullableDateTime(redisLast, bufferedLast),
                RegisteredUtc = metadata?.RegisteredUtc ?? EnsureUtc(UtcNow)
            });
        }

        return result;
    }

    private async Task ClearRedisDataAsync(PersistenceLease lease, CancellationToken cancellationToken)
    {
        var resetFenceToken = lease.FenceToken;
        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefreshEndpointMetadataFromRedis();
            ConcurrentDictionary<string, BufferedHit> clearedHits;
            ConcurrentDictionary<string, EndpointUsageInfo> clearedMetadataWrites;
            List<BufferedFlushBatch> clearedFlushBatches;
            Dictionary<string, EndpointUsageInfo> clearedEndpointMetadata;
            Dictionary<string, byte> clearedRejectedPatterns;
            long previousResetFence;
            List<string> pendingFlushBatchIds;
            List<string> endpointPatterns;
            lock (_hitBufferLock)
            {
                clearedHits = _hitBuffer;
                clearedMetadataWrites = _metadataWriteBuffer;
                clearedFlushBatches = _pendingFlushBatches.ToList();
                clearedEndpointMetadata = new Dictionary<string, EndpointUsageInfo>(
                    _endpointMetadata,
                    StringComparer.Ordinal);
                clearedRejectedPatterns = new Dictionary<string, byte>(
                    _rejectedEndpointPatterns,
                    StringComparer.Ordinal);
                previousResetFence = _observedResetFence;
                _hitBuffer = new ConcurrentDictionary<string, BufferedHit>(StringComparer.Ordinal);
                _metadataWriteBuffer = new ConcurrentDictionary<string, EndpointUsageInfo>(StringComparer.Ordinal);
                pendingFlushBatchIds = clearedFlushBatches.Select(x => x.BatchId).ToList();
                endpointPatterns = _endpointMetadata.Keys.ToList();
                _pendingFlushBatches.Clear();
                _endpointMetadata.Clear();
                _rejectedEndpointPatterns.Clear();
                _observedResetFence = resetFenceToken;
            }

            var keysToDelete = new HashSet<RedisKey>
            {
                MetadataKey,
                MetadataDirtyRedisKey,
                PendingBatchesRedisKey
            };
            foreach (var batchId in pendingFlushBatchIds)
                keysToDelete.Add(FlushBatchMarkerKey(batchId));
            foreach (var pattern in endpointPatterns)
            {
                keysToDelete.Add(HitKey(pattern));
                keysToDelete.Add(LastAccessedKey(pattern));
            }

            var pendingBatchIds = await _redisDb.SetMembersAsync(PendingBatchesRedisKey).ConfigureAwait(false);
            foreach (var value in pendingBatchIds)
            {
                var batchId = value.ToString();
                if (string.IsNullOrWhiteSpace(batchId))
                    continue;
                keysToDelete.Add(BatchMetadataKey(batchId));
                keysToDelete.Add(BatchHitsKey(batchId));
                keysToDelete.Add(BatchLastAccessedKey(batchId));
            }

            var scriptKeys = new RedisKey[3 + keysToDelete.Count];
            scriptKeys[0] = PersistenceLockRedisKey;
            scriptKeys[1] = PersistenceGenerationRedisKey;
            scriptKeys[2] = ResetFenceRedisKey;
            keysToDelete.CopyTo(scriptKeys, 3);
            var result = await _redisDb.ScriptEvaluateAsync(
                ClearRedisScript,
                scriptKeys,
                new RedisValue[] { lease.OwnerToken, resetFenceToken }).ConfigureAwait(false);
            if (long.TryParse(result.ToString(), out var clearStatus) && clearStatus < 0)
            {
                if (clearStatus == -1 && ReadResetFence() == previousResetFence)
                {
                    RestoreClearedLocalState(
                        clearedHits,
                        clearedMetadataWrites,
                        clearedFlushBatches,
                        clearedEndpointMetadata,
                        clearedRejectedPatterns,
                        previousResetFence);
                }

                throw new InvalidOperationException(
                    clearStatus == -1
                        ? "The Redis persistence lease was lost before reset data could be cleared."
                        : "A newer EndpointTracker reset superseded this reset operation.");
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private void RestoreClearedLocalState(
        IReadOnlyDictionary<string, BufferedHit> clearedHits,
        IReadOnlyDictionary<string, EndpointUsageInfo> clearedMetadataWrites,
        IReadOnlyList<BufferedFlushBatch> clearedFlushBatches,
        IReadOnlyDictionary<string, EndpointUsageInfo> clearedEndpointMetadata,
        IReadOnlyDictionary<string, byte> clearedRejectedPatterns,
        long previousResetFence)
    {
        lock (_hitBufferLock)
        {
            foreach (var pair in clearedHits)
            {
                _hitBuffer.AddOrUpdate(
                    pair.Key,
                    pair.Value,
                    (_, existing) => new BufferedHit(
                        checked(existing.Count + pair.Value.Count),
                        LatestDateTime(existing.LastAccessedUtc, pair.Value.LastAccessedUtc)));
            }

            foreach (var pair in clearedMetadataWrites)
            {
                _metadataWriteBuffer.AddOrUpdate(
                    pair.Key,
                    pair.Value,
                    (_, existing) => MergeMetadata(pair.Value, existing));
            }

            foreach (var batch in clearedFlushBatches)
                _pendingFlushBatches.Enqueue(batch);

            foreach (var pair in clearedEndpointMetadata)
            {
                _endpointMetadata.AddOrUpdate(
                    pair.Key,
                    pair.Value,
                    (_, existing) => MergeMetadata(pair.Value, existing));
            }

            foreach (var pair in clearedRejectedPatterns)
                _rejectedEndpointPatterns.TryAdd(pair.Key, pair.Value);

            _observedResetFence = previousResetFence;
        }
    }

    private void RefreshEndpointMetadataFromRedis()
    {
        try
        {
            var redisMetadata = DeserializeMetadata(_redisDb.HashGetAll(MetadataKey));
            var oversizedPatterns = new List<string>();
            lock (_hitBufferLock)
            {
                foreach (var pair in redisMetadata)
                {
                    if (_sqlPersistenceStore != null && pair.Key.Length > 450)
                    {
                        _endpointMetadata.TryRemove(pair.Key, out _);
                        _hitBuffer.TryRemove(pair.Key, out _);
                        _metadataWriteBuffer.TryRemove(pair.Key, out _);
                        oversizedPatterns.Add(pair.Key);
                        continue;
                    }
                    _endpointMetadata.AddOrUpdate(
                        pair.Key,
                        pair.Value,
                        (_, existing) => MergeMetadata(existing, pair.Value));
                }
            }

            foreach (var endpointPattern in oversizedPatterns)
                RejectOversizedPersistedEndpoint(endpointPattern);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh endpoint metadata from Redis.");
        }
    }

    private void RejectOversizedPersistedEndpoint(string endpointPattern)
    {
        if (_rejectedEndpointPatterns.TryAdd(endpointPattern, 0))
        {
            _logger.LogError(
                "Existing Redis endpoint {EndpointPattern} exceeds SQL persistence's 450-character route limit. " +
                "Its unsupported Redis data will be discarded so it cannot block later persistence batches.",
                endpointPattern);
        }

        try
        {
            _redisDb.HashDelete(MetadataKey, endpointPattern);
            _redisDb.KeyDelete(new RedisKey[]
            {
                HitKey(endpointPattern),
                LastAccessedKey(endpointPattern)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to remove unsupported oversized Redis endpoint {EndpointPattern}; cleanup will be retried.",
                endpointPattern);
        }
    }

    private static Dictionary<string, EndpointUsageInfo> DeserializeMetadata(HashEntry[] entries)
    {
        var result = new Dictionary<string, EndpointUsageInfo>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Value.IsNull)
                continue;
            try
            {
                var metadata = JsonSerializer.Deserialize<EndpointUsageInfo>(entry.Value.ToString());
                if (metadata != null)
                    result[entry.Name.ToString()] = metadata;
            }
            catch (JsonException)
            {
                // Ignore a malformed entry so one endpoint cannot make all metrics unavailable.
            }
        }
        return result;
    }

    private static void MergeAbsolute(IDictionary<string, EndpointUsageInfo> aggregate, EndpointUsageInfo value)
    {
        if (!aggregate.TryGetValue(value.EndpointPattern, out var existing))
        {
            aggregate[value.EndpointPattern] = Clone(value);
            return;
        }
        aggregate[value.EndpointPattern] = new EndpointUsageInfo
        {
            EndpointPattern = value.EndpointPattern,
            DisplayName = value.DisplayName ?? existing.DisplayName,
            HttpMethod = value.HttpMethod ?? existing.HttpMethod,
            HitCount = value.HitCount,
            LastAccessedUtc = LatestNullableDateTime(existing.LastAccessedUtc, value.LastAccessedUtc),
            RegisteredUtc = EarliestDateTime(existing.RegisteredUtc, value.RegisteredUtc)
        };
    }

    private static void MergeDelta(IDictionary<string, EndpointUsageInfo> aggregate, EndpointUsageInfo delta)
    {
        if (!aggregate.TryGetValue(delta.EndpointPattern, out var existing))
        {
            aggregate[delta.EndpointPattern] = Clone(delta);
            return;
        }
        aggregate[delta.EndpointPattern] = new EndpointUsageInfo
        {
            EndpointPattern = delta.EndpointPattern,
            DisplayName = delta.DisplayName ?? existing.DisplayName,
            HttpMethod = delta.HttpMethod ?? existing.HttpMethod,
            HitCount = checked(existing.HitCount + delta.HitCount),
            LastAccessedUtc = LatestNullableDateTime(existing.LastAccessedUtc, delta.LastAccessedUtc),
            RegisteredUtc = EarliestDateTime(existing.RegisteredUtc, delta.RegisteredUtc)
        };
    }

    private static void MergeBatchDelta(
        IDictionary<string, EndpointUsageInfo> aggregate,
        RedisPersistenceBatch batch)
    {
        foreach (var pendingUsage in batch.EndpointUsage)
            MergeDelta(aggregate, pendingUsage);
    }

    private static EndpointUsageInfo MergeMetadata(EndpointUsageInfo left, EndpointUsageInfo right) => new()
    {
        EndpointPattern = left.EndpointPattern,
        DisplayName = right.DisplayName ?? left.DisplayName,
        HttpMethod = right.HttpMethod ?? left.HttpMethod,
        RegisteredUtc = EarliestDateTime(left.RegisteredUtc, right.RegisteredUtc)
    };

    private static bool MetadataEquals(EndpointUsageInfo left, EndpointUsageInfo right) =>
        string.Equals(left.EndpointPattern, right.EndpointPattern, StringComparison.Ordinal) &&
        string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
        string.Equals(left.HttpMethod, right.HttpMethod, StringComparison.Ordinal) &&
        EnsureUtc(left.RegisteredUtc) == EnsureUtc(right.RegisteredUtc);

    private static EndpointUsageInfo Clone(EndpointUsageInfo value) => new()
    {
        EndpointPattern = value.EndpointPattern,
        DisplayName = value.DisplayName,
        HttpMethod = value.HttpMethod,
        HitCount = value.HitCount,
        LastAccessedUtc = value.LastAccessedUtc,
        RegisteredUtc = value.RegisteredUtc
    };

    private void ReconcileResetFence()
    {
        var currentResetFence = ReadResetFence();
        lock (_hitBufferLock)
            ReconcileResetFenceCore(currentResetFence);
    }

    private void ReconcileResetFenceCore(long currentResetFence)
    {
        if (currentResetFence == _observedResetFence)
            return;

        foreach (var pendingBatch in _pendingFlushBatches)
        {
            foreach (var pair in pendingBatch.Metadata)
            {
                _metadataWriteBuffer.AddOrUpdate(
                    pair.Key,
                    pair.Value,
                    (_, existing) => MergeMetadata(existing, pair.Value));
            }
        }

        foreach (var pair in _endpointMetadata)
        {
            _metadataWriteBuffer.AddOrUpdate(
                pair.Key,
                pair.Value,
                (_, existing) => MergeMetadata(existing, pair.Value));
        }

        _hitBuffer = new ConcurrentDictionary<string, BufferedHit>(StringComparer.Ordinal);
        _pendingFlushBatches.Clear();
        _observedResetFence = currentResetFence;
    }

    private long ReadResetFence()
    {
        try
        {
            return ParseLong(_redisDb.StringGet(ResetFenceRedisKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the EndpointTracker reset fence.");
            return _observedResetFence;
        }
    }

    private long ReadPersistenceGeneration()
    {
        try
        {
            return ParseLong(_redisDb.StringGet(PersistenceGenerationRedisKey));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the Redis-to-SQL persistence generation.");
            return 0;
        }
    }

    private static bool IsRedisCluster(IConnectionMultiplexer connection)
    {
        foreach (var endpoint in connection.GetEndPoints())
        {
            if (connection.GetServer(endpoint).ServerType == ServerType.Cluster)
                return true;
        }
        return false;
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];

    private static long ParseLong(RedisValue value) =>
        value.IsNull || !long.TryParse(value.ToString(), out var parsed) ? 0 : parsed;

    private static DateTime? ParseUtcTicks(RedisValue value)
    {
        if (value.IsNull || !long.TryParse(value.ToString(), out var ticks) ||
            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }
        return new DateTime(ticks, DateTimeKind.Utc);
    }
    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
    private static DateTime EarliestDateTime(DateTime left, DateTime right)
    {
        if (left == default) return EnsureUtc(right);
        if (right == default) return EnsureUtc(left);
        return EnsureUtc(left) <= EnsureUtc(right) ? EnsureUtc(left) : EnsureUtc(right);
    }
    private static DateTime LatestDateTime(DateTime left, DateTime right) => EnsureUtc(left) >= EnsureUtc(right) ? EnsureUtc(left) : EnsureUtc(right);
    private static DateTime? LatestNullableDateTime(DateTime? left, DateTime? right)
    {
        if (!left.HasValue) return right.HasValue ? EnsureUtc(right.Value) : null;
        if (!right.HasValue) return EnsureUtc(left.Value);
        return LatestDateTime(left.Value, right.Value);
    }

    private RedisKey MetadataKey => _keyPrefix + EndpointHashKey;
    private RedisKey MetadataDirtyRedisKey => _keyPrefix + MetadataDirtyKey;
    private RedisKey PendingBatchesRedisKey => _keyPrefix + PendingBatchesKey;
    private RedisKey HitKey(string pattern) => _keyPrefix + HitCountKeyFormat.Replace("{0}", pattern);
    private RedisKey LastAccessedKey(string pattern) => _keyPrefix + LastAccessedKeyFormat.Replace("{0}", pattern);
    private RedisKey BatchMetadataKey(string batchId) => _keyPrefix + BatchMetadataKeyFormat.Replace("{0}", batchId);
    private RedisKey BatchHitsKey(string batchId) => _keyPrefix + BatchHitsKeyFormat.Replace("{0}", batchId);
    private RedisKey BatchLastAccessedKey(string batchId) => _keyPrefix + BatchLastAccessedKeyFormat.Replace("{0}", batchId);
    private RedisKey FlushBatchMarkerKey(string batchId) => _keyPrefix + FlushBatchMarkerKeyFormat.Replace("{0}", batchId);
    private RedisKey PersistenceGenerationRedisKey => _keyPrefix + PersistenceGenerationKey;
    private RedisKey PersistenceLockRedisKey => _keyPrefix + PersistenceLockKey;
    private RedisKey PersistenceFenceRedisKey => _keyPrefix + PersistenceFenceKey;
    private RedisKey ResetFenceRedisKey => _keyPrefix + ResetFenceKey;
}
