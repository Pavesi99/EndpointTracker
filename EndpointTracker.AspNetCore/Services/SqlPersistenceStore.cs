using System.Data;
using System.Data.Common;
using EndpointTracker.AspNetCore.Models;
using EndpointTracker.AspNetCore.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointTracker.AspNetCore.Services;

public sealed class SqlPersistenceStore
{
    private readonly EndpointTrackerOptions _options;
    private readonly ILogger<SqlPersistenceStore> _logger;
    private readonly DbProviderFactory _factory;
    private readonly bool _isSqlServer;
    private readonly string _quotedTableName;

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

        var normalizedTableName = string.IsNullOrWhiteSpace(_options.SqlTableName)
            ? "EndpointTrackerMetrics"
            : _options.SqlTableName.Trim();

        if (normalizedTableName.Length == 0)
            normalizedTableName = "EndpointTrackerMetrics";

        _isSqlServer = _options.SqlProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);
        var isPostgres = _options.SqlProvider.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase)
                         || _options.SqlProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase);

        if (!_isSqlServer && !isPostgres)
            throw new InvalidOperationException("Unsupported SqlProvider. Supported values are 'SqlServer' and 'PostgreSQL'.");

        _factory = _isSqlServer
            ? SqlClientFactory.Instance
            : (DbProviderFactory)NpgsqlFactory.Instance;

        _quotedTableName = _isSqlServer
            ? $"[{normalizedTableName}]"
            : $"\"{normalizedTableName}\"";
    }

    public void EnsureTableExists()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = _isSqlServer ? BuildSqlServerCreateTableSql() : BuildPostgresCreateTableSql();
        command.ExecuteNonQuery();
    }

    public void PersistEndpointUsage(IEnumerable<EndpointUsageInfo> usage)
    {
        if (usage == null)
            return;

        using var connection = CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();
        foreach (var info in usage)
        {
            PersistEndpointUsageInternal(connection, transaction, info);
        }

        transaction.Commit();
    }

    public IReadOnlyList<EndpointUsageInfo> GetAllEndpointUsage()
    {
        var results = new List<EndpointUsageInfo>();

        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = BuildSelectAllSql();

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadUsageInfo(reader));
        }

        return results;
    }

    public void ClearAll()
    {
        using var connection = CreateConnection();
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_quotedTableName};";
        command.ExecuteNonQuery();
    }

    private DbConnection CreateConnection()
    {
        var connection = _factory.CreateConnection();
        if (connection == null)
            throw new InvalidOperationException("Unable to create a database connection for SQL persistence.");

        connection.ConnectionString = _options.SqlConnectionString!;
        return connection;
    }

    private void PersistEndpointUsageInternal(DbConnection connection, DbTransaction transaction, EndpointUsageInfo usage)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = _isSqlServer
            ? BuildSqlServerUpsertSql()
            : BuildPostgresUpsertSql();

        AddParameter(command, "@EndpointPattern", DbType.String, usage.EndpointPattern);
        AddParameter(command, "@DisplayName", DbType.String, usage.DisplayName ?? (object)DBNull.Value);
        AddParameter(command, "@HttpMethod", DbType.String, usage.HttpMethod ?? (object)DBNull.Value);
        AddParameter(command, "@HitCount", DbType.Int64, usage.HitCount);
        AddParameter(command, "@LastAccessedUtc", DbType.DateTime, usage.LastAccessedUtc.HasValue ? usage.LastAccessedUtc.Value.ToUniversalTime() : (object)DBNull.Value);
        AddParameter(command, "@RegisteredUtc", DbType.DateTime, usage.RegisteredUtc.ToUniversalTime());
        AddParameter(command, "@UpdatedUtc", DbType.DateTime, DateTime.UtcNow);

        command.ExecuteNonQuery();
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static EndpointUsageInfo ReadUsageInfo(DbDataReader reader)
    {
        return new EndpointUsageInfo
        {
            EndpointPattern = reader.GetString(reader.GetOrdinal("EndpointPattern")),
            DisplayName = reader.IsDBNull(reader.GetOrdinal("DisplayName")) ? null : reader.GetString(reader.GetOrdinal("DisplayName")),
            HttpMethod = reader.IsDBNull(reader.GetOrdinal("HttpMethod")) ? null : reader.GetString(reader.GetOrdinal("HttpMethod")),
            HitCount = reader.GetInt32(reader.GetOrdinal("HitCount")),
            LastAccessedUtc = reader.IsDBNull(reader.GetOrdinal("LastAccessedUtc")) ? null : reader.GetDateTime(reader.GetOrdinal("LastAccessedUtc")),
            RegisteredUtc = reader.GetDateTime(reader.GetOrdinal("RegisteredUtc"))
        };
    }

    private string BuildSelectAllSql()
    {
        return $"SELECT EndpointPattern, DisplayName, HttpMethod, HitCount, LastAccessedUtc, RegisteredUtc FROM {_quotedTableName};";
    }

    private string BuildSqlServerCreateTableSql()
    {
        return $@"IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'{_quotedTableName}') AND type in (N'U'))
BEGIN
    CREATE TABLE {_quotedTableName} (
        EndpointPattern nvarchar(450) NOT NULL PRIMARY KEY,
        DisplayName nvarchar(1024) NULL,
        HttpMethod nvarchar(50) NULL,
        HitCount bigint NOT NULL,
        LastAccessedUtc datetime2 NULL,
        RegisteredUtc datetime2 NOT NULL,
        UpdatedUtc datetime2 NOT NULL
    );
END";
    }

    private string BuildPostgresCreateTableSql()
    {
        return $@"CREATE TABLE IF NOT EXISTS {_quotedTableName} (
    ""EndpointPattern"" text PRIMARY KEY,
    ""DisplayName"" text,
    ""HttpMethod"" text,
    ""HitCount"" bigint NOT NULL,
    ""LastAccessedUtc"" timestamp with time zone NULL,
    ""RegisteredUtc"" timestamp with time zone NOT NULL,
    ""UpdatedUtc"" timestamp with time zone NOT NULL
);";
    }

    private string BuildSqlServerUpsertSql()
    {
        return $@"MERGE INTO {_quotedTableName} AS target
USING (VALUES (@EndpointPattern, @DisplayName, @HttpMethod, @HitCount, @LastAccessedUtc, @RegisteredUtc, @UpdatedUtc)) AS source (EndpointPattern, DisplayName, HttpMethod, HitCount, LastAccessedUtc, RegisteredUtc, UpdatedUtc)
    ON target.EndpointPattern = source.EndpointPattern
WHEN MATCHED THEN
    UPDATE SET
        DisplayName = source.DisplayName,
        HttpMethod = source.HttpMethod,
        HitCount = source.HitCount,
        LastAccessedUtc = source.LastAccessedUtc,
        RegisteredUtc = source.RegisteredUtc,
        UpdatedUtc = source.UpdatedUtc
WHEN NOT MATCHED THEN
    INSERT (EndpointPattern, DisplayName, HttpMethod, HitCount, LastAccessedUtc, RegisteredUtc, UpdatedUtc)
    VALUES (source.EndpointPattern, source.DisplayName, source.HttpMethod, source.HitCount, source.LastAccessedUtc, source.RegisteredUtc, source.UpdatedUtc);";
    }

    private string BuildPostgresUpsertSql()
    {
        return $@"INSERT INTO {_quotedTableName} (""EndpointPattern"", ""DisplayName"", ""HttpMethod"", ""HitCount"", ""LastAccessedUtc"", ""RegisteredUtc"", ""UpdatedUtc"")
VALUES (@EndpointPattern, @DisplayName, @HttpMethod, @HitCount, @LastAccessedUtc, @RegisteredUtc, @UpdatedUtc)
ON CONFLICT (""EndpointPattern"") DO UPDATE SET
    ""DisplayName"" = EXCLUDED.""DisplayName"",
    ""HttpMethod"" = EXCLUDED.""HttpMethod"",
    ""HitCount"" = EXCLUDED.""HitCount"",
    ""LastAccessedUtc"" = EXCLUDED.""LastAccessedUtc"",
    ""RegisteredUtc"" = EXCLUDED.""RegisteredUtc"",
    ""UpdatedUtc"" = EXCLUDED.""UpdatedUtc"";";
    }
}
