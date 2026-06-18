using EndpointTracker.AspNetCore.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointTracker.AspNetCore.Services;

internal class SqlPersistenceHostedService : IHostedService, IDisposable
{
    private readonly RedisEndpointTrackerService _redisTrackerService;
    private readonly SqlPersistenceStore _sqlPersistenceStore;
    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<SqlPersistenceHostedService> _logger;
    private Timer? _persistTimer;

    public SqlPersistenceHostedService(
        IEndpointTrackerService trackerService,
        SqlPersistenceStore sqlPersistenceStore,
        EndpointTrackerOptions options,
        ILogger<SqlPersistenceHostedService> logger)
    {
        if (trackerService is not RedisEndpointTrackerService redisTracker)
        {
            throw new InvalidOperationException(
                "SqlPersistenceHostedService can only be used with RedisEndpointTrackerService.");
        }

        _redisTrackerService = redisTracker;
        _sqlPersistenceStore = sqlPersistenceStore ?? throw new ArgumentNullException(nameof(sqlPersistenceStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sqlPersistenceStore.EnsureTableExists();
            _logger.LogInformation("SqlPersistenceHostedService ensured persistence table exists.");

            PersistToSql();

            var intervalMinutes = Math.Max(_options.SqlPersistIntervalMinutes, 1);
            _persistTimer = new Timer(
                _ => PersistToSql(),
                null,
                TimeSpan.FromMinutes(intervalMinutes),
                TimeSpan.FromMinutes(intervalMinutes));

            _logger.LogInformation("SqlPersistenceHostedService started with interval of {IntervalMinutes} minute(s).", intervalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SQL persistence host service.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SqlPersistenceHostedService stopping. Persisting final metrics.");
        PersistToSql();
        _persistTimer?.Dispose();
        return Task.CompletedTask;
    }

    private void PersistToSql()
    {
        try
        {
            _redisTrackerService.FlushHitBuffer();
            var endpointUsage = _redisTrackerService.GetAllEndpointUsage().ToList();

            if (!endpointUsage.Any())
            {
                _logger.LogDebug("No endpoint usage metrics available to persist to SQL.");
                return;
            }

            _sqlPersistenceStore.PersistEndpointUsage(endpointUsage);
            _redisTrackerService.ClearRedisData();

            _logger.LogInformation("Persisted {EndpointCount} endpoint metrics to SQL and cleared Redis data.", endpointUsage.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist endpoint metrics to SQL.");
        }
    }

    public void Dispose()
    {
        _persistTimer?.Dispose();
    }
}
