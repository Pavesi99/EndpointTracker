using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using StackExchange.Redis;
using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace EndpointTracker.Tests;

public class RedisIntegrationTests
{
    private const string ConnectionStringVariable = "ENDPOINTTRACKER_TEST_REDIS_CONNECTION_STRING";

    [EnvironmentFact(ConnectionStringVariable)]
    public void Redis_RoundTripsBufferedHitsAndMetadata()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        using var connection = ConnectionMultiplexer.Connect(connectionString);
        var keyPrefix = $"endpoint-tracker-integration:{Guid.NewGuid():N}:";
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = keyPrefix
        };
        var service = CreateRedisService(connection, options);

        try
        {
            service.RegisterEndpoint("GET /integration", "Integration endpoint", "GET");
            service.RecordHit("GET /integration");
            service.RecordHit("GET /integration");
            service.FlushHitBuffer();

            var reloadedService = CreateRedisService(connection, options);
            var usage = Assert.Single(reloadedService.GetAllEndpointUsage());

            Assert.Equal(2L, usage.HitCount);
            Assert.NotNull(usage.LastAccessedUtc);
            Assert.Equal(2L, reloadedService.GetMetrics().TotalRequests);
        }
        finally
        {
            service.ClearRedisData();
        }
    }

    [EnvironmentFact(ConnectionStringVariable)]
    public async Task Redis_PersistenceLeasesSerializeAndRelease()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        using var connection = ConnectionMultiplexer.Connect(connectionString);
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = $"endpoint-tracker-lease:{Guid.NewGuid():N}:"
        };
        var firstService = CreateRedisService(connection, options);
        var secondService = CreateRedisService(connection, options);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var firstLease = await firstService.AcquirePersistenceLeaseAsync(timeout.Token);
        var firstFence = firstLease.FenceToken;
        Task<RedisEndpointTrackerService.PersistenceLease>? secondLeaseTask = null;

        try
        {
            secondLeaseTask = secondService.AcquirePersistenceLeaseAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            Assert.False(secondLeaseTask.IsCompleted, "A second persistence lease entered before the first was released.");
        }
        finally
        {
            await firstLease.DisposeAsync();
        }

        await using (var secondLease = await secondLeaseTask.WaitAsync(
                         TimeSpan.FromSeconds(3),
                         cancellationToken))
        {
            secondLease.ThrowIfLeaseLost();
            Assert.True(secondLease.FenceToken > firstFence);
            firstFence = secondLease.FenceToken;
        }

        await using (var thirdLease = await firstService
                         .AcquirePersistenceLeaseAsync(timeout.Token)
                         .WaitAsync(TimeSpan.FromSeconds(3), cancellationToken))
        {
            thirdLease.ThrowIfLeaseLost();
            Assert.True(thirdLease.FenceToken > firstFence);
        }
    }

    [EnvironmentFact(ConnectionStringVariable)]
    public void Redis_ResetFenceDiscardsOldHitsButPreservesMetadataForNewHits()
    {
        const string endpointPattern = "GET /reset-fence";
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        using var connection = ConnectionMultiplexer.Connect(connectionString);
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = $"endpoint-tracker-reset:{Guid.NewGuid():N}:"
        };
        var staleWriter = CreateRedisService(connection, options);
        var resetter = CreateRedisService(connection, options);

        try
        {
            staleWriter.RegisterEndpoint(endpointPattern, "Reset fence endpoint", "GET");
            staleWriter.RecordHit(endpointPattern);

            resetter.ClearRedisData();
            var immediatelyAfterReset = Assert.Single(staleWriter.GetAllEndpointUsage());
            Assert.Equal(0L, immediatelyAfterReset.HitCount);
            // A hit accepted after ClearRedisData returns must be assigned to the new
            // reset epoch rather than being discarded with the pre-reset buffered hit.
            staleWriter.RecordHit(endpointPattern);
            staleWriter.FlushHitBuffer();

            var afterReset = CreateRedisService(connection, options);
            var resetUsage = Assert.Single(afterReset.GetAllEndpointUsage());
            Assert.Equal(endpointPattern, resetUsage.EndpointPattern);
            Assert.Equal("Reset fence endpoint", resetUsage.DisplayName);
            Assert.Equal(1L, resetUsage.HitCount);
        }
        finally
        {
            resetter.ClearRedisData();
        }
    }

    [EnvironmentFact(ConnectionStringVariable)]
    public async Task Redis_StaleResetOwnerCannotDiscardLocalOrRemoteHits()
    {
        const string endpointPattern = "GET /stale-reset-owner";
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)!;
        using var connection = ConnectionMultiplexer.Connect(connectionString);
        var keyPrefix = $"endpoint-tracker-stale-reset:{Guid.NewGuid():N}:";
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = connection,
            RedisKeyPrefix = keyPrefix
        };
        var service = CreateRedisService(connection, options);
        RedisEndpointTrackerService.PersistenceLease? staleLease = null;
        RedisEndpointTrackerService.PersistenceLease? replacementLease = null;

        try
        {
            service.RegisterEndpoint(endpointPattern, "Stale reset owner", "GET");
            service.RecordHit(endpointPattern);
            service.FlushHitBuffer();
            service.RecordHit(endpointPattern);

            staleLease = await service.AcquirePersistenceLeaseAsync(TestContext.Current.CancellationToken);
            await connection.GetDatabase().KeyDeleteAsync(keyPrefix + "sql-persistence:lock");
            replacementLease = await service.AcquirePersistenceLeaseAsync(TestContext.Current.CancellationToken);

            var clearMethod = typeof(RedisEndpointTrackerService).GetMethod(
                "ClearRedisDataAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ClearRedisDataAsync test seam was not found.");
            var clearTask = (Task)clearMethod.Invoke(
                service,
                [staleLease, TestContext.Current.CancellationToken])!;

            await Assert.ThrowsAsync<InvalidOperationException>(() => clearTask);

            var usage = Assert.Single(service.GetAllEndpointUsage());
            Assert.Equal(endpointPattern, usage.EndpointPattern);
            Assert.Equal(2L, usage.HitCount);

            var reloaded = CreateRedisService(connection, options);
            Assert.Equal(1L, Assert.Single(reloaded.GetAllEndpointUsage()).HitCount);
        }
        finally
        {
            if (replacementLease != null)
                await replacementLease.DisposeAsync();
            if (staleLease != null)
                await staleLease.DisposeAsync();

            service.ClearRedisData();
            await connection.GetDatabase().KeyDeleteAsync(
            [
                keyPrefix + "sql-persistence:lock",
                keyPrefix + "sql-persistence:fence",
                keyPrefix + "reset:fence",
                keyPrefix + "sql-persistence:generation"
            ]);
        }
    }

    private static RedisEndpointTrackerService CreateRedisService(
        IConnectionMultiplexer connection,
        EndpointTrackerOptions options)
    {
        return new RedisEndpointTrackerService(
            connection,
            options,
            NullLogger<RedisEndpointTrackerService>.Instance);
    }
}

public class SqlPersistenceStoreIntegrationTests
{
    private const string RedisConnectionStringVariable =
        "ENDPOINTTRACKER_TEST_REDIS_CONNECTION_STRING";
    private const string PostgreSqlConnectionStringVariable =
        "ENDPOINTTRACKER_TEST_POSTGRES_CONNECTION_STRING";
    private const string SqlServerConnectionStringVariable =
        "ENDPOINTTRACKER_TEST_SQLSERVER_CONNECTION_STRING";

    [EnvironmentFact(PostgreSqlConnectionStringVariable)]
    public async Task PostgreSql_CreatesTableAndRoundTripsMetrics()
    {
        await RoundTripMetrics("PostgreSQL", PostgreSqlConnectionStringVariable);
    }

    [EnvironmentFact(SqlServerConnectionStringVariable)]
    public async Task SqlServer_CreatesTableAndRoundTripsMetrics()
    {
        await RoundTripMetrics("SqlServer", SqlServerConnectionStringVariable);
    }

    [EnvironmentFact(PostgreSqlConnectionStringVariable, RedisConnectionStringVariable)]
    public async Task PostgreSql_RedisFenceLossRecoversAboveDurableSqlHighWatermark()
    {
        await VerifyRedisFenceRecoveryAsync("PostgreSQL", PostgreSqlConnectionStringVariable);
    }

    [EnvironmentFact(SqlServerConnectionStringVariable, RedisConnectionStringVariable)]
    public async Task SqlServer_RedisFenceLossRecoversAboveDurableSqlHighWatermark()
    {
        await VerifyRedisFenceRecoveryAsync("SqlServer", SqlServerConnectionStringVariable);
    }

    [EnvironmentFact(PostgreSqlConnectionStringVariable, RedisConnectionStringVariable)]
    public async Task PostgreSql_ReplacedRedisOwnerIsRejectedAfterSqlFenceReservation()
    {
        var sqlConnectionString = Environment.GetEnvironmentVariable(PostgreSqlConnectionStringVariable)!;
        var redisConnectionString = Environment.GetEnvironmentVariable(RedisConnectionStringVariable)!;
        var tableName = $"EndpointTrackerReserve{Guid.NewGuid():N}";
        var keyPrefix = $"endpoint-tracker-reserve:{Guid.NewGuid():N}:";
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = null,
            RedisKeyPrefix = keyPrefix,
            UseSqlPersistence = true,
            SqlProvider = "PostgreSQL",
            SqlConnectionString = sqlConnectionString,
            SqlTableName = tableName
        };
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        using var redisConnection = ConnectionMultiplexer.Connect(redisConnectionString);
        options.RedisConnection = redisConnection;
        var service = new RedisEndpointTrackerService(
            redisConnection,
            options,
            store,
            NullLogger<RedisEndpointTrackerService>.Instance);
        var redisDatabase = redisConnection.GetDatabase();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        RedisEndpointTrackerService.PersistenceLease? replacementLease = null;
        RedisEndpointTrackerService.PersistenceLease? recoveredLease = null;
        Task<RedisEndpointTrackerService.PersistenceLease>? acquisitionTask = null;
        var blockerCommitted = false;

        await DropTablesAsync("PostgreSQL", sqlConnectionString, tableName);
        await store.EnsureTableExistsAsync(timeout.Token);
        await using var blockerConnection = new NpgsqlConnection(sqlConnectionString);
        await blockerConnection.OpenAsync(timeout.Token);
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync(timeout.Token);
        await using (var blockerCommand = blockerConnection.CreateCommand())
        {
            blockerCommand.Transaction = blockerTransaction;
            blockerCommand.CommandText =
                $"SELECT \"CurrentFence\" FROM \"{tableName}_State\" WHERE \"StateId\" = 1 FOR UPDATE;";
            await blockerCommand.ExecuteScalarAsync(timeout.Token);
        }

        try
        {
            acquisitionTask = service.AcquireSqlPersistenceLeaseAsync(timeout.Token);
            var lockKey = (RedisKey)(keyPrefix + "sql-persistence:lock");
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while ((await redisDatabase.StringGetAsync(lockKey)).IsNull)
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("The first Redis lease was not acquired before the test deadline.");
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }

            await redisDatabase.KeyDeleteAsync(lockKey);
            replacementLease = await service.AcquirePersistenceLeaseAsync(timeout.Token);
            var replacementFence = replacementLease.FenceToken;

            await blockerTransaction.CommitAsync(timeout.Token);
            blockerCommitted = true;
            await Task.Delay(TimeSpan.FromMilliseconds(150), timeout.Token);
            Assert.False(
                acquisitionTask.IsCompleted,
                "A SQL-capable lease returned after its Redis owner had been replaced.");

            await replacementLease.DisposeAsync();
            replacementLease = null;
            recoveredLease = await acquisitionTask.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token);

            Assert.True(recoveredLease.FenceToken > replacementFence);
            Assert.Equal(
                recoveredLease.FenceToken,
                await store.GetCurrentFenceAsync(timeout.Token));
        }
        finally
        {
            if (!blockerCommitted)
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            if (replacementLease != null)
                await replacementLease.DisposeAsync();
            if (recoveredLease != null)
                await recoveredLease.DisposeAsync();

            timeout.Cancel();
            if (acquisitionTask != null && !acquisitionTask.IsCompleted)
            {
                try
                {
                    await acquisitionTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cleanup cancels an incomplete acquisition.
                }
            }

            await redisDatabase.KeyDeleteAsync(
            [
                keyPrefix + "sql-persistence:lock",
                keyPrefix + "sql-persistence:fence"
            ]);
            await DropTablesAsync("PostgreSQL", sqlConnectionString, tableName);
        }
    }

    private static async Task RoundTripMetrics(string provider, string connectionStringVariable)
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionStringVariable)!;
        await VerifyConcurrentFirstCreationAsync(provider, connectionString);
        await VerifyFencingAsync(provider, connectionString);

        var options = new EndpointTrackerOptions
        {
            UseSqlPersistence = true,
            SqlProvider = provider,
            SqlConnectionString = connectionString,
            SqlTableName = "EndpointTrackerProviderIntegration"
        };
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var tableCreated = false;

        try
        {
            store.EnsureTableExists();
            tableCreated = true;
            store.ClearAll();

            var registeredUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var lastAccessedUtc = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            var largeCount = (long)int.MaxValue + 42;

            var batchId = $"provider-integration-{Guid.NewGuid():N}";
            EndpointUsageInfo[] metrics =
            [
                new EndpointUsageInfo
                {
                    EndpointPattern = "GET /large-count",
                    DisplayName = "Large count",
                    HttpMethod = "GET",
                    HitCount = largeCount,
                    LastAccessedUtc = lastAccessedUtc,
                    RegisteredUtc = registeredUtc
                },
                new EndpointUsageInfo
                {
                    EndpointPattern = "POST /never-accessed",
                    DisplayName = "Never accessed",
                    HttpMethod = "POST",
                    HitCount = 0,
                    LastAccessedUtc = null,
                    RegisteredUtc = registeredUtc
                }
            ];

            var applied = await store.PersistEndpointUsageBatchAsync(batchId, metrics);
            var duplicateApplied = await store.PersistEndpointUsageBatchAsync(batchId, metrics);
            var laterAccessedUtc = lastAccessedUtc.AddMinutes(1);
            var nextBatchApplied = await store.PersistEndpointUsageBatchAsync(
                $"provider-integration-{Guid.NewGuid():N}",
                [
                    new EndpointUsageInfo
                    {
                        EndpointPattern = "GET /large-count",
                        DisplayName = null,
                        HttpMethod = null,
                        HitCount = 3,
                        LastAccessedUtc = laterAccessedUtc,
                        RegisteredUtc = registeredUtc.AddDays(1)
                    }
                ]);
            var concurrentBatchId = $"provider-integration-{Guid.NewGuid():N}";
            EndpointUsageInfo[] concurrentBatchMetrics =
            [
                new EndpointUsageInfo
                {
                    EndpointPattern = "GET /large-count",
                    HitCount = 7,
                    RegisteredUtc = registeredUtc.AddDays(1)
                }
            ];
            var sameBatchResults = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => store.PersistEndpointUsageBatchAsync(
                    concurrentBatchId,
                    concurrentBatchMetrics,
                    TestContext.Current.CancellationToken)));
            var distinctBatchResults = await Task.WhenAll(
                Enumerable.Range(0, 4).Select(_ => store.PersistEndpointUsageBatchAsync(
                    $"provider-integration-{Guid.NewGuid():N}",
                    [
                        new EndpointUsageInfo
                        {
                            EndpointPattern = "GET /large-count",
                            HitCount = 2,
                            RegisteredUtc = registeredUtc.AddDays(1)
                        }
                    ],
                    TestContext.Current.CancellationToken)));

            var expectedLargeCount = largeCount + 18;
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var raceBatchId = $"provider-race-{Guid.NewGuid():N}";
                var persistTask = store.PersistEndpointUsageBatchAsync(
                    raceBatchId,
                    [
                        new EndpointUsageInfo
                        {
                            EndpointPattern = "GET /large-count",
                            HitCount = 1,
                            RegisteredUtc = registeredUtc.AddDays(1)
                        }
                    ],
                    TestContext.Current.CancellationToken);
                var snapshotTask = store.GetEndpointUsageSnapshotAsync(
                    [raceBatchId],
                    TestContext.Current.CancellationToken);

                await Task.WhenAll(persistTask, snapshotTask);
                var snapshot = await snapshotTask;
                var snapshotCount = Assert.Single(
                    snapshot.EndpointUsage,
                    row => row.EndpointPattern == "GET /large-count").HitCount;
                if (snapshot.ProcessedBatchIds.Contains(raceBatchId))
                    Assert.Equal(expectedLargeCount + 1, snapshotCount);
                else
                    Assert.Equal(expectedLargeCount, snapshotCount);

                expectedLargeCount++;
            }

            var knownAndUnknownSnapshot = await store.GetEndpointUsageSnapshotAsync(
                [batchId, "provider-integration-not-processed"],
                TestContext.Current.CancellationToken);

            var actual = store.GetAllEndpointUsage()
                .OrderBy(item => item.EndpointPattern, StringComparer.Ordinal)
                .ToList();

            Assert.True(applied);
            Assert.False(duplicateApplied);
            Assert.True(nextBatchApplied);
            Assert.Single(sameBatchResults, result => result);
            Assert.All(distinctBatchResults, Assert.True);
            Assert.True(store.IsPersistenceBatchProcessed(batchId));
            Assert.True(store.IsPersistenceBatchProcessed(concurrentBatchId));
            Assert.Contains(batchId, knownAndUnknownSnapshot.ProcessedBatchIds);
            Assert.DoesNotContain("provider-integration-not-processed", knownAndUnknownSnapshot.ProcessedBatchIds);
            Assert.Equal(2, actual.Count);
            Assert.Equal(expectedLargeCount, actual[0].HitCount);
            Assert.Equal(laterAccessedUtc, actual[0].LastAccessedUtc);
            Assert.Equal(registeredUtc, actual[0].RegisteredUtc);
            Assert.Equal("Large count", actual[0].DisplayName);
            Assert.Equal("GET", actual[0].HttpMethod);
            Assert.Equal(0L, actual[1].HitCount);
            Assert.Null(actual[1].LastAccessedUtc);
        }
        finally
        {
            if (tableCreated)
                store.ClearAll();
        }
    }

    private static async Task VerifyRedisFenceRecoveryAsync(
        string provider,
        string sqlConnectionStringVariable)
    {
        const long durableHighWatermark = 50;
        var sqlConnectionString = Environment.GetEnvironmentVariable(sqlConnectionStringVariable)!;
        var redisConnectionString = Environment.GetEnvironmentVariable(RedisConnectionStringVariable)!;
        var tableName = $"EndpointTrackerRecover{Guid.NewGuid():N}";
        var keyPrefix = $"endpoint-tracker-recover:{Guid.NewGuid():N}:";
        using var redisConnection = ConnectionMultiplexer.Connect(redisConnectionString);
        var options = new EndpointTrackerOptions
        {
            UseRedis = true,
            RedisConnection = redisConnection,
            RedisKeyPrefix = keyPrefix,
            UseSqlPersistence = true,
            SqlProvider = provider,
            SqlConnectionString = sqlConnectionString,
            SqlTableName = tableName
        };
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var service = new RedisEndpointTrackerService(
            redisConnection,
            options,
            store,
            NullLogger<RedisEndpointTrackerService>.Instance);
        var redisDatabase = redisConnection.GetDatabase();

        await DropTablesAsync(provider, sqlConnectionString, tableName);
        try
        {
            await store.EnsureTableExistsAsync(TestContext.Current.CancellationToken);
            Assert.True(await store.ReserveFenceTokenAsync(
                durableHighWatermark,
                TestContext.Current.CancellationToken));

            await redisDatabase.KeyDeleteAsync(
            [
                keyPrefix + "sql-persistence:lock",
                keyPrefix + "sql-persistence:fence"
            ]);

            await using var lease = await service.AcquireSqlPersistenceLeaseAsync(
                TestContext.Current.CancellationToken);

            Assert.True(lease.FenceToken > durableHighWatermark);
            Assert.Equal(
                lease.FenceToken,
                (long)await redisDatabase.StringGetAsync(keyPrefix + "sql-persistence:fence"));
            Assert.Equal(
                lease.FenceToken,
                await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));

            var staleWrite = await Assert.ThrowsAsync<StaleSqlPersistenceFenceException>(() =>
                store.PersistEndpointUsageBatchFencedAsync(
                    $"stale-after-redis-loss-{Guid.NewGuid():N}",
                    [
                        new EndpointUsageInfo
                        {
                            EndpointPattern = "GET /stale-after-redis-loss",
                            HitCount = 1,
                            RegisteredUtc = DateTime.UtcNow
                        }
                    ],
                    durableHighWatermark,
                    TestContext.Current.CancellationToken));
            Assert.Equal(durableHighWatermark, staleWrite.ProvidedFenceToken);
            Assert.Equal(lease.FenceToken, staleWrite.CurrentFenceToken);
        }
        finally
        {
            await redisDatabase.KeyDeleteAsync(
            [
                keyPrefix + "sql-persistence:lock",
                keyPrefix + "sql-persistence:fence"
            ]);
            await DropTablesAsync(provider, sqlConnectionString, tableName);
        }
    }

    private static async Task VerifyConcurrentFirstCreationAsync(string provider, string connectionString)
    {
        var tableName = $"EndpointTrackerCreate{Guid.NewGuid():N}";
        var options = new EndpointTrackerOptions
        {
            UseSqlPersistence = true,
            SqlProvider = provider,
            SqlConnectionString = connectionString,
            SqlTableName = tableName
        };

        await DropTablesAsync(provider, connectionString, tableName);
        try
        {
            var stores = Enumerable.Range(0, 8)
                .Select(_ => new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance))
                .ToArray();

            await Task.WhenAll(stores.Select(store =>
                store.EnsureTableExistsAsync(TestContext.Current.CancellationToken)));

            var applied = await stores[0].PersistEndpointUsageBatchAsync(
                $"concurrent-create-{Guid.NewGuid():N}",
                [
                    new EndpointUsageInfo
                    {
                        EndpointPattern = "GET /concurrent-create",
                        HitCount = 1,
                        RegisteredUtc = DateTime.UtcNow
                    }
                ],
                TestContext.Current.CancellationToken);

            Assert.True(applied);
            Assert.Equal(1L, Assert.Single(stores[1].GetAllEndpointUsage()).HitCount);
        }
        finally
        {
            await DropTablesAsync(provider, connectionString, tableName);
        }
    }

    private static async Task VerifyFencingAsync(string provider, string connectionString)
    {
        var tableName = $"EndpointTrackerFence{Guid.NewGuid():N}";
        var options = new EndpointTrackerOptions
        {
            UseSqlPersistence = true,
            SqlProvider = provider,
            SqlConnectionString = connectionString,
            SqlTableName = tableName
        };
        var store = new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance);
        var registeredUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        await DropTablesAsync(provider, connectionString, tableName);
        try
        {
            await store.EnsureTableExistsAsync(TestContext.Current.CancellationToken);
            Assert.Equal(0, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));
            Assert.True(await store.ReserveFenceTokenAsync(1, TestContext.Current.CancellationToken));
            Assert.False(await store.ReserveFenceTokenAsync(1, TestContext.Current.CancellationToken));
            Assert.False(await store.ReserveFenceTokenAsync(0, TestContext.Current.CancellationToken));
            Assert.Equal(1, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));

            var staleWriterTask = CaptureFenceAttemptAsync(1, () => store.PersistEndpointUsageBatchFencedAsync(
                $"fenced-writer-{Guid.NewGuid():N}",
                [
                    new EndpointUsageInfo
                    {
                        EndpointPattern = "GET /fenced",
                        HitCount = 1,
                        RegisteredUtc = registeredUtc
                    }
                ],
                fenceToken: 1,
                TestContext.Current.CancellationToken));
            var resetTask = store.ClearAllFencedAsync(2, TestContext.Current.CancellationToken);

            await Task.WhenAll(staleWriterTask, resetTask);
            var staleWriterAttempt = await staleWriterTask;
            if (staleWriterAttempt.Error is not null)
            {
                Assert.Equal(1, staleWriterAttempt.Error.ProvidedFenceToken);
                Assert.True(staleWriterAttempt.Error.CurrentFenceToken >= 2);
            }
            else
            {
                Assert.True(staleWriterAttempt.Applied);
            }
            Assert.Empty(store.GetAllEndpointUsage());
            Assert.Equal(2, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));

            var explicitlyStale = await Assert.ThrowsAsync<StaleSqlPersistenceFenceException>(() =>
                store.PersistEndpointUsageBatchFencedAsync(
                    $"fenced-stale-{Guid.NewGuid():N}",
                    [
                        new EndpointUsageInfo
                        {
                            EndpointPattern = "GET /fenced",
                            HitCount = 100,
                            RegisteredUtc = registeredUtc
                        }
                    ],
                    fenceToken: 1,
                    TestContext.Current.CancellationToken));
            Assert.Equal(1, explicitlyStale.ProvidedFenceToken);
            Assert.Equal(2, explicitlyStale.CurrentFenceToken);

            var concurrentAttempts = await Task.WhenAll(
                Enumerable.Range(3, 8)
                    .Reverse()
                    .Select(fenceToken => CaptureFenceAttemptAsync(fenceToken, () =>
                        store.PersistEndpointUsageBatchFencedAsync(
                            $"fenced-concurrent-{fenceToken}-{Guid.NewGuid():N}",
                            [
                                new EndpointUsageInfo
                                {
                                    EndpointPattern = "GET /fenced",
                                    HitCount = 1,
                                    RegisteredUtc = registeredUtc
                                }
                            ],
                            fenceToken,
                            TestContext.Current.CancellationToken))));

            Assert.Contains(concurrentAttempts, attempt => attempt.FenceToken == 10 && attempt.Applied);
            Assert.All(
                concurrentAttempts.Where(attempt => attempt.Error is not null),
                attempt => Assert.True(attempt.Error!.CurrentFenceToken > attempt.Error.ProvidedFenceToken));
            var acceptedCount = concurrentAttempts.Count(attempt => attempt.Applied);
            Assert.Equal(acceptedCount, Assert.Single(store.GetAllEndpointUsage()).HitCount);
            Assert.Equal(10, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));

            var staleReset = await Assert.ThrowsAsync<StaleSqlPersistenceFenceException>(() =>
                store.ClearAllFencedAsync(9, TestContext.Current.CancellationToken));
            Assert.Equal(9, staleReset.ProvidedFenceToken);
            Assert.Equal(10, staleReset.CurrentFenceToken);
            Assert.Equal(acceptedCount, Assert.Single(store.GetAllEndpointUsage()).HitCount);
            Assert.Equal(10, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));

            await store.ClearAllFencedAsync(11, TestContext.Current.CancellationToken);
            Assert.Empty(store.GetAllEndpointUsage());
            Assert.Equal(11, await store.GetCurrentFenceAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await DropTablesAsync(provider, connectionString, tableName);
        }
    }

    private static async Task<FenceAttempt> CaptureFenceAttemptAsync(long fenceToken, Func<Task<bool>> action)
    {
        try
        {
            return new FenceAttempt(fenceToken, await action(), null);
        }
        catch (StaleSqlPersistenceFenceException exception)
        {
            return new FenceAttempt(fenceToken, false, exception);
        }
    }

    private static async Task DropTablesAsync(string provider, string connectionString, string tableName)
    {
        await using DbConnection connection = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? new SqlConnection(connectionString)
            : new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        var quotedTableName = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? $"[{tableName}]"
            : $"\"{tableName}\"";
        var quotedBatchTableName = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? $"[{tableName}_Batches]"
            : $"\"{tableName}_Batches\"";
        var quotedStateTableName = provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase)
            ? $"[{tableName}_State]"
            : $"\"{tableName}_State\"";

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP TABLE IF EXISTS {quotedStateTableName}; " +
            $"DROP TABLE IF EXISTS {quotedBatchTableName}; " +
            $"DROP TABLE IF EXISTS {quotedTableName};";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed record FenceAttempt(
        long FenceToken,
        bool Applied,
        StaleSqlPersistenceFenceException? Error);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(
        string environmentVariable,
        string? secondEnvironmentVariable = null,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        var missingVariables = new[] { environmentVariable, secondEnvironmentVariable }
            .Where(variable => !string.IsNullOrWhiteSpace(variable))
            .Where(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable!)))
            .ToArray();
        if (missingVariables.Length > 0)
        {
            Skip = $"Set {string.Join(" and ", missingVariables)} to run this integration test.";
        }
    }
}
