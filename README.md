# EndpointTracker

Track every ASP.NET Core endpoint automatically, expose metrics, and ship a production-ready NuGet package. This repository contains only the **EndpointTracker.AspNetCore** middleware/library and the **EndpointTracker.Example** application so you can understand, run, and publish the open-source core quickly.

---

## 📦 What's Inside

- `EndpointTracker.AspNetCore/` – the reusable middleware, services, and NuGet metadata
- `EndpointTracker.Example/` – a minimal API that demonstrates the package end-to-end

---

## 🚀 Quick Start

### 1. Install the package
```bash
dotnet add package EndpointTracker.AspNetCore
```

### 2. Register services
```csharp
using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointTracker();
```

### 3. Configure middleware, endpoints, and metrics
```csharp
var app = builder.Build();

app.UseEndpointTracker();

app.MapGet("/api/users", () => Results.Ok(new[] { "Alice", "Bob" }));
app.MapGet("/api/products/{id}", (int id) => Results.Ok($"Product {id}"));

app.MapEndpointTrackerMetrics();

app.UseEndpointTrackerRegistration(); // MUST be last
```

### 4. Run & test
```bash
dotnet run

curl http://localhost:5000/api/users
curl http://localhost:5000/metrics/endpoints
curl http://localhost:5000/metrics/unused
```

### 5. Programmatic access
```csharp
app.MapGet("/health/api-coverage", (IEndpointTrackerService tracker) =>
{
    var metrics = tracker.GetMetrics();
    var coverage = (double)metrics.UsedEndpoints / metrics.TotalEndpoints * 100;

    return Results.Ok(new
    {
        TotalEndpoints = metrics.TotalEndpoints,
        CoveragePercent = coverage,
        UnusedCount = metrics.UnusedEndpoints
    });
});
```

### 6. Production tips
```csharp
app.MapEndpointTrackerMetrics()
   .RequireAuthorization("MetricsReader");
```

```csharp
app.MapGet("/metrics/export", (IEndpointTrackerService tracker) =>
{
    var metrics = tracker.GetMetrics();
    // Export to Prometheus, AppInsights, etc.
});
```

---

## 📊 Metrics Endpoints

| Endpoint | Description |
| --- | --- |
| `GET /metrics/endpoints` | Total endpoints, used vs unused, per-endpoint hit counts, last accessed timestamps |
| `GET /metrics/unused` | Routes that have never been called so you can remove dead code |

**Use cases**
- Monitor API usage
- Identify dead code
- Analyze traffic patterns
- Verify test coverage
- Auto-discover documented routes

---

## 🧱 Package Architecture

```
EndpointTracker.AspNetCore/
├── Models/
│   ├── EndpointUsageInfo.cs          # Data model for endpoint statistics
│   └── EndpointMetricsResponse.cs    # Response model for metrics API
├── Services/
│   ├── IEndpointTrackerService.cs    # Service interface
│   └── EndpointTrackerService.cs     # Thread-safe tracking implementation
├── Middleware/
│   └── EndpointTrackerMiddleware.cs  # Request interception middleware
├── Extensions/
│   ├── ServiceCollectionExtensions.cs    # DI registration
│   └── ApplicationBuilderExtensions.cs   # Middleware & endpoint mapping
├── README.md                          # Package documentation
└── EndpointTracker.AspNetCore.csproj # Project configuration with NuGet metadata
```

### Models
- **EndpointUsageInfo** – route pattern, display name, HTTP method(s), hit count, registration time, last access time
- **EndpointMetricsResponse** – total endpoints, used/unused counts, total requests, list of `EndpointUsageInfo`

### Services
`IEndpointTrackerService` + `EndpointTrackerService`
- Thread-safe `ConcurrentDictionary<string, EndpointUsageInfo>`
- `Interlocked.Increment` for atomic counters
- Singleton lifetime to retain metrics throughout the app
- Methods: `RegisterEndpoint`, `RecordHit`, `GetAllEndpointUsage`, `GetUnusedEndpoints`, `GetMetrics`, `Reset`

### Middleware
`EndpointTrackerMiddleware`
1. Calls `_next(context)`
2. Reads the matched `RouteEndpoint`
3. Builds a pattern (`HTTP_METHOD route_pattern`)
4. Calls `_trackerService.RecordHit(pattern)`

### Extensions
- `services.AddEndpointTracker()`
- `app.UseEndpointTracker()` (after routing)
- `app.MapEndpointTrackerMetrics()` (adds `/metrics/endpoints` & `/metrics/unused` with Swagger metadata)
- `app.UseEndpointTrackerRegistration()` (call after all endpoint mappings)

---

## 🧪 Example Application
```bash
cd EndpointTracker.Example
dotnet run

curl http://localhost:5000/api/users
curl http://localhost:5000/weatherforecast
curl http://localhost:5000/metrics/endpoints
curl http://localhost:5000/metrics/unused
```

---

## 📤 Building & Publishing the NuGet Package

### 1. Pack
```bash
cd EndpointTracker.AspNetCore
dotnet pack -c Release -o ./nupkg
```

### 2. Publish to NuGet.org
```bash
# Acquire an API key from https://www.nuget.org/account/apikeys
dotnet nuget push ./nupkg/EndpointTracker.AspNetCore.1.0.0.nupkg \
  --api-key YOUR_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

### 3. Publish symbols (optional)
```bash
dotnet nuget push ./nupkg/EndpointTracker.AspNetCore.1.0.0.snupkg \
  --api-key YOUR_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

### 4. Versioning
- Follow [Semantic Versioning](https://semver.org/)
- Update `<Version>` inside `EndpointTracker.AspNetCore.csproj`

---

## ⚙️ Production Considerations
- Secure metrics endpoints (authorization, firewall rules)
- Default storage is in-memory per instance; implement a custom `IEndpointTrackerService` (Redis, SQL, etc.) for distributed systems
- Middleware overhead ≈ 0.1ms per request and ≈1KB RAM per endpoint

### Custom implementations
```csharp
public class RedisEndpointTrackerService : IEndpointTrackerService
{
    private readonly IConnectionMultiplexer _redis;
    // ...
}

builder.Services.AddSingleton<IEndpointTrackerService, RedisEndpointTrackerService>();
```

```csharp
app.MapGet("/metrics/export", (IEndpointTrackerService tracker) =>
{
    var metrics = tracker.GetMetrics();
    return ExportToMonitoringSystem(metrics);
});
```

---

## 🧰 Troubleshooting
- Ensure `UseEndpointTracker()` runs after routing middleware
- Call `UseEndpointTrackerRegistration()` after all `MapX()` calls so every endpoint is discovered
- Built-in service is thread-safe; confirm the same if you replace it

---

## 🛡️ License & Support
- MIT License
- Questions? Open an issue in this repository

Happy tracking! 🎉
