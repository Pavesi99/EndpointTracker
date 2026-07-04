# SQL Persistence Example

Use SQL persistence together with Redis by configuring the following options:

```json
{
  "EndpointTracker": {
    "UseRedis": true,
    "RedisDatabase": 0,
    "RedisKeyPrefix": "endpoint-tracker:",
    "UseSqlPersistence": true,
    "SqlProvider": "PostgreSQL",
    "SqlConnectionString": "Host=localhost;Username=postgres;Password=secret;Database=endpointtracker;",
    "SqlPersistIntervalMinutes": 10,
    "SqlTableName": "EndpointTrackerMetrics"
  }
}
```

Then register the tracker with DI and the Redis multiplexer:

```csharp
var redis = ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis"));
builder.Services.AddEndpointTracker(options =>
{
    options.UseRedis = true;
    options.RedisConnection = redis;
    options.UseSqlPersistence = true;
    options.SqlProvider = builder.Configuration["EndpointTracker:SqlProvider"];
    options.SqlConnectionString = builder.Configuration["EndpointTracker:SqlConnectionString"];
    options.SqlPersistIntervalMinutes = int.Parse(builder.Configuration["EndpointTracker:SqlPersistIntervalMinutes"] ?? "10");
});
```
