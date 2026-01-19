using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Background hosted service that flushes the Redis endpoint tracker's hit buffer at regular intervals.
/// </summary>
internal class RedisFlushHostedService : IHostedService
{
    private readonly RedisEndpointTrackerService _trackerService;
    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<RedisFlushHostedService> _logger;
    private Timer? _flushTimer;

    public RedisFlushHostedService(
        IEndpointTrackerService trackerService,
        EndpointTrackerOptions options,
        ILogger<RedisFlushHostedService> logger)
    {
        if (!(trackerService is RedisEndpointTrackerService redisTracker))
        {
            throw new InvalidOperationException(
                "RedisFlushHostedService can only be used with RedisEndpointTrackerService");
        }

        _trackerService = redisTracker;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var flushInterval = Math.Max(_options.FlushIntervalMs, 100); // Minimum 100ms
        
        _flushTimer = new Timer(
            _ => FlushBuffer(),
            null,
            TimeSpan.FromMilliseconds(flushInterval),
            TimeSpan.FromMilliseconds(flushInterval));

        _logger.LogInformation(
            "RedisFlushHostedService started with flush interval of {FlushIntervalMs}ms",
            flushInterval);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("RedisFlushHostedService stopping. Flushing final buffer...");
        
        // Final flush on shutdown
        FlushBuffer();

        _flushTimer?.Dispose();
        
        return Task.CompletedTask;
    }

    private void FlushBuffer()
    {
        try
        {
            _trackerService.FlushHitBuffer();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in RedisFlushHostedService flush timer");
        }
    }
}
