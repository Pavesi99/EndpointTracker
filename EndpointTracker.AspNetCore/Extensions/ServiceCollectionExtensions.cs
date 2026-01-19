using EndpointTracker.AspNetCore.Internal;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace EndpointTracker.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring endpoint tracking services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds endpoint tracking services to the dependency injection container using in-memory storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEndpointTracker(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Register the tracker service as a singleton to maintain state across requests
        services.TryAddSingleton<IEndpointTrackerService, EndpointTrackerService>();

        // Register the hosted service that will register all endpoints at startup
        services.AddHostedService<EndpointRegistrationHostedService>();

        return services;
    }

    /// <summary>
    /// Adds endpoint tracking services with configurable options (in-memory or Redis).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure the tracking options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEndpointTracker(
        this IServiceCollection services,
        Action<EndpointTrackerOptions> configureOptions)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        var options = new EndpointTrackerOptions();
        configureOptions(options);

        services.TryAddSingleton(options);

        if (options.UseRedis)
        {
            if (options.RedisConnection == null)
                throw new InvalidOperationException(
                    "RedisConnection must be provided when UseRedis is true. " +
                    "Configure it in the options: options.RedisConnection = connectionMultiplexer;");

            services.TryAddSingleton(options.RedisConnection);
            services.TryAddSingleton<IEndpointTrackerService, RedisEndpointTrackerService>();
            services.AddHostedService<RedisFlushHostedService>();
        }
        else
        {
            services.TryAddSingleton<IEndpointTrackerService, EndpointTrackerService>();
        }

        // Register the hosted service that will register all endpoints at startup
        services.AddHostedService<EndpointRegistrationHostedService>();

        return services;
    }

    /// <summary>
    /// Adds endpoint tracking services with Redis storage.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="redisConnection">The Redis connection multiplexer.</param>
    /// <param name="configureOptions">Optional action to configure additional Redis options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEndpointTrackerRedis(
        this IServiceCollection services,
        IConnectionMultiplexer redisConnection,
        Action<EndpointTrackerOptions>? configureOptions = null)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (redisConnection == null)
            throw new ArgumentNullException(nameof(redisConnection));

        var options = new EndpointTrackerOptions { UseRedis = true, RedisConnection = redisConnection };
        configureOptions?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton(redisConnection);
        services.TryAddSingleton<IEndpointTrackerService, RedisEndpointTrackerService>();
        services.AddHostedService<RedisFlushHostedService>();
        services.AddHostedService<EndpointRegistrationHostedService>();

        return services;
    }
}
