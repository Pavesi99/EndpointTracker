# SQL Persistence Production-Readiness Test Results

Run date: 2026-08-16 09:32:40 -03

Branch: `dev`

Base commit: `c242b5d6d6e239a75309753dd0d2f0ea96624668`

Tested state: base commit plus the current uncommitted SQL-persistence worktree changes

Configuration: .NET 10 Release

## Result

**PASS — 34 tests passed, 0 failed, 0 skipped.**

- Solution restore: passed
- Clean Release build: passed with 0 warnings and 0 errors
- Redis integration: passed against Redis 7
- PostgreSQL integration: passed against PostgreSQL 16
- SQL Server integration: passed against SQL Server 2022
- NuGet vulnerability audit: no vulnerable direct or transitive packages
- NuGet package creation: `.nupkg` and `.snupkg` created successfully
- Git whitespace/error check: passed

The first `dotnet clean` invocation used an unsupported `--no-restore` option and was corrected. The corrected clean removed the Release outputs but its MSBuild process did not exit after cleanup, so that process was terminated. A network-enabled restore and a complete Release rebuild then succeeded from the removed outputs. This was a local tooling/process-exit issue, not a source build or test failure.

## Services used

| Service | Container image | Test endpoint | Readiness result |
|---|---|---|---|
| Redis | `redis:7-alpine` | `127.0.0.1:6379` | `PONG` |
| PostgreSQL | `postgres:16-alpine` | `127.0.0.1:15432` | accepting connections |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` | `127.0.0.1:14333` | `SELECT 1` returned 1 |

Credentials are intentionally omitted from this report.

## Commands

Connection-string values are redacted below.

```text
dotnet restore EndpointTracker.sln --disable-parallel
dotnet build EndpointTracker.sln -c Release --no-restore --maxcpucount:1 --nodeReuse:false

ENDPOINTTRACKER_TEST_REDIS_CONNECTION_STRING=<local Redis> \
ENDPOINTTRACKER_TEST_POSTGRES_CONNECTION_STRING=<local PostgreSQL> \
ENDPOINTTRACKER_TEST_SQLSERVER_CONNECTION_STRING=<local SQL Server> \
dotnet test EndpointTracker.Tests/EndpointTracker.Tests.csproj \
  -c Release --no-build --no-restore \
  --logger 'console;verbosity=normal'

dotnet list EndpointTracker.sln package --vulnerable --include-transitive
dotnet pack EndpointTracker.AspNetCore/EndpointTracker.AspNetCore.csproj \
  -c Release --no-restore \
  -o /private/tmp/endpointtracker-production-readiness
git diff --check
```

## Exact build result

```text
EndpointTracker.AspNetCore -> EndpointTracker.AspNetCore/bin/Release/net10.0/EndpointTracker.AspNetCore.dll
EndpointTracker.Example -> EndpointTracker.Example/bin/Release/net10.0/EndpointTracker.Example.dll
EndpointTracker.Tests -> EndpointTracker.Tests/bin/Release/net10.0/EndpointTracker.Tests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Elapsed 00:00:43.77
```

## Exact test result

```text
Test run successful.
Total tests: 34
     Passed: 34
     Failed: 0
    Skipped: 0
Total time: 1.8923 seconds
```

### Passed test cases

1. `Constructor_RejectsUnsafeTableIdentifiers("Metrics;DROP_TABLE")`
2. `Constructor_RejectsUnsafeTableIdentifiers("public.Metrics")`
3. `Constructor_RejectsUnsafeTableIdentifiers("\"Metrics\"")`
4. `Constructor_RejectsUnsafeTableIdentifiers("Metrics-name")`
5. `Constructor_RejectsUnsafeTableIdentifiers("9Metrics")`
6. `Constructor_RejectsTableNamesThatLeaveNoRoomForLedgerSuffix(PostgreSQL, 56)`
7. `Constructor_RejectsTableNamesThatLeaveNoRoomForLedgerSuffix(SqlServer, 121)`
8. `FlushHitBuffer_WhenRedisOperationFails_RetainsHitsForRetry`
9. `GetUnusedEndpoints_ReturnsNullLastAccessedUtc`
10. `SqlPersistence_OversizedEndpointIsRejectedBeforeRedisFlush`
11. `AddEndpointTrackerRedis_WithSqlPersistence_ResolvesTrackerAndStore`
12. `RedisOnlyConstructor_TracksHitsWithoutSqlStore`
13. `Redis_RoundTripsBufferedHitsAndMetadata`
14. `RecordHit_NewEndpoint_FlushesMetadataAndHitInOneAtomicScript`
15. `FlushHitBuffer_WaitsForRedisAndPreservesHitsRecordedDuringFlush`
16. `AddEndpointTrackerRedis_WithoutSqlPersistence_ResolvesRedisTracker`
17. `GetMetrics_TotalRequestsMatchesEndpointTotalsIncludingExistingRedisHits`
18. `GetAllEndpointUsage_WithSqlStore_FiltersOversizedLegacyPendingRowsWithoutDroppingValidRows`
19. `GetAllEndpointUsage_WithoutSqlStore_IncludesPendingSqlBatchExactlyOnce`
20. `GetAllEndpointUsage_WhenSqlIsUnavailable_IncludesPendingRedisBatchExactlyOnce`
21. `Redis_ResetFenceDiscardsOldHitsButPreservesMetadataForNewHits`
22. `Redis_StaleResetOwnerCannotDiscardLocalOrRemoteHits`
23. `GetAllEndpointUsage_WhileFlushIsBlocked_DoesNotDoubleCountCapturedHits`
24. `GetAllEndpointUsage_SupportsCountsLargerThanInt32`
25. `SqlPersistence_RemovesOversizedExistingRedisEndpointAndFlushesValidHitsOnly`
26. `FlushHitBuffer_AfterAmbiguousFailure_RetriesSameAtomicBatchExactlyOnce`
27. `RedisCluster_ThrowsBeforeTrackerStarts`
28. `FlushHitBuffer_ZeroHitMetadataFailure_RetriesSameAtomicMetadataWrite`
29. `SqlServer_RedisFenceLossRecoversAboveDurableSqlHighWatermark`
30. `Redis_PersistenceLeasesSerializeAndRelease`
31. `PostgreSql_RedisFenceLossRecoversAboveDurableSqlHighWatermark`
32. `PostgreSql_CreatesTableAndRoundTripsMetrics`
33. `PostgreSql_ReplacedRedisOwnerIsRejectedAfterSqlFenceReservation`
34. `SqlServer_CreatesTableAndRoundTripsMetrics`

## What these tests prove

### Optional configuration and dependency injection

Redis-only mode resolves and tracks without any SQL store. SQL-enabled mode resolves both the tracker and SQL persistence store. This validates that SQL persistence remains optional.

### Redis buffering and atomicity

The tests validate live hit buffering, metadata registration, zero-hit endpoints, flush completion, retry after failures, retry after ambiguous responses, and preserving hits recorded while a flush is running. Metadata and the first hit are written atomically so a restart cannot leave an orphaned counter.

### SQL and Redis merged reads

Reads combine committed SQL rows, pending Redis-to-SQL batches, active Redis counters, and local buffered hits exactly once. The suite checks SQL outages, SQL-disabled fallback, concurrent reads during flush, totals, nullable last-access timestamps, and 64-bit hit counts.

### PostgreSQL and SQL Server persistence

Both live-provider tests cover more than their method names indicate. They validate:

- concurrent first-start schema creation;
- creation and use of the metrics, batch-ledger, and fencing-state tables;
- 64-bit counts and nullable UTC timestamps;
- additive upserts and metadata merge rules;
- duplicate batch idempotency;
- simultaneous identical batches applying only once;
- simultaneous distinct batches all applying;
- consistent SQL snapshots racing with commits;
- fenced persistence and fenced reset behavior;
- rejection of stale writers and stale resets.

### Outage and crash-window protection

Pending Redis batches remain visible when SQL is unavailable and are not counted twice. Stable batch IDs and the SQL ledger protect the SQL-commit/Redis-cleanup retry window. Durable SQL fence tokens reject workers that lost their Redis lease, even after the Redis fence key is lost.

### Multiple instances and reset safety

The suite validates distributed lease serialization, monotonically increasing fence tokens, owner replacement, stale reset rejection, local-buffer restoration after a rejected stale clear, reset-fence reconciliation on reads, and preserving the first hit accepted after a reset completes.

### Input and schema safety

Unsafe or schema-qualified table identifiers and provider-specific overlong names are rejected. SQL-backed endpoint patterns longer than 450 characters are excluded safely, including legacy Redis data, without poisoning future persistence batches.

### Deployment boundary

Standalone and Sentinel-managed Redis are supported. Redis Cluster fails fast because the implementation requires atomic multi-key Lua scripts that cannot safely span cluster hash slots.

## Vulnerability audit

```text
EndpointTracker.AspNetCore: no vulnerable packages found.
EndpointTracker.Example: no vulnerable packages found.
EndpointTracker.Tests: no vulnerable packages found.
```

The audit included direct and transitive dependencies using the current NuGet vulnerability feed.

## Package result

```text
/private/tmp/endpointtracker-production-readiness/EndpointTracker.AspNetCore.1.0.6-alpha.nupkg
/private/tmp/endpointtracker-production-readiness/EndpointTracker.AspNetCore.1.0.6-alpha.snupkg
```

## Production-readiness assessment

The SQL persistence implementation has strong correctness coverage and currently passes the release-candidate test gate. The tests do not, by themselves, justify silently changing an alpha package into a stable release.

Before publishing a non-alpha package, complete these final release gates:

1. Commit the exact tested worktree and run the same matrix in CI from that immutable commit.
2. Run a sustained load/soak test at the expected production request rate and database/Redis latency. Correctness is covered, but throughput, allocations, connection-pool behavior, and long-run memory growth are not measured by this suite.
3. Run a process-level chaos test that kills an application instance around SQL commit and Redis cleanup, then verifies exact recovery after restart. The protocol is covered by deterministic idempotency/fencing tests, but the complete hosted process has not been repeatedly killed at that boundary.
4. Confirm the supported deployment matrix: .NET version, Redis standalone/Sentinel versions, PostgreSQL versions, SQL Server versions, TLS/authentication requirements, and required SQL permissions.
5. Select and publish a stable semantic version. Because SQL persistence is a new backward-compatible feature, `1.1.0` is the recommended stable version rather than simply removing `-alpha` from `1.0.6-alpha`.

Current conclusion: **release-candidate quality; stable publication should follow the five gates above.**
