using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Services;

namespace EndpointTracker.Example
{
    public class CustomEndpointTrackerService : EndpointTrackerService
    {
        // Example: override clock (optional)
        protected override DateTime UtcNow => DateTime.UtcNow;

        public override void RecordHit(string endpointPattern)
        {
            base.RecordHit(endpointPattern);
        }

        // 2) Override RecordHitCore to change behavior
        protected override EndpointUsageInfo RecordHitCore(string endpointPattern, DateTime nowUtc)
        {
            // Example custom behavior:
            // - ignore health endpoints
            // - keep default hit tracking
            // - add a "burst" count (count each request as 3 hits)
            if (endpointPattern.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                return base.RecordHitCore(endpointPattern, nowUtc);

            // Call base once (creates/updates the entry)
            base.RecordHitCore(endpointPattern, nowUtc);

            // Add 2 extra hits (total 3) for non-health routes
            base.RecordHitCore(endpointPattern, nowUtc);
            return base.RecordHitCore(endpointPattern, nowUtc);
        }
    }
}
