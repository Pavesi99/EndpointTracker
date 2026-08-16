using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

[assembly: InternalsVisibleTo("EndpointTracker.Tests")]

namespace EndpointTracker.AspNetCore.Services;

internal sealed record SqlPersistenceReadSnapshot(
    IReadOnlyList<EndpointUsageInfo> EndpointUsage,
    IReadOnlySet<string> ProcessedBatchIds);

internal sealed class StaleSqlPersistenceFenceException : InvalidOperationException
{
    internal StaleSqlPersistenceFenceException(long providedFenceToken, long currentFenceToken)
        : base(
            $"SQL persistence fence token {providedFenceToken} is stale; " +
            $"the current token is {currentFenceToken}.")
    {
        ProvidedFenceToken = providedFenceToken;
        CurrentFenceToken = currentFenceToken;
    }

    internal long ProvidedFenceToken { get; }

    internal long CurrentFenceToken { get; }
}

/// <summary>
/// Persists endpoint usage deltas in SQL Server or PostgreSQL.
/// </summary>
public sealed class SqlPersistenceStore
{
    private const string DefaultTableName = "EndpointTrackerMetrics";
    private const string BatchTableSuffix = "_Batches";

    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<SqlPersistenceStore> _logger;
    private readonly DbProviderFactory _factory;
    private readonly bool _isSqlServer;
    private readonly string _quotedTableName;
    private readonly string _quotedBatchTableName;
    private readonly string _quotedStateTableName;
    private readonly string _schemaLockResource;

    /// <summary>
    /// Initializes a SQL persistence store from the configured provider options.
    /// </summary>
    /// <param name="options">The endpoint tracker persistence options.</param>
    /// <param name="logger">The store logger.</param>
    public SqlPersistenceStore(EndpointTrackerOptions options, ILogger<SqlPersistenceStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (!_options.UseSqlPersistence)
            throw new InvalidOperationException("SqlPersistenceStore requires UseSqlPersistence to be enabled.");

        if (string.IsNullOrWhiteSpace(_options.SqlProvider))
            throw new InvalidOperationException("SqlProvider must be configured when UseSqlPersistence is enabled.");

        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
            throw new InvalidOperationException("SqlConnectionString must be configured when UseSqlPersistence is enabled.");

        _isSqlServer = _options.SqlProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        var isPostgres = _options.SqlProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                         || _options.SqlProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);

        if (!_isSqlServer && !isPostgres)
            throw new InvalidOperationException("Unsupported SqlProvider. Supported values are 'SqlServer' and 'PostgreSQL'.");

        var tableName = string.IsNullOrWhiteSpace(_options.SqlTableName)
            ? DefaultTableName
            : _options.SqlTableName.Trim();

        // PostgreSQL identifiers are limited to 63 bytes. Restricting identifiers to
        // ASCII and reserving room for the ledger suffix keeps both generated table
        // names portable and prevents identifiers from being injected into SQL.
        var maximumTableNameLength = _isSqlServer ? 128 - BatchTableSuffix.Length : 63 - BatchTableSuffix.Length;
        ValidateIdentifier(tableName, maximumTableNameLength);

        _factory = _isSqlServer
            ? SqlClientFactory.Instance
            : NpgsqlFactory.Instance;

        _quotedTableName = QuoteIdentifier(tableName);
        _quotedBatchTableName = QuoteIdentifier(tableName + BatchTableSuffix);
        _quotedStateTableName = QuoteIdentifier(tableName + "_State");
        _schemaLockResource = "EndpointTracker.Schema:" +
                              (_isSqlServer ? tableName.ToUpperInvariant() : tableName);
    }

    /// <summary>
    /// Creates the metrics, persistence-batch, and fencing-state tables if they do not exist.
    /// </summary>
    public void EnsureTableExists() =>
        EnsureTableExistsAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Creates the metrics, persistence-batch, and fencing-state tables if they do not exist.
    /// </summary>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // PostgreSQL takes a new snapshot for each statement at read committed, so a
        // transaction that waited for the advisory lock sees the preceding creator's
        // committed DDL. SQL Server retains serializable protection around OBJECT_ID.
        var schemaIsolationLevel = _isSqlServer ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted;
        await using var transaction = await connection
            .BeginTransactionAsync(schemaIsolationLevel, cancellationToken)
            .ConfigureAwait(false);

        await AcquireSchemaLockAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _isSqlServer ? BuildSqlServerCreateTablesSql() : BuildPostgresCreateTablesSql();
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("SQL persistence tables are ready.");
    }

    /// <summary>
    /// Adds a set of usage deltas using a generated batch identifier.
    /// </summary>
    /// <remarks>
    /// Callers that may retry a transfer should use <see cref="PersistEndpointUsageBatchAsync"/>
    /// and retain their batch identifier until Redis cleanup has completed.
    /// </remarks>
    public void PersistEndpointUsage(IEnumerable<EndpointUsageInfo>? usage)
    {
        if (usage is null)
            return;

        PersistEndpointUsageBatch(Guid.NewGuid().ToString("N"), usage);
    }

    /// <summary>
    /// Atomically adds endpoint usage deltas and records the batch as processed.
    /// </summary>
    /// <returns><see langword="true"/> when the batch was newly applied; otherwise <see langword="false"/>.</returns>
    public bool PersistEndpointUsageBatch(
        string batchId,
        IEnumerable<EndpointUsageInfo> deltaRows,
        CancellationToken cancellationToken = default) =>
        PersistEndpointUsageBatchAsync(batchId, deltaRows, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Atomically adds endpoint usage deltas and records the batch as processed.
    /// Reusing a processed batch identifier is a no-op.
    /// </summary>
    /// <returns><see langword="true"/> when the batch was newly applied; otherwise <see langword="false"/>.</returns>
    public async Task<bool> PersistEndpointUsageBatchAsync(
        string batchId,
        IEnumerable<EndpointUsageInfo> deltaRows,
        CancellationToken cancellationToken = default) =>
        await PersistEndpointUsageBatchInternalAsync(
                batchId,
                deltaRows,
                fenceToken: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<bool> PersistEndpointUsageBatchFencedAsync(
        string batchId,
        IEnumerable<EndpointUsageInfo> deltaRows,
        long fenceToken,
        CancellationToken cancellationToken = default) =>
        await PersistEndpointUsageBatchInternalAsync(
                batchId,
                deltaRows,
                fenceToken,
                cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> PersistEndpointUsageBatchInternalAsync(
        string batchId,
        IEnumerable<EndpointUsageInfo> deltaRows,
        long? fenceToken,
        CancellationToken cancellationToken)
    {
        ValidateBatchId(batchId);
        ArgumentNullException.ThrowIfNull(deltaRows);
        if (fenceToken.HasValue)
            ValidateFenceToken(fenceToken.Value);

        var normalizedRows = NormalizeAndCombineRows(deltaRows);
        var appliedUtc = DateTime.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // SQL Server needs serializable key-range locking for its update-then-insert
        // upsert. PostgreSQL's INSERT ON CONFLICT is atomic at read committed.
        var isolationLevel = _isSqlServer ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted;
        await using var transaction = await connection
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        if (fenceToken.HasValue)
        {
            await ValidateAndAdvanceFenceAsync(
                    connection,
                    transaction,
                    fenceToken.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var claimed = await TryClaimBatchAsync(
                connection,
                transaction,
                batchId,
                appliedUtc,
                cancellationToken)
            .ConfigureAwait(false);

        if (!claimed)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("SQL persistence batch {BatchId} was already processed.", batchId);
            return false;
        }

        foreach (var row in normalizedRows)
        {
            await PersistEndpointUsageInternalAsync(
                    connection,
                    transaction,
                    row,
                    appliedUtc,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Applied SQL persistence batch {BatchId} containing {RowCount} endpoint rows.",
            batchId,
            normalizedRows.Count);
        return true;
    }

    /// <summary>
    /// Gets all persisted endpoint metrics.
    /// </summary>
    public IReadOnlyList<EndpointUsageInfo> GetAllEndpointUsage() =>
        GetAllEndpointUsageAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Gets all persisted endpoint metrics.
    /// </summary>
    public async Task<IReadOnlyList<EndpointUsageInfo>> GetAllEndpointUsageAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return await ReadAllEndpointUsageAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SqlPersistenceReadSnapshot> GetEndpointUsageSnapshotAsync(
        IEnumerable<string> batchIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batchIds);
        var distinctBatchIds = batchIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(batchId => batchId, StringComparer.Ordinal)
            .ToArray();
        foreach (var batchId in distinctBatchIds)
            ValidateBatchId(batchId);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // PostgreSQL repeatable read pins one MVCC snapshot for all three reads and
        // avoids SSI serialization failures in this read-only transaction. SQL Server
        // needs serializable key-range locks so an absent ledger row cannot appear
        // between the ledger and metrics reads.
        var snapshotIsolationLevel = _isSqlServer
            ? IsolationLevel.Serializable
            : IsolationLevel.RepeatableRead;
        await using var transaction = await connection
            .BeginTransactionAsync(snapshotIsolationLevel, cancellationToken)
            .ConfigureAwait(false);

        // Fenced writers and resets lock state, then ledger, then metrics. Reading in
        // the same order avoids lock inversion while guaranteeing one database state.
        await ReadCurrentFenceAsync(
                connection,
                transaction,
                forUpdate: false,
                cancellationToken)
            .ConfigureAwait(false);

        var processedBatchIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batchId in distinctBatchIds)
        {
            if (await IsPersistenceBatchProcessedInternalAsync(
                    connection,
                    transaction,
                    batchId,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                processedBatchIds.Add(batchId);
            }
        }

        var endpointUsage = await ReadAllEndpointUsageAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SqlPersistenceReadSnapshot(endpointUsage, processedBatchIds);
    }

    /// <summary>
    /// Returns whether a persistence batch has already been committed.
    /// </summary>
    public bool IsPersistenceBatchProcessed(string batchId) =>
        IsPersistenceBatchProcessedAsync(batchId).GetAwaiter().GetResult();

    /// <summary>
    /// Returns whether a persistence batch has already been committed.
    /// </summary>
    public async Task<bool> IsPersistenceBatchProcessedAsync(
        string batchId,
        CancellationToken cancellationToken = default)
    {
        ValidateBatchId(batchId);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = BuildBatchExistsSql();
        AddParameter(command, "@BatchId", DbType.String, batchId, 450);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    internal async Task<long> GetCurrentFenceAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $@"SELECT {QuoteColumn("CurrentFence")}
FROM {_quotedStateTableName}
WHERE {QuoteColumn("StateId")} = 1;";

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            throw new InvalidOperationException(
                "The SQL persistence state row is missing. EnsureTableExistsAsync must complete before reading the fence.");
        }

        return Convert.ToInt64(result);
    }

    internal async Task<bool> ReserveFenceTokenAsync(
        long proposedFenceToken,
        CancellationToken cancellationToken = default)
    {
        ValidateFenceToken(proposedFenceToken);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var isolationLevel = _isSqlServer ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted;
        await using var transaction = await connection
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        var currentFenceToken = await ReadCurrentFenceAsync(
                connection,
                transaction,
                forUpdate: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (proposedFenceToken <= currentFenceToken)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await UpdateCurrentFenceAsync(
                connection,
                transaction,
                proposedFenceToken,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Reserved SQL persistence fence token {FenceToken}.", proposedFenceToken);
        return true;
    }

    /// <summary>
    /// Removes all persisted metrics and batch-ledger entries.
    /// </summary>
    public void ClearAll() =>
        ClearAllAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Removes all persisted metrics and batch-ledger entries atomically.
    /// </summary>
    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await ClearAllInternalAsync(fenceToken: null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task ClearAllFencedAsync(
        long fenceToken,
        CancellationToken cancellationToken = default)
    {
        ValidateFenceToken(fenceToken);
        await ClearAllInternalAsync(fenceToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClearAllInternalAsync(long? fenceToken, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var isolationLevel = _isSqlServer ? IsolationLevel.Serializable : IsolationLevel.ReadCommitted;
        await using var transaction = await connection
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        if (fenceToken.HasValue)
        {
            await ValidateAndAdvanceFenceAsync(
                    connection,
                    transaction,
                    fenceToken.Value,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {_quotedBatchTableName}; DELETE FROM {_quotedTableName};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Cleared SQL metrics and persistence batch history.");
    }

    private DbConnection CreateConnection()
    {
        var connection = _factory.CreateConnection();
        if (connection is null)
            throw new InvalidOperationException("Unable to create a database connection for SQL persistence.");

        connection.ConnectionString = _options.SqlConnectionString!;
        return connection;
    }

    private async Task AcquireSchemaLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _isSqlServer
            ? BuildSqlServerAcquireSchemaLockSql()
            : BuildPostgresAcquireSchemaLockSql();
        AddParameter(command, "@SchemaLockResource", DbType.String, _schemaLockResource, 255);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (_isSqlServer && Convert.ToInt32(result) < 0)
        {
            throw new InvalidOperationException(
                $"SQL Server could not acquire the EndpointTracker schema lock (result {result}).");
        }
    }

    private async Task ValidateAndAdvanceFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        long fenceToken,
        CancellationToken cancellationToken)
    {
        var currentFenceToken = await ReadCurrentFenceAsync(
                connection,
                transaction,
                forUpdate: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (fenceToken < currentFenceToken)
            throw new StaleSqlPersistenceFenceException(fenceToken, currentFenceToken);

        if (fenceToken == currentFenceToken)
            return;

        await UpdateCurrentFenceAsync(connection, transaction, fenceToken, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task UpdateCurrentFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        long fenceToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"UPDATE {_quotedStateTableName}
SET {QuoteColumn("CurrentFence")} = @FenceToken
WHERE {QuoteColumn("StateId")} = 1;";
        AddParameter(command, "@FenceToken", DbType.Int64, fenceToken);

        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("The SQL persistence state row is missing.");
    }

    private async Task<long> ReadCurrentFenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildReadCurrentFenceSql(forUpdate);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
            throw new InvalidOperationException("The SQL persistence state row is missing.");

        return Convert.ToInt64(result);
    }

    private async Task<IReadOnlyList<EndpointUsageInfo>> ReadAllEndpointUsageAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var results = new List<EndpointUsageInfo>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildSelectAllSql();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(ReadUsageInfo(reader));

        return results;
    }

    private async Task<bool> IsPersistenceBatchProcessedInternalAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BuildBatchExistsSql();
        AddParameter(command, "@BatchId", DbType.String, batchId, 450);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null && result is not DBNull;
    }

    private async Task<bool> TryClaimBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        string batchId,
        DateTime appliedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _isSqlServer
            ? BuildSqlServerClaimBatchSql()
            : BuildPostgresClaimBatchSql();

        AddParameter(command, "@BatchId", DbType.String, batchId, 450);
        AddTimestampParameter(command, "@AppliedUtc", appliedUtc);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task PersistEndpointUsageInternalAsync(
        DbConnection connection,
        DbTransaction transaction,
        EndpointUsageInfo usage,
        DateTime updatedUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _isSqlServer
            ? BuildSqlServerUpsertSql()
            : BuildPostgresUpsertSql();

        AddParameter(command, "@EndpointPattern", DbType.String, usage.EndpointPattern, 450);
        AddParameter(command, "@DisplayName", DbType.String, usage.DisplayName ?? (object)DBNull.Value, 1024);
        AddParameter(command, "@HttpMethod", DbType.String, usage.HttpMethod ?? (object)DBNull.Value, 50);
        AddParameter(command, "@HitCount", DbType.Int64, usage.HitCount);
        AddTimestampParameter(command, "@LastAccessedUtc", usage.LastAccessedUtc);
        AddTimestampParameter(command, "@RegisteredUtc", usage.RegisteredUtc);
        AddTimestampParameter(command, "@UpdatedUtc", updatedUtc);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        DbType type,
        object value,
        int? size = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size.HasValue)
            parameter.Size = size.Value;

        command.Parameters.Add(parameter);
    }

    private void AddTimestampParameter(DbCommand command, string name, DateTime? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value.HasValue ? NormalizeUtc(value.Value) : DBNull.Value;

        if (_isSqlServer)
        {
            parameter.DbType = DbType.DateTime2;
        }
        else if (parameter is NpgsqlParameter postgresParameter)
        {
            postgresParameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
        }
        else
        {
            parameter.DbType = DbType.DateTime;
        }

        command.Parameters.Add(parameter);
    }

    private static EndpointUsageInfo ReadUsageInfo(DbDataReader reader)
    {
        var endpointPatternOrdinal = reader.GetOrdinal("EndpointPattern");
        var displayNameOrdinal = reader.GetOrdinal("DisplayName");
        var httpMethodOrdinal = reader.GetOrdinal("HttpMethod");
        var hitCountOrdinal = reader.GetOrdinal("HitCount");
        var lastAccessedUtcOrdinal = reader.GetOrdinal("LastAccessedUtc");
        var registeredUtcOrdinal = reader.GetOrdinal("RegisteredUtc");

        return new EndpointUsageInfo
        {
            EndpointPattern = reader.GetString(endpointPatternOrdinal),
            DisplayName = reader.IsDBNull(displayNameOrdinal) ? null : reader.GetString(displayNameOrdinal),
            HttpMethod = reader.IsDBNull(httpMethodOrdinal) ? null : reader.GetString(httpMethodOrdinal),
            HitCount = reader.GetInt64(hitCountOrdinal),
            LastAccessedUtc = reader.IsDBNull(lastAccessedUtcOrdinal)
                ? null
                : NormalizeDatabaseUtc(reader.GetDateTime(lastAccessedUtcOrdinal)),
            RegisteredUtc = NormalizeDatabaseUtc(reader.GetDateTime(registeredUtcOrdinal))
        };
    }

    private static IReadOnlyList<EndpointUsageInfo> NormalizeAndCombineRows(
        IEnumerable<EndpointUsageInfo> rows)
    {
        var combined = new Dictionary<string, EndpointUsageInfo>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            ValidateUsageRow(row);

            var registeredUtc = NormalizeUtc(row.RegisteredUtc);
            var lastAccessedUtc = row.LastAccessedUtc.HasValue
                ? NormalizeUtc(row.LastAccessedUtc.Value)
                : (DateTime?)null;

            if (!combined.TryGetValue(row.EndpointPattern, out var existing))
            {
                combined.Add(row.EndpointPattern, new EndpointUsageInfo
                {
                    EndpointPattern = row.EndpointPattern,
                    DisplayName = row.DisplayName,
                    HttpMethod = row.HttpMethod,
                    HitCount = row.HitCount,
                    LastAccessedUtc = lastAccessedUtc,
                    RegisteredUtc = registeredUtc
                });
                continue;
            }

            existing.HitCount = checked(existing.HitCount + row.HitCount);
            existing.DisplayName = row.DisplayName ?? existing.DisplayName;
            existing.HttpMethod = row.HttpMethod ?? existing.HttpMethod;
            existing.RegisteredUtc = registeredUtc < existing.RegisteredUtc
                ? registeredUtc
                : existing.RegisteredUtc;
            existing.LastAccessedUtc = Latest(existing.LastAccessedUtc, lastAccessedUtc);
        }

        return combined.Values
            .OrderBy(row => row.EndpointPattern, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateUsageRow(EndpointUsageInfo usage)
    {
        if (string.IsNullOrWhiteSpace(usage.EndpointPattern))
            throw new ArgumentException("EndpointPattern cannot be empty.", nameof(usage));
        if (usage.EndpointPattern.Length > 450)
            throw new ArgumentException("EndpointPattern cannot exceed 450 characters.", nameof(usage));
        if (usage.DisplayName?.Length > 1024)
            throw new ArgumentException("DisplayName cannot exceed 1024 characters.", nameof(usage));
        if (usage.HttpMethod?.Length > 50)
            throw new ArgumentException("HttpMethod cannot exceed 50 characters.", nameof(usage));
        if (usage.HitCount < 0)
            throw new ArgumentOutOfRangeException(nameof(usage), "HitCount deltas cannot be negative.");
    }

    private static void ValidateBatchId(string batchId)
    {
        if (string.IsNullOrWhiteSpace(batchId))
            throw new ArgumentException("A persistence batch identifier is required.", nameof(batchId));
        if (batchId.Length > 450)
            throw new ArgumentException("A persistence batch identifier cannot exceed 450 characters.", nameof(batchId));
    }

    private static void ValidateFenceToken(long fenceToken)
    {
        if (fenceToken < 0)
            throw new ArgumentOutOfRangeException(nameof(fenceToken), "Fence tokens cannot be negative.");
    }

    private static void ValidateIdentifier(string identifier, int maximumLength)
    {
        if (identifier.Length == 0 || identifier.Length > maximumLength)
        {
            throw new InvalidOperationException(
                $"SqlTableName must contain between 1 and {maximumLength} characters for the selected provider.");
        }

        if (!IsAsciiLetter(identifier[0]) && identifier[0] != '_')
        {
            throw new InvalidOperationException(
                "SqlTableName must start with an ASCII letter or underscore and contain only ASCII letters, digits, and underscores.");
        }

        for (var index = 1; index < identifier.Length; index++)
        {
            var character = identifier[index];
            if (!IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
            {
                throw new InvalidOperationException(
                    "SqlTableName must start with an ASCII letter or underscore and contain only ASCII letters, digits, and underscores.");
            }
        }
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static DateTime NormalizeDatabaseUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Latest(DateTime? first, DateTime? second)
    {
        if (!first.HasValue)
            return second;
        if (!second.HasValue)
            return first;
        return first.Value >= second.Value ? first : second;
    }

    private string QuoteIdentifier(string identifier) =>
        _isSqlServer ? $"[{identifier}]" : $"\"{identifier}\"";

    private string QuoteColumn(string identifier) => QuoteIdentifier(identifier);

    private string BuildSelectAllSql() =>
        $"SELECT {QuoteColumn("EndpointPattern")}, {QuoteColumn("DisplayName")}, {QuoteColumn("HttpMethod")}, " +
        $"{QuoteColumn("HitCount")}, {QuoteColumn("LastAccessedUtc")}, {QuoteColumn("RegisteredUtc")} " +
        $"FROM {_quotedTableName};";

    private string BuildBatchExistsSql() =>
        $"SELECT 1 FROM {_quotedBatchTableName} WHERE {QuoteColumn("BatchId")} = @BatchId;";

    private static string BuildSqlServerAcquireSchemaLockSql() =>
        """
        DECLARE @LockResult int;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @SchemaLockResource,
            @LockMode = 'Exclusive',
            @LockOwner = 'Transaction',
            @LockTimeout = 30000;
        SELECT @LockResult;
        """;

    private static string BuildPostgresAcquireSchemaLockSql() =>
        "SELECT pg_advisory_xact_lock(hashtextextended(@SchemaLockResource, 0));";

    private string BuildReadCurrentFenceSql(bool forUpdate)
    {
        if (_isSqlServer)
        {
            var lockHint = forUpdate ? "UPDLOCK, HOLDLOCK" : "HOLDLOCK";
            return $@"SELECT {QuoteColumn("CurrentFence")}
FROM {_quotedStateTableName} WITH ({lockHint})
WHERE {QuoteColumn("StateId")} = 1;";
        }

        // PostgreSQL's serializable MVCC snapshot already keeps the multi-table read
        // consistent. Only mutation paths take a row lock; a read lock could fail with
        // 40001 after waiting for a concurrent fence advancement.
        var lockClause = forUpdate ? "FOR UPDATE" : string.Empty;
        return $@"SELECT {QuoteColumn("CurrentFence")}
FROM {_quotedStateTableName}
WHERE {QuoteColumn("StateId")} = 1
{lockClause};";
    }

    private string BuildSqlServerCreateTablesSql()
    {
        return $@"IF OBJECT_ID(N'{_quotedTableName}', N'U') IS NULL
BEGIN
    CREATE TABLE {_quotedTableName} (
        [EndpointPattern] nvarchar(450) NOT NULL PRIMARY KEY,
        [DisplayName] nvarchar(1024) NULL,
        [HttpMethod] nvarchar(50) NULL,
        [HitCount] bigint NOT NULL,
        [LastAccessedUtc] datetime2 NULL,
        [RegisteredUtc] datetime2 NOT NULL,
        [UpdatedUtc] datetime2 NOT NULL
    );
END;

IF OBJECT_ID(N'{_quotedBatchTableName}', N'U') IS NULL
BEGIN
    CREATE TABLE {_quotedBatchTableName} (
        [BatchId] nvarchar(450) NOT NULL PRIMARY KEY,
        [AppliedUtc] datetime2 NOT NULL
    );
END;

IF OBJECT_ID(N'{_quotedStateTableName}', N'U') IS NULL
BEGIN
    CREATE TABLE {_quotedStateTableName} (
        [StateId] tinyint NOT NULL PRIMARY KEY CHECK ([StateId] = 1),
        [CurrentFence] bigint NOT NULL
    );
END;

IF NOT EXISTS (SELECT 1 FROM {_quotedStateTableName} WHERE [StateId] = 1)
BEGIN
    INSERT INTO {_quotedStateTableName} ([StateId], [CurrentFence]) VALUES (1, 0);
END;";
    }

    private string BuildPostgresCreateTablesSql()
    {
        return $@"CREATE TABLE IF NOT EXISTS {_quotedTableName} (
    ""EndpointPattern"" text PRIMARY KEY,
    ""DisplayName"" text,
    ""HttpMethod"" text,
    ""HitCount"" bigint NOT NULL,
    ""LastAccessedUtc"" timestamp with time zone NULL,
    ""RegisteredUtc"" timestamp with time zone NOT NULL,
    ""UpdatedUtc"" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS {_quotedBatchTableName} (
    ""BatchId"" text PRIMARY KEY,
    ""AppliedUtc"" timestamp with time zone NOT NULL
);

CREATE TABLE IF NOT EXISTS {_quotedStateTableName} (
    ""StateId"" smallint PRIMARY KEY CHECK (""StateId"" = 1),
    ""CurrentFence"" bigint NOT NULL
);

INSERT INTO {_quotedStateTableName} (""StateId"", ""CurrentFence"")
VALUES (1, 0)
ON CONFLICT (""StateId"") DO NOTHING;";
    }

    private string BuildSqlServerClaimBatchSql()
    {
        return $@"INSERT INTO {_quotedBatchTableName} ([BatchId], [AppliedUtc])
SELECT @BatchId, @AppliedUtc
WHERE NOT EXISTS (
    SELECT 1
    FROM {_quotedBatchTableName} WITH (UPDLOCK, HOLDLOCK)
    WHERE [BatchId] = @BatchId
);";
    }

    private string BuildPostgresClaimBatchSql()
    {
        return $@"INSERT INTO {_quotedBatchTableName} (""BatchId"", ""AppliedUtc"")
VALUES (@BatchId, @AppliedUtc)
ON CONFLICT (""BatchId"") DO NOTHING;";
    }

    private string BuildSqlServerUpsertSql()
    {
        return $@"UPDATE {_quotedTableName} WITH (UPDLOCK, HOLDLOCK)
SET
    [DisplayName] = COALESCE(@DisplayName, [DisplayName]),
    [HttpMethod] = COALESCE(@HttpMethod, [HttpMethod]),
    [HitCount] = [HitCount] + @HitCount,
    [LastAccessedUtc] = CASE
        WHEN @LastAccessedUtc IS NULL THEN [LastAccessedUtc]
        WHEN [LastAccessedUtc] IS NULL OR [LastAccessedUtc] < @LastAccessedUtc THEN @LastAccessedUtc
        ELSE [LastAccessedUtc]
    END,
    [RegisteredUtc] = CASE
        WHEN [RegisteredUtc] > @RegisteredUtc THEN @RegisteredUtc
        ELSE [RegisteredUtc]
    END,
    [UpdatedUtc] = @UpdatedUtc
WHERE [EndpointPattern] = @EndpointPattern;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO {_quotedTableName}
        ([EndpointPattern], [DisplayName], [HttpMethod], [HitCount], [LastAccessedUtc], [RegisteredUtc], [UpdatedUtc])
    VALUES
        (@EndpointPattern, @DisplayName, @HttpMethod, @HitCount, @LastAccessedUtc, @RegisteredUtc, @UpdatedUtc);
END;";
    }

    private string BuildPostgresUpsertSql()
    {
        return $@"INSERT INTO {_quotedTableName} AS target
    (""EndpointPattern"", ""DisplayName"", ""HttpMethod"", ""HitCount"", ""LastAccessedUtc"", ""RegisteredUtc"", ""UpdatedUtc"")
VALUES
    (@EndpointPattern, @DisplayName, @HttpMethod, @HitCount, @LastAccessedUtc, @RegisteredUtc, @UpdatedUtc)
ON CONFLICT (""EndpointPattern"") DO UPDATE SET
    ""DisplayName"" = COALESCE(EXCLUDED.""DisplayName"", target.""DisplayName""),
    ""HttpMethod"" = COALESCE(EXCLUDED.""HttpMethod"", target.""HttpMethod""),
    ""HitCount"" = target.""HitCount"" + EXCLUDED.""HitCount"",
    ""LastAccessedUtc"" = CASE
        WHEN EXCLUDED.""LastAccessedUtc"" IS NULL THEN target.""LastAccessedUtc""
        WHEN target.""LastAccessedUtc"" IS NULL OR target.""LastAccessedUtc"" < EXCLUDED.""LastAccessedUtc""
            THEN EXCLUDED.""LastAccessedUtc""
        ELSE target.""LastAccessedUtc""
    END,
    ""RegisteredUtc"" = LEAST(target.""RegisteredUtc"", EXCLUDED.""RegisteredUtc""),
    ""UpdatedUtc"" = EXCLUDED.""UpdatedUtc"";";
    }
}
