# EndpointTracker.AspNetCore

[![NuGet](https://img.shields.io/nuget/v/EndpointTracker.AspNetCore.svg)](https://www.nuget.org/packages/EndpointTracker.AspNetCore/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)

A production-ready ASP.NET Core 8.0 middleware package that tracks API endpoint usage, counts hits, records access timestamps, and identifies unused endpoints. Perfect for monitoring, analytics, and identifying dead code in your APIs.

## Features

- **Automatic Endpoint Discovery** - Captures all mapped endpoints at startup
- **Thread-Safe Tracking** - Uses `ConcurrentDictionary` and atomic operations for high-performance concurrent access
- **Hit Counting** - Tracks how many times each endpoint has been accessed
- **Timestamp Tracking** - Records last access time for each endpoint
- **Unused Endpoint Detection** - Easily identify endpoints that have never been called
- **Built-in Metrics API** - Exposes `/metrics/endpoints` and `/metrics/unused` routes
- **Zero Configuration** - Works out of the box with minimal setup
- **Production Ready** - Fully documented with XML comments, optimized for performance

## Installation

```bash
dotnet add package EndpointTracker.AspNetCore
```

## Quick Start

### 1. Register the Service

```csharp
using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register EndpointTracker
builder.Services.AddEndpointTracker();

var app = builder.Build();
```

### 2. Add Middleware

```csharp
// Must be after UseRouting() (implicit in minimal APIs)
app.UseEndpointTracker();
```

### 3. Map Your Endpoints

```csharp
app.MapGet("/api/users", () => Results.Ok(new[] { "User1", "User2" }))
   .WithName("GetUsers");

app.MapGet("/api/products/{id}", (int id) => Results.Ok($"Product {id}"))
   .WithName("GetProduct");
```

### 4. Add Metrics Endpoints & Register

```csharp
// Add metrics routes
app.MapEndpointTrackerMetrics();

// Register all endpoints (must be AFTER all MapX calls)
app.UseEndpointTrackerRegistration();

app.Run();
```

## Complete Example

```csharp
using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Step 1: Add EndpointTracker service
builder.Services.AddEndpointTracker();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Step 2: Use tracking middleware
app.UseEndpointTracker();

// Step 3: Map your endpoints
app.MapGet("/api/users", () => Results.Ok(new { Users = new[] { "Alice", "Bob" } }))
   .WithName("GetUsers");

app.MapGet("/api/users/{id}", (int id) => Results.Ok(new { Id = id, Name = $"User{id}" }))
   .WithName("GetUserById");

app.MapPost("/api/users", (object user) => Results.Created("/api/users/123", user))
   .WithName("CreateUser");

// Step 4: Map metrics endpoints
app.MapEndpointTrackerMetrics();

// Step 5: Register all endpoints with tracker
app.UseEndpointTrackerRegistration();

app.Run();
```

## Metrics API

### GET /metrics/endpoints

Returns comprehensive endpoint usage statistics:

```json
{
  "totalEndpoints": 10,
  "usedEndpoints": 7,
  "unusedEndpoints": 3,
  "totalRequests": 1543,
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

### GET /metrics/unused

Returns only endpoints that have never been accessed:

```json
{
  "count": 3,
  "endpoints": [
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

## Programmatic Access

Inject `IEndpointTrackerService` to access tracking data programmatically:

```csharp
app.MapGet("/custom-metrics", (IEndpointTrackerService tracker) =>
{
    var allUsage = tracker.GetAllEndpointUsage();
    var unused = tracker.GetUnusedEndpoints();
    var metrics = tracker.GetMetrics();

    return Results.Ok(new { allUsage, unused, metrics });
});
```

## Advanced Usage

### Custom Metrics Endpoint

```csharp
app.MapGet("/api/health/dead-endpoints", (IEndpointTrackerService tracker) =>
{
    var unused = tracker.GetUnusedEndpoints();
    if (unused.Any())
    {
        return Results.Json(new
        {
            Status = "Warning",
            DeadEndpoints = unused.Count(),
            Details = unused
        });
    }
    return Results.Ok(new { Status = "Healthy", DeadEndpoints = 0 });
});
```

### Reset Tracking Data

```csharp
app.MapPost("/admin/reset-metrics", (IEndpointTrackerService tracker) =>
{
    tracker.Reset();
    return Results.Ok("Metrics reset successfully");
})
.RequireAuthorization("Admin"); // Add appropriate authorization
```

## Thread Safety

All operations are thread-safe:
- Uses `ConcurrentDictionary<string, EndpointUsageInfo>` for storing endpoint data
- Employs `Interlocked.Increment` for atomic counter updates
- Safe for high-concurrency scenarios

## Performance Considerations

- **Minimal Overhead** - Lightweight middleware adds negligible latency
- **In-Memory Storage** - All data stored in memory (not suitable for distributed systems without external storage)
- **Singleton Service** - Single instance maintains state across all requests
- **No I/O Operations** - Pure in-memory operations for maximum speed

## Integration with Monitoring Tools

Export metrics to your monitoring system:

```csharp
// Example: Export to Prometheus, Application Insights, etc.
app.MapGet("/metrics/prometheus", (IEndpointTrackerService tracker) =>
{
    var metrics = tracker.GetMetrics();
    var prometheusFormat = FormatAsPrometheus(metrics);
    return Results.Text(prometheusFormat, "text/plain");
});
```

## Best Practices

1. **Call `UseEndpointTrackerRegistration()` last** - After all `MapX()` calls to ensure all endpoints are registered
2. **Secure metrics endpoints** - Add authentication/authorization in production
3. **Monitor unused endpoints** - Regularly review unused endpoints for potential removal
4. **Consider distributed scenarios** - For multi-instance deployments, consider exporting to centralized storage

## Requirements

- .NET 8.0 or higher
- ASP.NET Core 8.0

## License

This project is licensed under the MIT License.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For issues, questions, or contributions, please visit the [GitHub repository](https://github.com/Pavesi99/EndpointTracker).
