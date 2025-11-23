using EndpointTracker.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register EndpointTracker service
builder.Services.AddEndpointTracker();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

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
.WithName("GetWeatherForecast")
.WithOpenApi();

app.MapGet("/api/users", () =>
{
    return Results.Ok(new[]
    {
        new { Id = 1, Name = "Alice" },
        new { Id = 2, Name = "Bob" }
    });
})
.WithName("GetUsers")
.WithTags("Users")
.WithOpenApi();

app.MapGet("/api/users/{id:int}", (int id) =>
{
    return Results.Ok(new { Id = id, Name = $"User {id}" });
})
.WithName("GetUserById")
.WithTags("Users")
.WithOpenApi();

app.MapPost("/api/users", (object user) =>
{
    return Results.Created($"/api/users/123", user);
})
.WithName("CreateUser")
.WithTags("Users")
.WithOpenApi();

app.MapPut("/api/users/{id:int}", (int id, object user) =>
{
    return Results.NoContent();
})
.WithName("UpdateUser")
.WithTags("Users")
.WithOpenApi();

app.MapDelete("/api/users/{id:int}", (int id) =>
{
    return Results.NoContent();
})
.WithName("DeleteUser")
.WithTags("Users")
.WithOpenApi();

// This endpoint will likely remain unused in testing
app.MapGet("/api/admin/settings", () =>
{
    return Results.Ok(new { Setting1 = "Value1", Setting2 = "Value2" });
})
.WithName("GetAdminSettings")
.WithTags("Admin")
.WithOpenApi();

// Map the endpoint tracker metrics routes
app.MapEndpointTrackerMetrics();

// Endpoints are automatically registered via the hosted service

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
