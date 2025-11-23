using EndpointTracker.AspNetCore.Internal;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointTracker.AspNetCore.Extensions;

/// <summary>
/// Extension methods for configuring endpoint tracking services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds endpoint tracking services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddEndpointTracker(this IServiceCollection services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        // Register the tracker service as a singleton to maintain state across requests
        services.AddSingleton<IEndpointTrackerService, EndpointTrackerService>();

        // Register the hosted service that will register all endpoints at startup
        services.AddHostedService<EndpointRegistrationHostedService>();

        return services;
    }
}
