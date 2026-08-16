using EndpointTracker.AspNetCore.Models;

namespace EndpointTracker.AspNetCore.Services;

internal sealed record RedisPersistenceBatch(
    string BatchId,
    IReadOnlyList<EndpointUsageInfo> EndpointUsage);
