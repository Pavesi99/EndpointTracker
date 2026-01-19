# Redis Support for EndpointTracker

Complete guide for using Redis-backed metrics storage for distributed endpoint tracking.

## ?? Quick Start (5 minutes)

### 1. Start Redis
```bash
docker run -d -p 6379:6379 redis
```

### 2. Install Package
```bash
dotnet add package StackExchange.Redis
```

### 3. Update Program.cs
```csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redis = ConnectionMultiplexer.Connect("localhost:6379");
builder.Services.AddEndpointTrackerRedis(redis);

var app = builder.Build();
app.UseEndpointTracker();
app.MapEndpointTrackerMetrics();
app.UseEndpointTrackerRegistration();
app.Run();
```

### 4. Verify
```bash
curl http://localhost:5000/metrics/endpoints
```

---

## Table of Contents

- [Overview](#overview)
- [Why Redis?](#why-redis)
- [Installation](#installation)
- [Configuration](#configuration)
- [How It Works](#how-it-works)
- [Redis Schema](#redis-schema)
- [Error Handling](#error-handling)
- [Multi-Instance Setup](#multi-instance-setup)
- [Tuning & Performance](#tuning--performance)
- [Troubleshooting](#troubleshooting)
- [Production Checklist](#production-checklist)

---

## Overview

The Redis implementation provides:

- **Distributed Metrics** - Share endpoint metrics across multiple application instances
- **Persistence** - Metrics survive application restarts
- **High Performance** - In-memory buffering with periodic batched Redis writes
- **Graceful Degradation** - Failures don't block request processing
- **Flexible Configuration** - Multiple ways to configure Redis
- **Zero Setup** - Works with sensible defaults

### Architecture

```
Request
  ?
Middleware records hit
  ?
In-Memory Buffer (ConcurrentDictionary) - instant, non-blocking
  ?
Response sent to client
  
Meanwhile (background):
  ?
Timer fires every 1000ms
  ?
Batch flush to Redis
  ?
Redis persistent store
```

---

## Why Redis?

### When to Use

| Use Case | Solution |
|----------|----------|
| **Single instance, small deployment** | In-Memory (default) |
| **Multi-instance application** | ? Redis |
| **Metrics must survive restarts** | ? Redis |
| **Shared metrics across instances** | ? Redis |
| **High-traffic application** | ? Redis (distributed load) |
| **Development/testing** | In-Memory or Redis |

### Comparison Table

| Feature | In-Memory | Redis |
|---------|-----------|-------|
| **Storage** | App memory | Redis server |
| **Persistence** | ? Lost on restart | ? Survives restart |
| **Multi-instance** | ? Separate metrics | ? Shared metrics |
| **Dependencies** | ? None | ? Redis required |
| **Latency** | Minimal | Very low (batched) |
| **Scalability** | Limited | Unlimited |
| **Cost** | Free | Hosting cost |

---

## Installation

### Prerequisites

- Redis server (local or remote)
- .NET 8.0 or higher
- StackExchange.Redis NuGet package

### Option 1: Docker (Easiest)

```bash
# Start Redis in Docker
docker run -d -p 6379:6379 --name redis redis:latest

# Verify it's running
redis-cli ping  # Should return PONG
```

### Option 2: Local Installation

**Windows (with WSL2):**
```bash
wsl
sudo apt-get install redis-server
redis-server
```

**macOS:**
```bash
brew install redis
redis-server
```

**Linux:**
```bash
sudo apt-get install redis-server
redis-server
```

### Install NuGet Packages

```bash
dotnet add package EndpointTracker.AspNetCore
dotnet add package StackExchange.Redis
```

---

## Configuration

### Method 1: Simple (Recommended) ?

```csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

var redisConnection = ConnectionMultiplexer.Connect("localhost:6379");
builder.Services.AddEndpointTrackerRedis(redisConnection);

var app = builder.Build();
app.UseEndpointTracker();
app.MapEndpointTrackerMetrics();
app.UseEndpointTrackerRegistration();
app.Run();
```

### Method 2: With Custom Options

```csharp
builder.Services.AddEndpointTrackerRedis(redisConnection, options =>
{
    options.FlushIntervalMs = 1000;        // Flush every 1 second
    options.RedisDatabase = 0;              // Use Redis DB 0
    options.RedisKeyPrefix = "my-app:";     // Custom prefix
});
```

### Method 3: Using Options Configuration

```csharp
using EndpointTracker.AspNetCore.Options;

builder.Services.AddEndpointTracker(options =>
{
    options.UseRedis = true;
    options.RedisConnection = ConnectionMultiplexer.Connect("localhost:6379");
    options.FlushIntervalMs = 1000;
    options.RedisDatabase = 0;
    options.RedisKeyPrefix = "my-app:";
});
```

### Method 4: From appsettings.json

**appsettings.json:**
```json
{
  "Redis": {
    "Connection": "localhost:6379",
    "Database": 0,
    "FlushIntervalMs": 1000,
    "KeyPrefix": "my-app:"
  }
}
```

**Program.cs:**
```csharp
var redisConfig = builder.Configuration.GetSection("Redis");
var connection = ConnectionMultiplexer.Connect(
    redisConfig["Connection"] ?? "localhost:6379"
);

builder.Services.AddEndpointTrackerRedis(connection, options =>
{
    options.FlushIntervalMs = int.Parse(redisConfig["FlushIntervalMs"] ?? "1000");
    options.RedisDatabase = int.Parse(redisConfig["Database"] ?? "0");
    options.RedisKeyPrefix = redisConfig["KeyPrefix"] ?? "endpoint-tracker:";
});
```

### Method 5: Conditional In-Memory or Redis

```csharp
if (app.Environment.IsProduction())
{
    var redis = ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379"
    );
    builder.Services.AddEndpointTrackerRedis(redis);
}
else
{
    builder.Services.AddEndpointTracker();  // Use in-memory for dev
}
```

### Configuration Options Reference

```csharp
public class EndpointTrackerOptions
{
    /// <summary>Enable Redis storage (default: false)</summary>
    public bool UseRedis { get; set; } = false;

    /// <summary>Redis connection multiplexer (required if UseRedis = true)</summary>
    public IConnectionMultiplexer? RedisConnection { get; set; }

    /// <summary>Redis database number 0-15 (default: 0)</summary>
    public int RedisDatabase { get; set; } = 0;

    /// <summary>How often to flush buffer to Redis in ms (default: 1000, minimum: 100)</summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>Prefix for all Redis keys (default: "endpoint-tracker:")</summary>
    public string RedisKeyPrefix { get; set; } = "endpoint-tracker:";
}
```

---

## How It Works

### Request Flow

```
1. User makes HTTP request
   ?
2. Middleware intercepts and records endpoint hit
   ?
3. Hit added to in-memory buffer (ConcurrentDictionary) - INSTANT ?
   ?
4. Response sent to client (no Redis latency)
   ?
5. Meanwhile, background timer runs every 1000ms
   ?
6. All buffered hits batched into single Redis operation
   ?
7. Buffer cleared, ready for next cycle
```

### Data Consistency

- **Recording**: Non-blocking, in-memory only
- **Reading Metrics**: Merges in-memory buffer + Redis (always up-to-date)
- **Flushing**: Periodic batched operations
- **Shutdown**: Final flush prevents data loss

### Example Timeline

```
10:00:00.000 - Request to GET /api/users ? Buffer: {users: 1}
10:00:00.100 - Request to GET /api/users ? Buffer: {users: 2}
10:00:00.200 - Request to POST /api/users ? Buffer: {users: 2, create: 1}
...
10:00:01.000 - FLUSH TIMER FIRES
             - INCR hits:GET /api/users 2
             - INCR hits:POST /api/users 1
             - Update last-accessed timestamps
             - Buffer cleared ?
...
10:00:02.000 - GET /metrics/endpoints
             - Reads Redis: hits: {users: 2, create: 1}
             - Merges buffer: (empty at this moment)
             - Returns up-to-date metrics ?
```

---

## Redis Schema

### Key Structure

```
{prefix}endpoints:metadata          Hash    Endpoint registration data
{prefix}hits:{pattern}              String  Hit count for each endpoint
{prefix}last-accessed:{pattern}     String  Last access timestamp (ticks)
```

### Example Data

```redis
# Register an endpoint
HSET endpoint-tracker:endpoints:metadata "GET /api/users" \
  '{"endpointPattern":"GET /api/users","displayName":"GetUsers",...}'

# Record hits
INCR endpoint-tracker:hits:GET\ /api/users
INCR endpoint-tracker:hits:GET\ /api/users
INCR endpoint-tracker:hits:GET\ /api/users

# Update last access time
SET endpoint-tracker:last-accessed:GET\ /api/users 133787654321234567

# Reading metrics
HGETALL endpoint-tracker:endpoints:metadata
GET endpoint-tracker:hits:GET\ /api/users        # Returns 3
GET endpoint-tracker:last-accessed:GET\ /api/users
```

### Custom Key Prefix

```csharp
options.RedisKeyPrefix = "production:";
// Keys become: production:endpoints:metadata, production:hits:GET\ /api/users, etc.
```

---

## Error Handling

### Graceful Degradation

If Redis is unavailable:

1. **Recording Hits** - Continues buffering in memory ?
2. **Responses** - Sent immediately (no blocked requests) ?
3. **Flushing** - Logs error, retries on next interval ?
4. **Metrics** - Returns data from in-memory buffer ?
5. **Request Processing** - Zero impact ?

### Example Error Scenario

```
10:00:00 - Everything working normally
10:00:30 - Redis goes down (network issue)
10:01:00 - Timer tries to flush ? ERROR logged
         - But hits continue buffering in memory
10:02:00 - Timer tries again ? Still can't connect
         - Continues logging, buffer still accumulating
10:03:00 - Redis comes back up
10:03:00 - Timer tries to flush ? SUCCESS ?
         - All accumulated buffer flushed to Redis
```

### Error Logs

```
warn: EndpointTracker.AspNetCore.Services.RedisEndpointTrackerService[0]
      Failed to flush hit buffer to Redis. Will retry on next interval.
      StackExchange.Redis.RedisConnectionException: Error connecting to endpoint ...
```

### Monitoring for Errors

```csharp
// Configure structured logging to monitor Redis errors
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// In your log monitoring, watch for:
// - Category: "EndpointTracker.AspNetCore.Services.RedisEndpointTrackerService"
// - Level: "Error"
// - Message: "Failed to flush hit buffer to Redis"
```

---

## Multi-Instance Setup

### Architecture

```
Load Balancer
    ?      ?      ?
Instance 1  Instance 2  Instance 3
    ?       ?       ?
    ?????????????????
      (shared)
      Redis Server
```

### Configuration (Same for All Instances)

```csharp
// All instances point to same Redis
var redis = ConnectionMultiplexer.Connect("redis.example.com:6379");

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.FlushIntervalMs = 1000;
    options.RedisKeyPrefix = "prod:";  // Can be different per environment
});
```

### Result

```
GET /api/users (Instance 1) ? Buffer on Instance 1 ? Flush to Redis
GET /api/users (Instance 2) ? Buffer on Instance 2 ? Flush to Redis
GET /api/users (Instance 3) ? Buffer on Instance 3 ? Flush to Redis

GET /metrics/endpoints (on any instance)
? Redis returns: GET /api/users = 3 hits (combined from all instances) ?
```

### Environment-Specific Key Prefixes

```csharp
var environment = app.Environment.EnvironmentName;
var keyPrefix = $"{environment}:endpoint-tracker:";

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisKeyPrefix = keyPrefix;
});

// Development:  dev:endpoint-tracker:...
// Staging:      staging:endpoint-tracker:...
// Production:   prod:endpoint-tracker:...
```

---

## Tuning & Performance

### Request Recording Performance

| Operation | Time | Blocking |
|-----------|------|----------|
| RecordHit() | < 1?s | No |
| Buffer add | < 1?s | No |
| Network | Not used | - |

### Flush Performance

| Scenario | Time | Operations |
|----------|------|-----------|
| 1000 hits | < 10ms | INCR x1000 + SET x1000 (batched) |
| 10000 hits | < 50ms | Batched Redis operations |

### Metrics Query Performance

| Query | Time | Operations |
|-------|------|-----------|
| GetAllEndpointUsage() | < 100ms | HGETALL + GETS |
| GetMetrics() | < 100ms | Above + aggregation |

### Tuning Recommendations

**High-Traffic Applications (10k+ req/sec)**
```csharp
options.FlushIntervalMs = 500;  // More frequent flushes
// Trade-off: More Redis load, more up-to-date metrics
```

**Normal Traffic (1k-10k req/sec)**
```csharp
options.FlushIntervalMs = 1000;  // Default (good balance)
```

**Low-Traffic Applications (< 1k req/sec)**
```csharp
options.FlushIntervalMs = 5000;  // Less frequent flushes
// Trade-off: Less Redis load, slightly stale metrics between flushes
```

**Very High Traffic with Memory Constraints**
```csharp
options.FlushIntervalMs = 100;   // Aggressive flushing
// Reduces buffer size at the cost of more Redis operations
```

---

## Troubleshooting

### Redis Connection Issues

**Problem**: `Error connecting to Redis at localhost:6379`

**Solutions**:
```bash
# Check if Redis is running
redis-cli ping  # Should return PONG

# Verify port is correct
netstat -an | findstr 6379  # Windows
lsof -i :6379               # macOS/Linux

# Test connection manually
redis-cli -h localhost -p 6379
```

**Problem**: `Connection timeout`

**Solutions**:
- Verify firewall allows port 6379
- Check Redis configuration: `redis-cli CONFIG GET bind`
- Ensure Redis is listening on all interfaces: `bind 0.0.0.0`

**Problem**: `Authentication failed`

**Solution**: Include password in connection string:
```csharp
var redis = ConnectionMultiplexer.Connect("localhost:6379,password=mypassword");
```

### Metrics Not Updating

**Problem**: Metrics appear frozen

**Solutions**:
1. Check flush interval: `options.FlushIntervalMs`
2. Verify Redis connectivity: `redis-cli ping`
3. Check logs for flush errors
4. Restart application

**Problem**: Buffer not flushing

**Solutions**:
```csharp
// Ensure RedisFlushHostedService is registered
// It should be automatic when using AddEndpointTrackerRedis()

// Verify in logs:
// "RedisFlushHostedService started with flush interval of 1000ms"
```

### High Redis Memory Usage

**Problem**: Redis memory continuously growing

**Solutions**:
```csharp
// 1. Check flush interval (too frequent = more keys)
options.FlushIntervalMs = 1000;

// 2. Call Reset() periodically if needed
tracker.Reset();  // Clears all Redis data

// 3. Configure Redis eviction policy
redis-cli CONFIG SET maxmemory-policy allkeys-lru

// 4. Monitor with redis-cli
redis-cli INFO memory
```

### Metrics Include Old Data

**Problem**: After restart, old metrics appear

**Expected**: This is correct behavior - Redis persists data

**Solution**: If you don't want persistence:
```csharp
// Option 1: Use in-memory for dev
builder.Services.AddEndpointTracker();

// Option 2: Clear on startup
using (var scope = app.Services.CreateScope())
{
    var tracker = scope.ServiceProvider.GetRequiredService<IEndpointTrackerService>();
    tracker.Reset();
}
```

### Common Error Messages

| Error | Cause | Solution |
|-------|-------|----------|
| "Failed to flush hit buffer to Redis" | Redis unavailable | Check Redis is running |
| "Connection timeout" | Network issue | Verify connectivity |
| "MOVED redirect" | Redis Cluster | Use appropriate client settings |
| "OOM command not allowed" | Redis out of memory | Increase max memory or evict keys |

---

## Testing

### Manual Testing

```bash
# Start Redis
docker run -d -p 6379:6379 redis

# In another terminal, monitor Redis
redis-cli MONITOR

# Run your application
dotnet run

# In another terminal, test the API
curl http://localhost:5000/api/users
curl http://localhost:5000/api/users
curl http://localhost:5000/metrics/endpoints

# Watch redis-cli for key operations
# Should see: INCR, SET operations
```

### Unit Testing Example

```csharp
[Fact]
public void TestRedisFlush()
{
    var options = new EndpointTrackerOptions
    {
        UseRedis = true,
        FlushIntervalMs = 1000,
        RedisDatabase = 15  // Use separate DB for tests
    };
    
    var redis = ConnectionMultiplexer.Connect("localhost:6379");
    var service = new RedisEndpointTrackerService(redis, options, logger);
    
    // Register endpoint
    service.RegisterEndpoint("GET /test", "Test", "GET");
    
    // Record hits
    service.RecordHit("GET /test");
    service.RecordHit("GET /test");
    
    // Flush buffer
    service.FlushHitBuffer();
    
    // Verify
    var metrics = service.GetMetrics();
    Assert.Single(metrics.Endpoints);
    Assert.Equal(2, metrics.Endpoints.First().HitCount);
}
```

---

## Production Checklist

### Infrastructure
- [ ] Redis server deployed and accessible
- [ ] Redis authentication configured
- [ ] Redis persistence enabled (AOF or RDB)
- [ ] Redis replication/clustering configured for HA
- [ ] Redis server monitored for health
- [ ] Redis disk space monitored
- [ ] Redis memory limits configured

### Configuration
- [ ] Flush interval tuned for your traffic
- [ ] Key prefix set (for multi-environment)
- [ ] Database number selected
- [ ] Connection string in secrets manager (not appsettings)
- [ ] Error handling configured in application
- [ ] Logging configured for Redis operations

### Metrics Endpoint
- [ ] `/metrics/endpoints` requires authentication
- [ ] Rate limiting applied if needed
- [ ] HTTPS enforced for metrics endpoint
- [ ] Access logs reviewed

### Monitoring & Alerting
- [ ] Redis connectivity monitoring
- [ ] Flush error rate monitoring
- [ ] Redis memory usage monitoring
- [ ] Application error logs monitored
- [ ] Metrics endpoint availability monitored
- [ ] Alerts configured for critical issues

### Testing
- [ ] Single instance tested
- [ ] Multi-instance tested with shared Redis
- [ ] Redis failure/recovery tested
- [ ] Application restart tested
- [ ] Graceful shutdown tested
- [ ] Metrics accuracy verified

### Documentation
- [ ] Team knows how to access metrics
- [ ] Team knows how to interpret metrics
- [ ] Runbook for Redis issues documented
- [ ] On-call escalation path clear
- [ ] Performance baselines documented

---

## Performance Considerations

### Throughput Impact

- **Single request**: < 1?s latency added (in-memory buffer only)
- **1000 req/sec**: ~10ms to flush 1000 hits to Redis
- **10000 req/sec**: ~50ms to flush 10000 hits (batched)
- **100000 req/sec**: Distributed across instances, each flushes subset

### Memory Impact

| Component | Size |
|-----------|------|
| Buffer per endpoint | ~20 bytes |
| In-memory metadata | ~200 bytes per endpoint |
| Redis storage per endpoint | ~500 bytes (metadata + hits) |

### Network Impact

- Per flush cycle: 1 network round-trip to Redis
- At 1000ms interval: ~1 request/second to Redis server
- Batched operations: Efficient (single command with multiple operations)

### Scaling

- **Single instance**: No problem up to 100k req/sec
- **Multiple instances**: Redis becomes bottleneck after 1M combined req/sec
  - Solution: Use Redis Cluster or separate Redis instances per environment

---

## Related Documentation

- [Main README](README.md) - General documentation
- [GitHub Repository](https://github.com/Pavesi99/EndpointTracker)
- [StackExchange.Redis Documentation](https://stackexchange.github.io/StackExchange.Redis/)
- [Redis Documentation](https://redis.io/documentation)

---

## Switching Back to In-Memory

If you want to remove Redis, simply replace:

```csharp
// Remove this:
builder.Services.AddEndpointTrackerRedis(redis);

// With this:
builder.Services.AddEndpointTracker();
```

No other code changes needed! The interface is identical.

### Option 4: Configuration from appsettings.json

```json
{
  "Redis": {
    "Connection": "localhost:6379,allowAdmin=true",
    "Database": 0,
    "FlushIntervalMs": 1000,
    "KeyPrefix": "my-app:"
  }
}
```

```csharp
var builder = WebApplication.CreateBuilder(args);
var redisConfig = builder.Configuration.GetSection("Redis");

var redisConnection = ConnectionMultiplexer.Connect(
    redisConfig["Connection"] ?? "localhost:6379"
);

builder.Services.AddEndpointTrackerRedis(redisConnection, options =>
{
    options.FlushIntervalMs = int.Parse(redisConfig["FlushIntervalMs"] ?? "1000");
    options.RedisDatabase = int.Parse(redisConfig["Database"] ?? "0");
    options.RedisKeyPrefix = redisConfig["KeyPrefix"] ?? "endpoint-tracker:";
});
```

## How It Works

### Architecture

```
Request Flow:
    ?
Middleware Records Hit
    ?
In-Memory Buffer (ConcurrentDictionary)
    ?
Every 1000ms (configurable)
    ?
Batch Flush to Redis
    ?
Redis Persistent Store
```

### Data Flow

1. **Request Processing**
   - Middleware records endpoint hit in in-memory buffer
   - Returns response immediately (non-blocking)

2. **Background Flushing**
   - Timer triggers every `FlushIntervalMs` milliseconds
   - Collects all buffered hits
   - Sends batched increment operations to Redis
   - Clears buffer

3. **Metrics Queries**
   - Merges in-memory buffer with Redis data
   - Returns up-to-date metrics (even before flush completes)

4. **Shutdown**
   - Final flush ensures no data loss
   - All buffered hits written to Redis

## Redis Schema

### Key Structure

```
my-app:endpoints:metadata           Hash    Endpoint registration data
my-app:hits:{pattern}               String  Hit count for each endpoint
my-app:last-accessed:{pattern}      String  Last access timestamp (ticks)
```

### Example Data

```redis
HGETALL my-app:endpoints:metadata
1) "GET /api/users"
2) {"endpointPattern":"GET /api/users","displayName":"GetUsers","httpMethod":"GET","hitCount":0,"lastAccessedUtc":null,"registeredUtc":"2025-01-15T10:00:00.000Z"}

GET my-app:hits:GET\ /api/users
342

GET my-app:last-accessed:GET\ /api/users
133787654321234567
```

### Custom Key Prefix

```csharp
options.RedisKeyPrefix = "production:";
// Keys become: production:endpoints:metadata, production:hits:...
```

## Configuration Options

### EndpointTrackerOptions

```csharp
public class EndpointTrackerOptions
{
    /// <summary>
    /// Enable Redis storage instead of in-memory.
    /// Default: false
    /// </summary>
    public bool UseRedis { get; set; } = false;

    /// <summary>
    /// Redis connection multiplexer (required if UseRedis = true).
    /// </summary>
    public IConnectionMultiplexer? RedisConnection { get; set; }

    /// <summary>
    /// Redis database number to use.
    /// Default: 0
    /// </summary>
    public int RedisDatabase { get; set; } = 0;

    /// <summary>
    /// How often to flush the in-memory buffer to Redis (milliseconds).
    /// Default: 1000ms
    /// Minimum: 100ms
    /// </summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Prefix for all Redis keys to avoid conflicts.
    /// Default: "endpoint-tracker:"
    /// </summary>
    public string RedisKeyPrefix { get; set; } = "endpoint-tracker:";
}
```

## Tuning Recommendations

### High-Traffic Applications

```csharp
options.FlushIntervalMs = 500;  // More frequent flushes
// Trade-off: More Redis operations but more up-to-date metrics
```

### Low-Traffic Applications

```csharp
options.FlushIntervalMs = 5000;  // Less frequent flushes
// Trade-off: Less Redis load, slightly stale metrics between flushes
```

### Multi-Instance Setups

```csharp
// Use separate Redis databases for different environments
options.RedisDatabase = app.Environment.IsProduction() ? 0 : 1;

// Or use prefixes
options.RedisKeyPrefix = $"{app.Environment.EnvironmentName}:endpoint-tracker:";
```

## Metrics Example

### Reading Metrics

```csharp
app.MapGet("/metrics/endpoints", (IEndpointTrackerService tracker) =>
{
    return Results.Json(tracker.GetMetrics());
});
```

### Response Format

```json
{
  "totalEndpoints": 10,
  "usedEndpoints": 7,
  "unusedEndpoints": 3,
  "totalRequests": 5234,
  "endpoints": [
    {
      "endpointPattern": "GET /api/users",
      "displayName": "GetUsers",
      "httpMethod": "GET",
      "hitCount": 342,
      "lastAccessedUtc": "2025-01-15T14:23:45.123Z",
      "registeredUtc": "2025-01-15T10:00:00.000Z"
    },
    {
      "endpointPattern": "GET /api/admin/settings",
      "displayName": "GetAdminSettings",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2025-01-15T10:00:00.000Z"
    }
  ]
}
```

## Error Handling

### Graceful Degradation

If Redis is unavailable:

1. **Recording Hits** - Continues in-memory buffer (doesn't throw)
2. **Flushing** - Logs error and retries on next interval
3. **Metrics Queries** - Returns available data from buffer
4. **Impact** - Zero impact on request processing

### Example Error Scenario

```
Redis goes down at 10:00:00
10:00:01 - Error logged: "Failed to flush hit buffer to Redis"
10:00:02 - Hits continue buffering in memory
10:00:10 - Redis comes back up
10:00:11 - Successfully flushes accumulated buffer
```

### Monitoring Errors

```csharp
// Check for errors in logs
// Category: EndpointTracker.AspNetCore.Services.RedisEndpointTrackerService
// Level: Error
// Message: "Failed to flush hit buffer to Redis. Will retry on next interval."
```

## Multi-Instance Example

### Setup with Load Balancer

```
Load Balancer
    ?      ?      ?
Instance 1  Instance 2  Instance 3
    ?       ?       ?
    ?????????????????
         Redis
```

### Configuration (Same for All Instances)

```csharp
var builder = WebApplication.CreateBuilder(args);

var redisConnection = ConnectionMultiplexer.Connect("redis.example.com:6379");

builder.Services.AddEndpointTrackerRedis(redisConnection, options =>
{
    options.FlushIntervalMs = 1000;
    options.RedisKeyPrefix = "prod:";
});
```

### Viewing Aggregated Metrics

```csharp
// All instances write to same Redis database
// GET /metrics/endpoints shows combined metrics from all instances

GET /api/users          // Hit on Instance 1
GET /api/users          // Hit on Instance 2
GET /api/users/{id}     // Hit on Instance 3

// Metrics show: GET /api/users - 2 hits (from 2 instances)
//               GET /api/users/{id} - 1 hit (from 1 instance)
```

## Testing

### Manual Testing

```bash
# Start Redis
docker run -d -p 6379:6379 redis

# In another terminal, monitor Redis
redis-cli MONITOR

# Run the application
dotnet run

# In another terminal, test the API
curl http://localhost:5000/api/users
curl http://localhost:5000/api/users
curl http://localhost:5000/metrics/endpoints

# Watch redis-cli for key operations
```

### Unit Testing

```csharp
[Fact]
public void TestRedisFlush()
{
    var options = new EndpointTrackerOptions
    {
        UseRedis = true,
        FlushIntervalMs = 1000
    };
    
    var service = new RedisEndpointTrackerService(
        redisConnection,
        options,
        logger);
    
    service.RegisterEndpoint("GET /api/test", "Test", "GET");
    service.RecordHit("GET /api/test");
    service.RecordHit("GET /api/test");
    
    service.FlushHitBuffer();
    
    var metrics = service.GetMetrics();
    Assert.Single(metrics.Endpoints);
    Assert.Equal(2, metrics.Endpoints.First().HitCount);
}
```

## Troubleshooting

### Redis Connection Issues

```
Issue: "Error connecting to Redis at localhost:6379"
Solution: Ensure Redis is running: docker run -d -p 6379:6379 redis

Issue: "Connection timeout"
Solution: Check network connectivity: redis-cli ping

Issue: "Authentication failed"
Solution: Include password in connection string:
         "localhost:6379,password=mypassword"
```

### Metrics Not Updating

```
Issue: Metrics appear frozen
Solution: Check flush interval - increase FlushIntervalMs

Issue: Buffer not flushing
Solution: Ensure RedisFlushHostedService is registered
          Check logs for flush errors
```

### High Redis Memory Usage

```
Issue: Redis memory continuously growing
Solution: 1. Check flush interval (too frequent = more keys)
          2. Call tracker.Reset() periodically
          3. Configure Redis eviction policy
          
Example: redis-cli CONFIG SET maxmemory-policy allkeys-lru
```

## Performance Considerations

### Throughput

- **Recording Hits**: O(1) in-memory buffer add (non-blocking)
- **Flushing**: Batched Redis operations (efficient)
- **Metrics Query**: O(n) where n = number of endpoints

### Memory Impact

- **In-Memory Buffer**: ~100 bytes per unique endpoint pattern
- **Redis Storage**: ~500 bytes per endpoint (including metadata)

### Recommendation

For high-traffic applications (>10k requests/second):
- Use `FlushIntervalMs = 500-1000`
- Monitor Redis memory usage
- Consider distributed Redis setup

## Production Checklist

- [ ] Redis server configured for HA/replication
- [ ] Connection string includes authentication
- [ ] FlushIntervalMs tuned for your traffic
- [ ] Metrics endpoints secured with authentication
- [ ] Redis persistence enabled (AOF or RDB)
- [ ] Monitoring/alerts set up for Redis availability
- [ ] Tested graceful degradation when Redis is down
- [ ] Documented key prefix naming convention

## See Also

- [StackExchange.Redis Documentation](https://stackexchange.github.io/StackExchange.Redis/)
- [Redis Documentation](https://redis.io/documentation)
- [EndpointTracker Main README](README.md)
