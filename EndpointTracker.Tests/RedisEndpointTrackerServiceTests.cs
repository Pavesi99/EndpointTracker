using EndpointTracker.AspNetCore.Extensions;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using System.Net;
using System.Text.Json;
using Xunit;

namespace EndpointTracker.Tests;

public class RedisEndpointTrackerServiceTests
{
    private const string EndpointPattern = "GET /api/test";
    private const string KeyPrefix = "test-endpoint-tracker:";

    [Fact]
    public void RedisOnlyConstructor_TracksHitsWithoutSqlStore()
    {
        var (service, _) = CreateService();

        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");
        service.RecordHit(EndpointPattern);
        service.RecordHit(EndpointPattern);

        var usage = Assert.Single(service.GetAllEndpointUsage());

        Assert.Equal(EndpointPattern, usage.EndpointPattern);
        Assert.Equal(2L, usage.HitCount);
        Assert.Equal("Get Test", usage.DisplayName);
        Assert.Equal("GET", usage.HttpMethod);
    }

    [Fact]
    public void GetUnusedEndpoints_ReturnsNullLastAccessedUtc()
    {
        var (service, _) = CreateService();
        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");

        var unused = Assert.Single(service.GetUnusedEndpoints());

        Assert.Equal(0L, unused.HitCount);
        Assert.Null(unused.LastAccessedUtc);
    }

    [Fact]
    public void GetAllEndpointUsage_SupportsCountsLargerThanInt32()
    {
        var expectedCount = (long)int.MaxValue + 42;
        var (service, database) = CreateService();
        ConfigureRedisHitCount(database, expectedCount);
        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");

        var usage = Assert.Single(service.GetAllEndpointUsage());

        Assert.Equal(expectedCount, usage.HitCount);
    }

    [Fact]
    public void GetMetrics_TotalRequestsMatchesEndpointTotalsIncludingExistingRedisHits()
    {
        const long existingRedisHits = 8;
        var (service, database) = CreateService();
        ConfigureRedisHitCount(database, existingRedisHits);
        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");
        service.RecordHit(EndpointPattern);
        service.RecordHit(EndpointPattern);

        var metrics = service.GetMetrics();

        Assert.Equal(10L, metrics.TotalRequests);
        Assert.Equal(metrics.Endpoints.Sum(endpoint => endpoint.HitCount), metrics.TotalRequests);
    }

    [Fact]
    public async Task FlushHitBuffer_WaitsForRedisAndPreservesHitsRecordedDuringFlush()
    {
        var (service, database) = CreateService();
        var scriptCompletion = NewCompletionSource<RedisResult>();
        var scriptCalled = NewCompletionSource();
        long persistedHits = 0;

        ConfigureRedisHitCount(database, () => persistedHits);
        ConfigureFencedScripts(database, (_, _) =>
        {
            scriptCalled.TrySetResult();
            return scriptCompletion.Task;
        });

        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");
        service.RecordHit(EndpointPattern);
        service.RecordHit(EndpointPattern);

        var cancellationToken = TestContext.Current.CancellationToken;
        var flushTask = Task.Run(service.FlushHitBuffer, cancellationToken);
        await scriptCalled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        service.RecordHit(EndpointPattern);
        var waitedForRedisOperations = !flushTask.IsCompleted;

        persistedHits = 2;
        scriptCompletion.SetResult(RedisResult.Create((RedisValue)2L));
        await flushTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.True(waitedForRedisOperations, "FlushHitBuffer returned before its Redis operations completed.");
        Assert.Equal(3L, Assert.Single(service.GetAllEndpointUsage()).HitCount);
    }

    [Fact]
    public void FlushHitBuffer_WhenRedisOperationFails_RetainsHitsForRetry()
    {
        var (service, database) = CreateService();

        ConfigureFencedScripts(
            database,
            (_, _) => Task.FromException<RedisResult>(new RedisException("Test failure")));

        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");
        service.RecordHit(EndpointPattern);
        service.RecordHit(EndpointPattern);

        Assert.Throws<RedisException>(service.FlushHitBuffer);

        Assert.Equal(2L, Assert.Single(service.GetAllEndpointUsage()).HitCount);
    }

    [Fact]
    public void FlushHitBuffer_AfterAmbiguousFailure_RetriesSameAtomicBatchExactlyOnce()
    {
        const string firstEndpoint = "GET /first";
        const string secondEndpoint = "GET /second";
        var (service, database) = CreateService();
        var persistedHits = new Dictionary<string, long>(StringComparer.Ordinal);
        var scriptCalls = new List<(string[] Keys, string[] Values)>();
        ConfigureRedisHitCount(
            database,
            key => persistedHits.TryGetValue(key.ToString(), out var count) ? count : 0);
        ConfigureFencedScripts(
            database,
            (redisKeys, redisValues) =>
            {
                var keys = redisKeys.Select(key => key.ToString()).ToArray();
                var values = redisValues.Select(value => value.ToString()).ToArray();
                scriptCalls.Add((keys, values));

                Assert.Equal(8, keys.Length);
                Assert.Equal(KeyPrefix + "endpoints:metadata", keys[1]);
                Assert.Equal(KeyPrefix + "endpoints:metadata-dirty", keys[2]);
                Assert.Equal(KeyPrefix + "reset:fence", keys[3]);
                Assert.Equal("2", values[0]);
                Assert.Equal("2", values[1]);
                Assert.Equal("0", values[2]);

                if (scriptCalls.Count == 1)
                {
                    // Simulate Redis applying the atomic script before the client observes a timeout.
                    persistedHits[KeyPrefix + "hits:" + firstEndpoint] = 1;
                    persistedHits[KeyPrefix + "hits:" + secondEndpoint] = 1;
                    return Task.FromException<RedisResult>(new RedisException("Ambiguous test failure"));
                }

                // Redis recognizes the stable batch marker and does not apply the increments again.
                return Task.FromResult(RedisResult.Create((RedisValue)0L));
            });
        service.RegisterEndpoint(firstEndpoint, null, "GET");
        service.RegisterEndpoint(secondEndpoint, null, "GET");
        service.RecordHit(firstEndpoint);
        service.RecordHit(secondEndpoint);

        Assert.Throws<RedisException>(service.FlushHitBuffer);
        Assert.Single(scriptCalls);

        service.FlushHitBuffer();

        var usage = service.GetAllEndpointUsage().ToDictionary(item => item.EndpointPattern);
        Assert.Equal(2, scriptCalls.Count);
        Assert.Equal(scriptCalls[0].Keys, scriptCalls[1].Keys);
        Assert.Equal(scriptCalls[0].Values, scriptCalls[1].Values);
        Assert.Equal(1L, usage[firstEndpoint].HitCount);
        Assert.Equal(1L, usage[secondEndpoint].HitCount);
        Assert.Equal(2L, service.GetMetrics().TotalRequests);
    }

    [Fact]
    public void FlushHitBuffer_ZeroHitMetadataFailure_RetriesSameAtomicMetadataWrite()
    {
        const string zeroHitEndpoint = "GET /registered-only";
        var connection = CreateRedisSubstitute(out var database);
        var options = CreateOptions(connection);
        var scriptCalls = new List<(string[] Keys, string[] Values)>();
        HashEntry[] persistedMetadata = [];
        database.HashGetAll(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ => persistedMetadata);
        ConfigureFencedScripts(
            database,
            (redisKeys, redisValues) =>
            {
                var keys = redisKeys.Select(key => key.ToString()).ToArray();
                var values = redisValues.Select(value => value.ToString()).ToArray();
                scriptCalls.Add((keys, values));

                Assert.Equal(4, keys.Length);
                Assert.Equal(KeyPrefix + "endpoints:metadata", keys[1]);
                Assert.Equal(KeyPrefix + "endpoints:metadata-dirty", keys[2]);
                Assert.Equal(KeyPrefix + "reset:fence", keys[3]);
                Assert.Equal("0", values[0]);
                Assert.Equal("1", values[1]);
                Assert.Equal("0", values[2]);
                Assert.Equal(zeroHitEndpoint, values[3]);

                if (scriptCalls.Count == 1)
                    return Task.FromException<RedisResult>(new RedisException("First metadata write failed"));

                persistedMetadata = [new HashEntry(values[3], values[4])];
                return Task.FromResult(RedisResult.Create((RedisValue)1L));
            });
        var service = new RedisEndpointTrackerService(
            connection,
            options,
            NullLogger<RedisEndpointTrackerService>.Instance);
        service.RegisterEndpoint(zeroHitEndpoint, "Registered only", "GET");

        Assert.Throws<RedisException>(service.FlushHitBuffer);
        service.FlushHitBuffer();

        Assert.Equal(2, scriptCalls.Count);
        Assert.Equal(scriptCalls[0].Keys, scriptCalls[1].Keys);
        Assert.Equal(scriptCalls[0].Values, scriptCalls[1].Values);
        var reloadedService = new RedisEndpointTrackerService(
            connection,
            options,
            NullLogger<RedisEndpointTrackerService>.Instance);
        var unused = Assert.Single(reloadedService.GetUnusedEndpoints());
        Assert.Equal(zeroHitEndpoint, unused.EndpointPattern);
        Assert.Equal("Registered only", unused.DisplayName);
        Assert.Equal(0L, unused.HitCount);
        Assert.Null(unused.LastAccessedUtc);
    }

    [Fact]
    public void SqlPersistence_OversizedEndpointIsRejectedBeforeRedisFlush()
    {
        var connection = CreateRedisSubstitute(out var database);
        var options = CreateOptions(connection);
        options.UseSqlPersistence = true;
        options.SqlProvider = "PostgreSQL";
        options.SqlConnectionString = "Host=localhost;Database=endpoint_tracker;Username=test;Password=test";
        var sqlStore = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var service = new RedisEndpointTrackerService(
            connection,
            options,
            sqlStore,
            NullLogger<RedisEndpointTrackerService>.Instance);
        var oversizedEndpoint = new string('x', 451);
        var flushScriptCalls = 0;
        ConfigureFencedScripts(database, (_, _) =>
        {
            flushScriptCalls++;
            return Task.FromResult(RedisResult.Create((RedisValue)1L));
        });

        service.RegisterEndpoint(oversizedEndpoint, "Too large", "GET");
        service.RecordHit(oversizedEndpoint);
        service.FlushHitBuffer();

        Assert.Equal(0, flushScriptCalls);
    }

    [Fact]
    public async Task GetAllEndpointUsage_WhileFlushIsBlocked_DoesNotDoubleCountCapturedHits()
    {
        var (service, database) = CreateService();
        var flushCompletion = NewCompletionSource<RedisResult>();
        var flushCalled = NewCompletionSource();
        long persistedHits = 0;
        ConfigureRedisHitCount(database, () => persistedHits);
        ConfigureFencedScripts(database, (_, _) =>
        {
            persistedHits = 1;
            flushCalled.TrySetResult();
            return flushCompletion.Task;
        });
        var readReachedPendingBatchCheck = NewCompletionSource();
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                readReachedPendingBatchCheck.TrySetResult();
                return Task.FromResult(Array.Empty<RedisValue>());
            });
        service.RegisterEndpoint(EndpointPattern, "Get Test", "GET");
        service.RecordHit(EndpointPattern);
        var cancellationToken = TestContext.Current.CancellationToken;

        var flushTask = service.FlushHitBufferAsync(cancellationToken);
        await flushCalled.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var readTask = Task.Run(() =>
            Assert.Single(service.GetAllEndpointUsage()), cancellationToken);
        await readReachedPendingBatchCheck.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);

        Assert.False(readTask.IsCompleted, "Metrics read did not wait for the in-flight flush to settle.");
        flushCompletion.SetResult(RedisResult.Create((RedisValue)1L));

        await flushTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var usage = await readTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.Equal(1L, usage.HitCount);
    }

    [Fact]
    public void RecordHit_NewEndpoint_FlushesMetadataAndHitInOneAtomicScript()
    {
        const string newEndpoint = "POST /atomic-registration";
        var (service, database) = CreateService();
        RedisKey[]? capturedKeys = null;
        RedisValue[]? capturedValues = null;
        ConfigureFencedScripts(database, (keys, values) =>
        {
            capturedKeys = keys;
            capturedValues = values;
            return Task.FromResult(RedisResult.Create((RedisValue)1L));
        });

        service.RecordHit(newEndpoint);
        service.FlushHitBuffer();

        Assert.NotNull(capturedKeys);
        Assert.NotNull(capturedValues);
        Assert.Equal(6, capturedKeys.Length);
        Assert.Equal(KeyPrefix + "endpoints:metadata", capturedKeys[1].ToString());
        Assert.Equal(KeyPrefix + "endpoints:metadata-dirty", capturedKeys[2].ToString());
        Assert.Equal(KeyPrefix + "reset:fence", capturedKeys[3].ToString());
        Assert.Equal(KeyPrefix + "hits:" + newEndpoint, capturedKeys[4].ToString());
        Assert.Equal(KeyPrefix + "last-accessed:" + newEndpoint, capturedKeys[5].ToString());
        Assert.Equal("1", capturedValues[0].ToString());
        Assert.Equal("1", capturedValues[1].ToString());
        Assert.Equal("0", capturedValues[2].ToString());
        Assert.Equal("1", capturedValues[3].ToString());
        Assert.Equal(newEndpoint, capturedValues[5].ToString());
        var metadata = JsonSerializer.Deserialize<EndpointUsageInfo>(capturedValues[6].ToString());
        Assert.NotNull(metadata);
        Assert.Equal(newEndpoint, metadata.EndpointPattern);
    }

    [Fact]
    public void SqlPersistence_RemovesOversizedExistingRedisEndpointAndFlushesValidHitsOnly()
    {
        const string validEndpoint = "GET /valid-existing";
        var oversizedEndpoint = new string('o', 451);
        var connection = CreateRedisSubstitute(out var database);
        var registeredUtc = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        database.HashGetAll(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(
            [
                MetadataEntry(validEndpoint, registeredUtc),
                MetadataEntry(oversizedEndpoint, registeredUtc)
            ]);
        var options = CreateOptions(connection);
        options.UseSqlPersistence = true;
        options.SqlProvider = "PostgreSQL";
        options.SqlConnectionString = "Host=localhost;Database=endpoint_tracker;Username=test;Password=test";
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        RedisKey[]? flushKeys = null;
        RedisValue[]? flushValues = null;
        ConfigureFencedScripts(database, (keys, values) =>
        {
            flushKeys = keys;
            flushValues = values;
            return Task.FromResult(RedisResult.Create((RedisValue)1L));
        });

        var service = new RedisEndpointTrackerService(
            connection,
            options,
            store,
            NullLogger<RedisEndpointTrackerService>.Instance);
        service.RecordHit(validEndpoint);
        service.FlushHitBuffer();

        database.Received().HashDelete(
            Arg.Is<RedisKey>(key => key.ToString() == KeyPrefix + "endpoints:metadata"),
            Arg.Is<RedisValue>(value => value.ToString() == oversizedEndpoint),
            Arg.Any<CommandFlags>());
        database.Received().KeyDelete(
            Arg.Is<RedisKey[]>(keys =>
                keys != null &&
                keys.Any(key => key.ToString() == KeyPrefix + "hits:" + oversizedEndpoint) &&
                keys.Any(key => key.ToString() == KeyPrefix + "last-accessed:" + oversizedEndpoint)),
            Arg.Any<CommandFlags>());
        Assert.NotNull(flushKeys);
        Assert.NotNull(flushValues);
        Assert.DoesNotContain(flushKeys, key => key.ToString().Contains(oversizedEndpoint, StringComparison.Ordinal));
        Assert.DoesNotContain(flushValues, value => value.ToString().Contains(oversizedEndpoint, StringComparison.Ordinal));
    }

    [Fact]
    public void RedisCluster_ThrowsBeforeTrackerStarts()
    {
        var connection = CreateRedisSubstitute(out _);
        var server = Substitute.For<IServer>();
        var endpoint = new DnsEndPoint("redis-cluster.test", 6379);
        connection.GetEndPoints(Arg.Any<bool>()).Returns([endpoint]);
        connection.GetServer(Arg.Any<EndPoint>(), Arg.Any<object>()).Returns(server);
        server.ServerType.Returns(ServerType.Cluster);
        var options = CreateOptions(connection);

        var exception = Assert.Throws<NotSupportedException>(() =>
            new RedisEndpointTrackerService(
                connection,
                options,
                NullLogger<RedisEndpointTrackerService>.Instance));

        Assert.Contains("Redis Cluster is not currently supported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEndpointTrackerRedis_WithoutSqlPersistence_ResolvesRedisTracker()
    {
        var connection = CreateRedisSubstitute(out _);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEndpointTrackerRedis(connection, options =>
        {
            options.UseSqlPersistence = false;
            options.RedisKeyPrefix = KeyPrefix;
        });

        using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<IEndpointTrackerService>();
        Assert.IsType<RedisEndpointTrackerService>(tracker);
        Assert.Null(provider.GetService<SqlPersistenceStore>());
    }

    [Fact]
    public void AddEndpointTrackerRedis_WithSqlPersistence_ResolvesTrackerAndStore()
    {
        var connection = CreateRedisSubstitute(out _);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEndpointTrackerRedis(connection, options =>
        {
            options.UseSqlPersistence = true;
            options.SqlProvider = "PostgreSQL";
            options.SqlConnectionString = "Host=localhost;Database=endpoint_tracker;Username=test;Password=test";
            options.RedisKeyPrefix = KeyPrefix;
        });

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RedisEndpointTrackerService>(provider.GetRequiredService<IEndpointTrackerService>());
        Assert.NotNull(provider.GetRequiredService<SqlPersistenceStore>());
    }

    private static (RedisEndpointTrackerService Service, IDatabase Database) CreateService()
    {
        var connection = CreateRedisSubstitute(out var database);
        var options = CreateOptions(connection);

        var service = new RedisEndpointTrackerService(
            connection,
            options,
            NullLogger<RedisEndpointTrackerService>.Instance);

        return (service, database);
    }

    private static EndpointTrackerOptions CreateOptions(IConnectionMultiplexer connection) => new()
    {
        UseRedis = true,
        RedisConnection = connection,
        RedisDatabase = 0,
        RedisKeyPrefix = KeyPrefix
    };

    private static IConnectionMultiplexer CreateRedisSubstitute(out IDatabase database)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.HashGetAll(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<HashEntry>());
        return connection;
    }

    private static void ConfigureRedisHitCount(IDatabase database, long hitCount)
    {
        ConfigureRedisHitCount(database, _ => hitCount);
    }

    private static void ConfigureRedisHitCount(IDatabase database, Func<long> hitCount)
    {
        ConfigureRedisHitCount(database, _ => hitCount());
    }

    private static void ConfigureRedisHitCount(IDatabase database, Func<RedisKey, long> hitCount)
    {
        database.StringGet(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(call => call.ArgAt<RedisKey[]>(0)
                .Select(key => IsHitCountKey(key) ? (RedisValue)hitCount(key) : RedisValue.Null)
                .ToArray());
    }

    private static bool IsHitCountKey(RedisKey key)
    {
        return key.ToString().Contains(":hits:", StringComparison.Ordinal);
    }

    private static void ConfigureFencedScripts(
        IDatabase database,
        Func<RedisKey[], RedisValue[], Task<RedisResult>> flushHandler)
    {
        long fenceToken = 0;
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(call =>
            {
                var keys = call.ArgAt<RedisKey[]>(1);
                var values = call.ArgAt<RedisValue[]>(2);

                if (keys.Length == 2 &&
                    keys[0].ToString().EndsWith("sql-persistence:lock", StringComparison.Ordinal) &&
                    keys[1].ToString().EndsWith("sql-persistence:fence", StringComparison.Ordinal))
                {
                    return Task.FromResult(RedisResult.Create((RedisValue)Interlocked.Increment(ref fenceToken)));
                }

                if (keys.Length >= 4 &&
                    keys[0].ToString().Contains("redis-buffer:batch:", StringComparison.Ordinal))
                {
                    return flushHandler(keys, values);
                }

                if (keys.Length == 1 &&
                    keys[0].ToString().EndsWith("sql-persistence:lock", StringComparison.Ordinal))
                {
                    return Task.FromResult(RedisResult.Create((RedisValue)1L));
                }

                throw new InvalidOperationException($"Unexpected Redis script keys: {string.Join(", ", keys.Select(key => key.ToString()))}");
            });
    }

    private static HashEntry MetadataEntry(string endpointPattern, DateTime registeredUtc)
    {
        return new HashEntry(
            endpointPattern,
            JsonSerializer.Serialize(new EndpointUsageInfo
            {
                EndpointPattern = endpointPattern,
                RegisteredUtc = registeredUtc
            }));
    }

    private static TaskCompletionSource<T> NewCompletionSource<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource NewCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
