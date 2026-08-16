using EndpointTracker.AspNetCore.Internal;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace EndpointTracker.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring endpoint tracking services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds endpoint tracking with in-memory storage.
    /// </summary>
    public static IServiceCollection AddEndpointTracker(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IEndpointTrackerService, EndpointTrackerService>();
        services.AddHostedService<EndpointRegistrationHostedService>();
        return services;
    }

    /// <summary>
    /// Adds endpoint tracking with configurable in-memory, Redis, and optional SQL persistence.
    /// </summary>
    public static IServiceCollection AddEndpointTracker(
        this IServiceCollection services,
        Action<EndpointTrackerOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        var options = new EndpointTrackerOptions();
        configureOptions(options);
        ValidateOptions(options);
        services.TryAddSingleton(options);

        if (options.UseRedis)
            AddRedisTracker(services, options);
        else
            services.TryAddSingleton<IEndpointTrackerService, EndpointTrackerService>();

        services.AddHostedService<EndpointRegistrationHostedService>();
        return services;
    }

    /// <summary>
    /// Adds endpoint tracking with Redis storage and optional SQL persistence.
    /// </summary>
    public static IServiceCollection AddEndpointTrackerRedis(
        this IServiceCollection services,
        IConnectionMultiplexer redisConnection,
        Action<EndpointTrackerOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(redisConnection);

        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = redisConnection
        };
        configureOptions?.Invoke(options);
        ValidateOptions(options);
        services.TryAddSingleton(options);
        AddRedisTracker(services, options);
        services.AddHostedService<EndpointRegistrationHostedService>();
        return services;
    }

    private static void AddRedisTracker(IServiceCollection services, EndpointTrackerOptions options)
    {
        var redisConnection = options.RedisConnection
            ?? throw new InvalidOperationException("RedisConnection must be configured when UseRedis is true.");

        services.TryAddSingleton<IConnectionMultiplexer>(redisConnection);

        if (options.UseSqlPersistence)
            services.TryAddSingleton<SqlPersistenceStore>();

        services.TryAddSingleton<RedisEndpointTrackerService>(serviceProvider =>
            new RedisEndpointTrackerService(
                serviceProvider.GetRequiredService<IConnectionMultiplexer>(),
                serviceProvider.GetRequiredService<EndpointTrackerOptions>(),
                options.UseSqlPersistence
                    ? serviceProvider.GetRequiredService<SqlPersistenceStore>()
                    : null,
                serviceProvider.GetRequiredService<ILogger<RedisEndpointTrackerService>>()));

        services.TryAddSingleton<IEndpointTrackerService>(serviceProvider =>
            serviceProvider.GetRequiredService<RedisEndpointTrackerService>());

        if (options.UseSqlPersistence)
            services.AddHostedService<SqlPersistenceHostedService>();
        services.AddHostedService<RedisFlushHostedService>();
    }

    private static void ValidateOptions(EndpointTrackerOptions options)
    {
        if (options.FlushIntervalMs < 100)
            throw new InvalidOperationException("FlushIntervalMs must be at least 100 milliseconds.");

        if (!options.UseRedis)
        {
            if (options.UseSqlPersistence)
                throw new InvalidOperationException("SQL persistence requires Redis storage.");
            return;
        }

        if (options.RedisConnection == null)
            throw new InvalidOperationException("RedisConnection must be configured when UseRedis is true.");

        if (!options.UseSqlPersistence)
            return;

        if (string.IsNullOrWhiteSpace(options.SqlProvider))
            throw new InvalidOperationException("SqlProvider must be configured when SQL persistence is enabled.");
        if (!options.SqlProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) &&
            !options.SqlProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) &&
            !options.SqlProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Unsupported SqlProvider. Supported values are 'SqlServer' and 'PostgreSQL'.");
        }
        if (string.IsNullOrWhiteSpace(options.SqlConnectionString))
            throw new InvalidOperationException("SqlConnectionString must be configured when SQL persistence is enabled.");
        if (options.SqlPersistIntervalMinutes < 1)
            throw new InvalidOperationException("SqlPersistIntervalMinutes must be at least one minute.");
    }
}
