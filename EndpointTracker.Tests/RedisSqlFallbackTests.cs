using System.Text.Json;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EndpointTracker.Tests;

public class RedisSqlFallbackTests
{
    [Fact(Timeout = 10_000)]
    public void GetAllEndpointUsage_WithSqlStore_FiltersOversizedLegacyPendingRowsWithoutDroppingValidRows()
    {
        const string validEndpoint = "GET /valid-pending";
        var oversizedEndpoint = new string('p', 451);
        const string batchId = "mixed-legacy-batch";
        const string keyPrefix = "mixed-legacy-test:";
        var registeredUtc = new DateTime(2026, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.HashGetAll(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns([]);
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns([new RedisValue(batchId)]);
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => MixedPendingBatchEntries(
                call.ArgAt<RedisKey>(0),
                keyPrefix,
                batchId,
                validEndpoint,
                oversizedEndpoint,
                registeredUtc));
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = keyPrefix,
            UseSqlPersistence = true,
            SqlProvider = "PostgreSQL",
            SqlConnectionString =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Command Timeout=1"
        };
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var service = new RedisEndpointTrackerService(
            connection,
            options,
            store,
            NullLogger<RedisEndpointTrackerService>.Instance);

        var usage = Assert.Single(service.GetAllEndpointUsage());

        Assert.Equal(validEndpoint, usage.EndpointPattern);
        Assert.Equal(3L, usage.HitCount);
        Assert.DoesNotContain(oversizedEndpoint, service.GetAllEndpointUsage().Select(item => item.EndpointPattern));
    }

    [Fact]
    public void GetAllEndpointUsage_WithoutSqlStore_IncludesPendingSqlBatchExactlyOnce()
    {
        const string endpointPattern = "GET /pending-without-sql";
        const string batchId = "pending-without-sql-batch-1";
        const string keyPrefix = "sql-disabled-fallback-test:";
        var registeredUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var lastAccessedUtc = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var connection = CreatePendingBatchRedis(
            keyPrefix,
            batchId,
            endpointPattern,
            registeredUtc,
            lastAccessedUtc);
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = keyPrefix,
            UseSqlPersistence = false
        };
        var service = new RedisEndpointTrackerService(
            connection,
            options,
            NullLogger<RedisEndpointTrackerService>.Instance);

        var usage = Assert.Single(service.GetAllEndpointUsage());

        Assert.Equal(endpointPattern, usage.EndpointPattern);
        Assert.Equal(5L, usage.HitCount);
        Assert.Equal(lastAccessedUtc, usage.LastAccessedUtc);
        Assert.Equal(registeredUtc, usage.RegisteredUtc);
        Assert.Equal(5L, service.GetMetrics().TotalRequests);
    }

    [Fact(Timeout = 10_000)]
    public void GetAllEndpointUsage_WhenSqlIsUnavailable_IncludesPendingRedisBatchExactlyOnce()
    {
        const string endpointPattern = "GET /pending";
        const string batchId = "pending-batch-1";
        const string keyPrefix = "sql-fallback-test:";
        var registeredUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var lastAccessedUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var connection = CreatePendingBatchRedis(
            keyPrefix,
            batchId,
            endpointPattern,
            registeredUtc,
            lastAccessedUtc);

        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = keyPrefix,
            UseSqlPersistence = true,
            SqlProvider = "PostgreSQL",
            SqlConnectionString =
                "Host=127.0.0.1;Port=1;Database=unavailable;Username=test;Password=test;Timeout=1;Command Timeout=1"
        };
        var sqlStore = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var service = new RedisEndpointTrackerService(
            connection,
            options,
            sqlStore,
            NullLogger<RedisEndpointTrackerService>.Instance);

        var usage = Assert.Single(service.GetAllEndpointUsage());

        Assert.Equal(endpointPattern, usage.EndpointPattern);
        Assert.Equal(5L, usage.HitCount);
        Assert.Equal(lastAccessedUtc, usage.LastAccessedUtc);
        Assert.Equal(registeredUtc, usage.RegisteredUtc);
        Assert.Equal(5L, service.GetMetrics().TotalRequests);
    }

    private static IConnectionMultiplexer CreatePendingBatchRedis(
        string keyPrefix,
        string batchId,
        string endpointPattern,
        DateTime registeredUtc,
        DateTime lastAccessedUtc)
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        database.HashGetAll(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Array.Empty<HashEntry>());
        database.SetMembersAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns([new RedisValue(batchId)]);
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(call => PendingBatchEntries(
                call.ArgAt<RedisKey>(0),
                keyPrefix,
                batchId,
                endpointPattern,
                registeredUtc,
                lastAccessedUtc));
        return connection;
    }

    private static HashEntry[] PendingBatchEntries(
        RedisKey key,
        string keyPrefix,
        string batchId,
        string endpointPattern,
        DateTime registeredUtc,
        DateTime lastAccessedUtc)
    {
        var keyText = key.ToString();
        var batchKeyPrefix = $"{keyPrefix}sql-persistence:batch:{batchId}:";

        if (keyText == batchKeyPrefix + "metadata")
        {
            var metadata = new EndpointUsageInfo
            {
                EndpointPattern = endpointPattern,
                DisplayName = "Pending endpoint",
                HttpMethod = "GET",
                RegisteredUtc = registeredUtc
            };
            return [new HashEntry(endpointPattern, JsonSerializer.Serialize(metadata))];
        }

        if (keyText == batchKeyPrefix + "hits")
            return [new HashEntry(endpointPattern, 5L)];

        if (keyText == batchKeyPrefix + "last-accessed")
            return [new HashEntry(endpointPattern, lastAccessedUtc.Ticks)];

        return [];
    }

    private static HashEntry[] MixedPendingBatchEntries(
        RedisKey key,
        string keyPrefix,
        string batchId,
        string validEndpoint,
        string oversizedEndpoint,
        DateTime registeredUtc)
    {
        var keyText = key.ToString();
        var batchKeyPrefix = $"{keyPrefix}sql-persistence:batch:{batchId}:";

        if (keyText == batchKeyPrefix + "metadata")
        {
            return
            [
                MetadataEntry(validEndpoint, registeredUtc),
                MetadataEntry(oversizedEndpoint, registeredUtc)
            ];
        }

        if (keyText == batchKeyPrefix + "hits")
            return [new HashEntry(validEndpoint, 3L), new HashEntry(oversizedEndpoint, 99L)];

        return [];
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
}
