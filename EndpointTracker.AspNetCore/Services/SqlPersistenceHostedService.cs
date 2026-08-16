using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointTracker.AspNetCore.Services;

internal sealed class SqlPersistenceHostedService : BackgroundService
{
    private readonly RedisEndpointTrackerService _redisTrackerService;
    private readonly SqlPersistenceStore _sqlPersistenceStore;
    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<SqlPersistenceHostedService> _logger;
    private readonly SemaphoreSlim _persistenceLock = new(1, 1);

    public SqlPersistenceHostedService(
        RedisEndpointTrackerService redisTrackerService,
        SqlPersistenceStore sqlPersistenceStore,
        EndpointTrackerOptions options,
        ILogger<SqlPersistenceHostedService> logger)
    {
        _redisTrackerService = redisTrackerService ?? throw new ArgumentNullException(nameof(redisTrackerService));
        _sqlPersistenceStore = sqlPersistenceStore ?? throw new ArgumentNullException(nameof(sqlPersistenceStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _sqlPersistenceStore.EnsureTableExistsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SQL persistence tables are ready.");
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PersistPendingMetricsAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.SqlPersistIntervalMinutes));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await PersistPendingMetricsAsync(stoppingToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SQL persistence is stopping. Persisting final metrics.");
        await PersistPendingMetricsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistPendingMetricsAsync(CancellationToken cancellationToken)
    {
        if (!await _persistenceLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping SQL persistence because another persistence operation is still active.");
            return;
        }

        try
        {
            await using var lease = await _redisTrackerService
                .AcquireSqlPersistenceLeaseAsync(cancellationToken)
                .ConfigureAwait(false);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.LeaseLostToken);
            var operationToken = operationCancellation.Token;

            var batches = await _redisTrackerService
                .PreparePersistenceBatchesAsync(operationToken)
                .ConfigureAwait(false);

            foreach (var batch in batches)
            {
                operationToken.ThrowIfCancellationRequested();
                try
                {
                    var applied = await _sqlPersistenceStore
                        .PersistEndpointUsageBatchFencedAsync(
                            batch.BatchId,
                            batch.EndpointUsage,
                            lease.FenceToken,
                            operationToken)
                        .ConfigureAwait(false);

                    lease.ThrowIfLeaseLost();
                    await _redisTrackerService
                        .CompletePersistenceBatchAsync(batch.BatchId, operationToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        applied
                            ? "Persisted Redis batch {BatchId} containing {EndpointCount} endpoint metrics to SQL."
                            : "Redis batch {BatchId} was already persisted; completed Redis cleanup.",
                        batch.BatchId,
                        batch.EndpointUsage.Count);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException)
                {
                    _logger.LogError(
                        ex,
                        "Redis batch {BatchId} contains data that SQL persistence cannot accept. " +
                        "It remains readable in Redis; later batches will still be processed.",
                        batch.BatchId);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to persist Redis batch {BatchId}. It remains in Redis and will be retried.",
                        batch.BatchId);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prepare Redis metrics for SQL persistence. Data remains available for retry.");
        }
        finally
        {
            _persistenceLock.Release();
        }
    }
}
