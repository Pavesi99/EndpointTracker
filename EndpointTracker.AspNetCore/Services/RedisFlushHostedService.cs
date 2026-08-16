using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointTracker.AspNetCore.Services;

/// <summary>
/// Periodically flushes the in-memory hit buffer to Redis.
/// </summary>
internal sealed class RedisFlushHostedService : BackgroundService
{
    private readonly RedisEndpointTrackerService _trackerService;
    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<RedisFlushHostedService> _logger;

    public RedisFlushHostedService(
        RedisEndpointTrackerService trackerService,
        EndpointTrackerOptions options,
        ILogger<RedisFlushHostedService> logger)
    {
        _trackerService = trackerService ?? throw new ArgumentNullException(nameof(trackerService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.FlushIntervalMs));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _trackerService.FlushHitBufferAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush endpoint hits to Redis. Buffered data will be retried.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _trackerService.FlushHitBufferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to flush the final endpoint hit buffer during shutdown.");
        }
    }
}
