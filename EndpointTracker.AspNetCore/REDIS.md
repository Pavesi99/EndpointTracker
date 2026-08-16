# Redis support

Redis mode stores endpoint metadata and counters outside the application process, so multiple instances can report shared metrics. Redis is also the live-data layer used by optional SQL persistence.

> EndpointTracker's SQL persistence feature is currently prerelease. Test failure and recovery behavior in a staging environment before production use.

## Requirements

- .NET 10
- A reachable standalone or Sentinel-managed Redis server
- `EndpointTracker.AspNetCore`; its package dependencies include StackExchange.Redis

Redis Cluster is not supported. EndpointTracker rejects cluster connections during tracker construction because its durable flush and SQL-transfer protocol uses atomic scripts across multiple Redis keys. Redis Sentinel is supported when StackExchange.Redis resolves the writable primary through the Sentinel deployment.

## Local Redis

The following container is intentionally small for development:

~~~bash
docker run --detach \
  --name endpointtracker-redis \
  --publish 6379:6379 \
  --memory 128m \
  redis:7-alpine

docker exec endpointtracker-redis redis-cli ping
~~~

The ping should return `PONG`.

## Configure Redis

~~~csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisConnectionString =
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis must be configured.");

var redis = ConnectionMultiplexer.Connect(redisConnectionString);

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisDatabase = 0;
    options.RedisKeyPrefix = "my-api:endpoint-tracker:";
    options.FlushIntervalMs = 1000;
});

var app = builder.Build();
app.UseEndpointTracker();
app.MapGet("/api/users", () => Results.Ok());
app.MapEndpointTrackerMetrics(isAuthRequired: false); // local demo only
app.Run();
~~~

Endpoint registration is automatic. In deployed applications, omit `isAuthRequired: false` and configure authorization for the metrics routes.

## Configuration from settings

`appsettings.json` can contain non-secret Redis settings:

~~~json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "EndpointTracker": {
    "RedisDatabase": 0,
    "RedisKeyPrefix": "my-api:endpoint-tracker:",
    "FlushIntervalMs": 1000
  }
}
~~~

~~~csharp
var section = builder.Configuration.GetSection("EndpointTracker");
var redis = ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis is not configured."));

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisDatabase = section.GetValue("RedisDatabase", 0);
    options.RedisKeyPrefix =
        section["RedisKeyPrefix"] ?? "endpoint-tracker:";
    options.FlushIntervalMs = section.GetValue("FlushIntervalMs", 1000);
});
~~~

For hosted Redis, keep credentials out of committed settings. ASP.NET Core maps `ConnectionStrings__Redis` to `ConnectionStrings:Redis`:

~~~bash
export ConnectionStrings__Redis='redis.example.com:6380,password=replace-me,ssl=true'
~~~

## Options

| Option | Default | Description |
| --- | --- | --- |
| `RedisDatabase` | `0` | Redis logical database |
| `RedisKeyPrefix` | `endpoint-tracker:` | Prefix applied to all tracker data |
| `FlushIntervalMs` | `1000` | Buffer flush interval; must be at least 100 ms |

Use a unique prefix for each application and environment. Sharing both the Redis database and prefix intentionally combines their metrics.

## How data flows

1. Middleware records a hit in the process-local buffer.
2. The Redis hosted service periodically moves buffered increments to Redis.
3. Metrics reads combine buffered hits with values already stored in Redis.
4. During application shutdown, the hosted service attempts a final flush.

The request path does not synchronously write each hit to Redis. A process can still lose data during an abrupt termination, so EndpointTracker metrics should not be treated as an audit log.

## Key layout

With the default prefix, Redis data is namespaced beneath `endpoint-tracker:`. The implementation maintains endpoint metadata, hit counters, and last-access values. Treat the exact internal key names as implementation details: use `IEndpointTrackerService` or the metrics endpoints instead of reading or modifying keys directly.

## Multiple application instances

Configure every instance with the same Redis endpoint, database, and prefix to aggregate metrics:

~~~csharp
builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisDatabase = 0;
    options.RedisKeyPrefix = "production:orders-api:endpoint-tracker:";
});
~~~

Use different prefixes when metrics must remain isolated. Monitor connection failures and Redis memory just as you would for any shared application dependency.

When SQL persistence is enabled, every application instance can run the persistence worker, but only one instance at a time transfers data for a shared prefix. EndpointTracker acquires and renews a Redis lease around snapshot creation, SQL persistence, and Redis cleanup. `IEndpointTrackerService.Reset()` uses the same lease so it cannot race an active transfer. SQL stores the lease's monotonic fence token in a singleton `_State` row; after a newer reset or transfer advances that value, a stale worker whose lease expired cannot commit. If renewal fails, the active operation is cancelled and its pending batch remains available for a later lease holder to retry.

## Add SQL persistence

SQL persistence is optional and must be enabled on Redis mode:

~~~csharp
builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisKeyPrefix = "my-api:endpoint-tracker:";
    options.UseSqlPersistence = true;
    options.SqlProvider = "PostgreSQL"; // or "SqlServer"
    options.SqlConnectionString =
        builder.Configuration.GetConnectionString("EndpointTrackerSql");
    options.SqlPersistIntervalMinutes = 10;
});
~~~

EndpointTracker creates the SQL metrics table plus `_Batches` and `_State` companion tables on startup, periodically transfers Redis metrics, and combines SQL history with current Redis values when metrics are read. See [SQL persistence](READMEs/SqlPersistenceExample.md).

SQL-backed endpoint patterns have a maximum length of 450 characters. A longer pattern is logged and is not tracked. When SQL mode loads preexisting Redis data, overlong patterns and their active metadata, hit, and last-access keys are rejected and cleaned up. Display names and HTTP method values are truncated to the SQL limits of 1,024 and 50 characters.

## Test manually

~~~bash
dotnet run --project EndpointTracker.Example

curl http://localhost:5288/weatherforecast
curl http://localhost:5288/weatherforecast
curl http://localhost:5288/metrics/endpoints
~~~

Use the port printed by `dotnet run`. To inspect the local Redis container:

~~~bash
docker exec endpointtracker-redis redis-cli --scan --pattern 'endpoint-tracker:*'
~~~

## Troubleshooting

### The application cannot connect

- Confirm `docker exec endpointtracker-redis redis-cli ping` returns `PONG`.
- Confirm the host and port are reachable from the application.
- If the application also runs in Docker, `localhost` refers to that application container; use the Redis service/container name.
- Include required password, TLS, and timeout settings in the StackExchange.Redis connection string.
- If the connection resolves to Redis Cluster, switch to standalone Redis or a Sentinel-managed primary; cluster mode is deliberately rejected.

### Metrics do not update

- Generate traffic against a mapped endpoint and wait at least one flush interval.
- Check logs from `RedisEndpointTrackerService` and `RedisFlushHostedService`.
- Confirm all instances use the intended database and key prefix.
- Confirm the metrics route is authorized; the default is to require authorization.

### Redis contains old data

Redis mode intentionally survives application restarts. Call `IEndpointTrackerService.Reset()` only when you explicitly want to erase tracked metrics. With SQL persistence enabled, reset semantics also include persisted metrics.

### Redis runs out of memory

Inspect `INFO memory` and size Redis for the number of routes and retention expected. Choose an eviction and persistence policy deliberately; automatic eviction can make metric totals incomplete.

## Deployment checklist

- Protect `/metrics` with authorization and network controls.
- Store connection credentials in a secret manager.
- Give applications and environments unique key prefixes.
- Use standalone or Sentinel-managed Redis, not Redis Cluster.
- Decide whether Redis AOF/RDB durability is required.
- Alert on flush, connection, and SQL-transfer errors.
- Load-test using realistic route cardinality and traffic.
- Test application shutdown, Redis interruption, and recovery.

## Switch back to in-memory

Replace Redis registration:

~~~csharp
builder.Services.AddEndpointTrackerRedis(redis);
~~~

with:

~~~csharp
builder.Services.AddEndpointTracker();
~~~

In-memory metrics belong to one process and are lost when that process stops.
