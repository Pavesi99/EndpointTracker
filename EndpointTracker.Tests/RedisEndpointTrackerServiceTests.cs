using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace EndpointTracker.Tests;

public class RedisEndpointTrackerServiceTests
{
    [Fact]
    public void RecordHit_IncrementsHitCount()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisDatabase = 0,
            RedisKeyPrefix = "test-endpoint-tracker:"
        };

        var service = new RedisEndpointTrackerService(connection, options, NullLogger<RedisEndpointTrackerService>.Instance);
        service.RegisterEndpoint("GET /api/test", "Get Test", "GET");
        service.RecordHit("GET /api/test");
        service.RecordHit("GET /api/test");

        var usage = service.GetAllEndpointUsage().Single();

        Assert.Equal("GET /api/test", usage.EndpointPattern);
        Assert.Equal(2, usage.HitCount);
    }

    [Fact]
    public void GetUnusedEndpoints_ReturnsEndpointsWithZeroHits()
    {
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisDatabase = 0,
            RedisKeyPrefix = "test-endpoint-tracker:"
        };

        var service = new RedisEndpointTrackerService(connection, options, NullLogger<RedisEndpointTrackerService>.Instance);
        service.RegisterEndpoint("GET /api/test", "Get Test", "GET");

        var unused = service.GetUnusedEndpoints().ToList();

        Assert.Single(unused);
        Assert.Equal("GET /api/test", unused[0].EndpointPattern);
    }
}
