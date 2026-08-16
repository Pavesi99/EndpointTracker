# EndpointTracker

EndpointTracker is ASP.NET Core middleware that discovers mapped endpoints and reports hit counts, last-access times, and unused routes. Storage can be in memory, shared through Redis, or periodically archived from Redis to PostgreSQL or SQL Server.

> Status: prerelease. The SQL persistence work is currently published as an alpha and should be validated against your workload before production use.

## Requirements

- .NET 10 SDK and ASP.NET Core 10
- Redis when using distributed or SQL-backed tracking
- PostgreSQL or SQL Server when SQL persistence is enabled

## Projects

- `EndpointTracker.AspNetCore/` — middleware and NuGet package
- `EndpointTracker.Example/` — runnable minimal API
- `EndpointTracker.Tests/` — automated tests

## Install

While the SQL feature remains prerelease:

~~~bash
dotnet add package EndpointTracker.AspNetCore --prerelease
~~~

## In-memory quick start

~~~csharp
using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointTracker();

var app = builder.Build();

app.UseEndpointTracker();

app.MapGet("/api/users", () => Results.Ok(new[] { "Alice", "Bob" }));
app.MapEndpointTrackerMetrics(isAuthRequired: false); // local quick start only

app.Run();
~~~

Endpoint discovery is automatic after startup. `MapEndpointTrackerMetrics()` requires authorization by default; the quick start disables it only so the sample can run without an authentication scheme.

## Redis

Redis shares metrics across application instances and is required by the optional SQL persistence layer. Standalone and Sentinel-managed Redis deployments are supported. Redis Cluster is rejected because durable transfers use multi-key atomic scripts.

~~~csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var redis = ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis is not configured."));

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisDatabase = 0;
    options.RedisKeyPrefix = "my-api:endpoint-tracker:";
    options.FlushIntervalMs = 1000;
});
~~~

See [the Redis guide](EndpointTracker.AspNetCore/REDIS.md) for configuration and operational notes.

## Optional SQL persistence

SQL persistence is opt-in and works with Redis. On startup, EndpointTracker creates the configured metrics table plus `_Batches` and `_State` companion tables when they do not exist. At the configured interval, it moves accumulated Redis metrics to SQL; reads combine persisted SQL data with current Redis data. Instances sharing a Redis prefix coordinate persistence and reset operations through a renewable Redis lease. The singleton `_State` row provides monotonic fencing, preventing an expired worker from committing after a newer reset or transfer.

~~~csharp
builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.UseSqlPersistence = true;
    options.SqlProvider = "PostgreSQL"; // or "SqlServer"
    options.SqlConnectionString =
        builder.Configuration.GetConnectionString("EndpointTrackerSql");
    options.SqlPersistIntervalMinutes = 10;
    options.SqlTableName = "EndpointTrackerMetrics";
});
~~~

`SqlPersistIntervalMinutes` defaults to 10. SQL persistence cannot be enabled without Redis. SQL-backed endpoint patterns are limited to 450 characters; display names and HTTP method values are truncated to their database limits of 1,024 and 50 characters. When SQL mode starts, preexisting Redis endpoints over the pattern limit are rejected and their unsupported Redis data is cleaned up. Put database credentials in user secrets, environment variables, or a secret manager—not in a committed settings file.

See [the SQL persistence guide](EndpointTracker.AspNetCore/READMEs/SqlPersistenceExample.md) for both providers and local Docker examples.

## Metrics

| Route | Description |
| --- | --- |
| `GET /metrics/endpoints` | Totals and per-endpoint usage |
| `GET /metrics/unused` | Endpoints whose hit count is zero |

You can also inject `IEndpointTrackerService`:

~~~csharp
app.MapGet("/internal/endpoint-coverage", (IEndpointTrackerService tracker) =>
{
    var metrics = tracker.GetMetrics();
    return Results.Ok(metrics);
});
~~~

Protect metrics routes in deployed applications because route names and traffic data can reveal internal behavior.

## Run the example

Start Redis, then run:

~~~bash
docker run --detach --name endpointtracker-redis --publish 6379:6379 --memory 128m redis:7-alpine
dotnet run --project EndpointTracker.Example
~~~

Try:

~~~bash
curl http://localhost:5288/weatherforecast
curl http://localhost:5288/metrics/endpoints
curl http://localhost:5288/metrics/unused
~~~

The actual HTTP port is printed by `dotnet run` and may differ if the launch profile changes.

## Build and test

~~~bash
dotnet restore EndpointTracker.sln
dotnet build EndpointTracker.sln --configuration Release --no-restore
dotnet test EndpointTracker.Tests/EndpointTracker.Tests.csproj --configuration Release --no-build --no-restore
~~~

## License

MIT. Please use GitHub issues for bugs and feature requests.
