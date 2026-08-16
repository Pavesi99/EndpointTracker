# Local Redis setup

Start a low-memory Redis container:

~~~bash
docker run --detach \
  --name endpointtracker-redis \
  --publish 6379:6379 \
  --memory 128m \
  redis:7-alpine

docker exec endpointtracker-redis redis-cli ping
~~~

EndpointTracker supports standalone Redis and Sentinel-managed Redis. Redis Cluster is not supported because durable transfers use atomic scripts across multiple keys; a cluster connection is rejected when the tracker is created.

Configure EndpointTracker:

~~~csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var redis = ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis is not configured."));

builder.Services.AddEndpointTrackerRedis(redis);
~~~

Then add the middleware and metrics routes:

~~~csharp
app.UseEndpointTracker();
app.MapEndpointTrackerMetrics(isAuthRequired: false); // local setup only
~~~

Metrics routes require authorization by default. This local setup disables it only so the sample can run without an authentication scheme.

See [the complete Redis guide](EndpointTracker.AspNetCore/REDIS.md) for configuration, multi-instance behavior, troubleshooting, and SQL persistence.
