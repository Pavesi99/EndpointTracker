using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// ENDPOINTRACKER
// Register first your custom implementation if you want to use one
// builder.Services.AddSingleton<IEndpointTrackerService, CustomEndpointTrackerService>();

// ENDPOINTRACKER
// Register EndpointTracker service with optional SQL persistence via configuration
var redisConnectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis must be configured.");
var redis = ConnectionMultiplexer.Connect(redisConnectionString);
var endpointTrackerConfiguration = builder.Configuration.GetSection("EndpointTracker");

builder.Services.AddEndpointTracker(options =>
{
    options.UseRedis = true;
    options.RedisConnection = redis;
    options.RedisDatabase = endpointTrackerConfiguration.GetValue("RedisDatabase", 0);
    options.RedisKeyPrefix = endpointTrackerConfiguration["RedisKeyPrefix"] ?? "endpoint-tracker:";
    options.UseSqlPersistence = endpointTrackerConfiguration.GetValue("UseSqlPersistence", false);
    options.SqlProvider = endpointTrackerConfiguration["SqlProvider"];
    options.SqlConnectionString = builder.Configuration.GetConnectionString("EndpointTrackerSql");
    options.SqlPersistIntervalMinutes = endpointTrackerConfiguration.GetValue("SqlPersistIntervalMinutes", 10);
    options.SqlTableName = endpointTrackerConfiguration["SqlTableName"] ?? "EndpointTrackerMetrics";
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseAuthentication();
app.UseAuthorization();
app.UseHttpsRedirection();

// ENDPOINTRACKER
// Add EndpointTracker middleware (must be after UseRouting, which is implicit with minimal APIs)
app.UseEndpointTracker();

// Sample endpoints to demonstrate tracking
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.MapGet("/api/users", () =>
{
    return Results.Ok(new[]
    {
        new { Id = 1, Name = "Alice" },
        new { Id = 2, Name = "Bob" }
    });
})
.WithName("GetUsers")
.WithTags("Users");

app.MapGet("/api/users/{id:int}", (int id) =>
{
    return Results.Ok(new { Id = id, Name = $"User {id}" });
})
.WithName("GetUserById")
.WithTags("Users");

app.MapPost("/api/users", (object user) =>
{
    return Results.Created($"/api/users/123", user);
})
.WithName("CreateUser")
.WithTags("Users");

app.MapPut("/api/users/{id:int}", (int id, object user) =>
{
    return Results.NoContent();
})
.WithName("UpdateUser")
.WithTags("Users");

app.MapDelete("/api/users/{id:int}", (int id) =>
{
    return Results.NoContent();
})
.WithName("DeleteUser")
.WithTags("Users");

// This endpoint will likely remain unused in testing
app.MapGet("/api/admin/settings", () =>
{
    return Results.Ok(new { Setting1 = "Value1", Setting2 = "Value2" });
})
.WithName("GetAdminSettings")
.WithTags("Admin");

// ENDPOINTRACKER 
// Map the endpoint tracker metrics routes
// Remarks: Remove it if you want to not have metrics endpoints and use only the services
app.MapEndpointTrackerMetrics(isAuthRequired: false);

// Endpoints are automatically registered via the hosted service

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
