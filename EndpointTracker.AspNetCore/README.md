# EndpointTracker.AspNetCore

[![NuGet](https://img.shields.io/nuget/v/EndpointTracker.AspNetCore.svg)](https://www.nuget.org/packages/EndpointTracker.AspNetCore/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

EndpointTracker discovers ASP.NET Core endpoints and tracks hit counts, last-access times, and unused routes. It supports in-memory storage, Redis for shared metrics, and optional persistence from Redis to PostgreSQL or SQL Server.

> The SQL persistence release is currently alpha. Validate it in a staging environment before relying on it for production telemetry.

## Requirements

- .NET 10
- Redis for Redis mode and SQL persistence
- PostgreSQL or SQL Server only when SQL persistence is enabled

## Installation

~~~bash
dotnet add package EndpointTracker.AspNetCore --prerelease
~~~

The prerelease switch is needed while the SQL support is published as an alpha.

## In-memory setup

~~~csharp
using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointTracker();

var app = builder.Build();

app.UseEndpointTracker();

app.MapGet("/api/users", () => Results.Ok(new[] { "Alice", "Bob" }))
   .WithName("GetUsers");

app.MapEndpointTrackerMetrics(isAuthRequired: false); // local quick start only
app.Run();
~~~

Endpoint discovery runs automatically after application startup. The manual `UseEndpointTrackerRegistration()` extension remains available, but normal applications do not need to call it.

`MapEndpointTrackerMetrics()` protects the metrics routes with authorization by default. The quick start disables it only so the sample can run without an authentication scheme. In a deployed application, configure authentication and use the default.

## Redis setup

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

Redis mode buffers request hits in the application and flushes them periodically. Reads include both the current buffer and Redis values. Standalone and Sentinel-managed Redis deployments are supported. Redis Cluster is rejected during tracker construction because durable transfers depend on multi-key atomic scripts.

For configuration, key layout, and operating guidance, see the [Redis guide](https://github.com/Pavesi99/EndpointTracker/blob/master/EndpointTracker.AspNetCore/REDIS.md).

## Redis with optional SQL persistence

SQL persistence is disabled by default and is supported only with Redis mode:

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

At startup, the package validates the SQL configuration and creates the metrics table plus `_Batches` and `_State` companion tables if needed. On each persistence cycle it transfers accumulated metrics from Redis to SQL. Metrics reads combine SQL history with current Redis data. The interval defaults to 10 minutes. Across instances that share a Redis prefix, a renewable Redis lease serializes SQL persistence and reset operations; losing the lease cancels the active transfer so another instance can safely continue. The singleton `_State` row stores the latest monotonic fence token, preventing an expired or stale worker from committing after a newer reset or transfer.

SQL persistence accepts endpoint patterns up to 450 characters. Longer patterns are logged and are not tracked. When SQL mode starts, preexisting overlong endpoint patterns are removed from active Redis metadata, hit, and last-access data so they cannot block later batches. Display names longer than 1,024 characters and HTTP method values longer than 50 characters are truncated before persistence.

Supported provider values:

| Provider | `SqlProvider` value |
| --- | --- |
| PostgreSQL | `PostgreSQL` or `Postgres` |
| SQL Server | `SqlServer` |

Store the connection string outside source control:

~~~bash
# PostgreSQL example
export ConnectionStrings__EndpointTrackerSql='Host=localhost;Port=5432;Database=endpointtracker;Username=endpointtracker;Password=change-me'

# SQL Server example
export ConnectionStrings__EndpointTrackerSql='Server=localhost,1433;Database=EndpointTracker;User Id=sa;Password=change-me;Encrypt=True;TrustServerCertificate=True'
~~~

For local containers and complete setup, see the [SQL persistence guide](https://github.com/Pavesi99/EndpointTracker/blob/master/EndpointTracker.AspNetCore/READMEs/SqlPersistenceExample.md).

## Configuration reference

| Option | Default | Notes |
| --- | --- | --- |
| `UseRedis` | `false` | Selects Redis-backed tracking |
| `RedisConnection` | none | Required when `UseRedis` is true |
| `RedisDatabase` | `0` | Redis logical database |
| `RedisKeyPrefix` | `endpoint-tracker:` | Isolate applications and environments |
| `FlushIntervalMs` | `1000` | Redis buffer flush interval; minimum 100 ms |
| `UseSqlPersistence` | `false` | Enables periodic Redis-to-SQL transfer |
| `SqlProvider` | none | `PostgreSQL`/`Postgres` or `SqlServer` |
| `SqlConnectionString` | none | Required when SQL persistence is enabled |
| `SqlPersistIntervalMinutes` | `10` | SQL transfer interval; minimum 1 minute |
| `SqlTableName` | `EndpointTrackerMetrics` | Base name for the metrics table; `_Batches` and `_State` companion tables are also managed |

## Metrics API

### `GET /metrics/endpoints`

Returns totals plus an `endpoints` collection:

~~~json
{
  "totalEndpoints": 2,
  "usedEndpoints": 1,
  "unusedEndpoints": 1,
  "totalRequests": 3,
  "endpoints": [
    {
      "endpointPattern": "GET /api/users",
      "displayName": "GetUsers",
      "httpMethod": "GET",
      "hitCount": 3,
      "lastAccessedUtc": "2026-08-15T12:00:00Z",
      "registeredUtc": "2026-08-15T11:00:00Z"
    }
  ]
}
~~~

### `GET /metrics/unused`

Returns endpoints whose combined hit count is zero.

## Programmatic access

~~~csharp
using EndpointTracker.AspNetCore.Services;

app.MapGet("/internal/endpoint-metrics", (IEndpointTrackerService tracker) =>
    Results.Ok(tracker.GetMetrics()));
~~~

`IEndpointTrackerService` also exposes endpoint registration, hit recording, usage queries, and reset.

## Middleware placement

`UseEndpointTracker()` must run after routing has selected an endpoint and before the endpoint handler completes. In minimal APIs, place it before mapped handlers are executed as shown above.

## Deployment checklist

- Require authentication and authorization for metrics routes.
- Use different Redis key prefixes for different applications and environments.
- Use standalone or Sentinel-managed Redis; Redis Cluster is not supported.
- Store Redis and SQL credentials in a secret store.
- Enable appropriate Redis durability if Redis-only history must survive restarts.
- Monitor Redis and database connectivity and EndpointTracker error logs.
- Validate schema permissions: the SQL login needs permission to create and access all three persistence tables.
- Exercise persistence, shutdown, and recovery behavior under realistic load before production rollout.

## License

MIT. Issues and contributions are welcome in the [GitHub repository](https://github.com/Pavesi99/EndpointTracker).
