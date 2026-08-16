using EndpointTracker.AspNetCore.Options;
using EndpointTracker.AspNetCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EndpointTracker.Tests;

public class SqlPersistenceStoreTests
{
    [Theory]
    [InlineData("Metrics;DROP_TABLE")]
    [InlineData("public.Metrics")]
    [InlineData("Metrics-name")]
    [InlineData("\"Metrics\"")]
    [InlineData("9Metrics")]
    public void Constructor_RejectsUnsafeTableIdentifiers(string tableName)
    {
        var options = CreateOptions("PostgreSQL", tableName);

        Assert.Throws<InvalidOperationException>(() =>
            new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance));
    }

    [Theory]
    [InlineData("PostgreSQL", 56)]
    [InlineData("SqlServer", 121)]
    public void Constructor_RejectsTableNamesThatLeaveNoRoomForLedgerSuffix(
        string provider,
        int tableNameLength)
    {
        var options = CreateOptions(provider, new string('a', tableNameLength));

        Assert.Throws<InvalidOperationException>(() =>
            new SqlPersistenceStore(options, NullLogger<SqlPersistenceStore>.Instance));
    }

    private static EndpointTrackerOptions CreateOptions(string provider, string tableName) => new()
    {
        UseSqlPersistence = true,
        SqlProvider = provider,
        SqlConnectionString = "test-connection-string",
        SqlTableName = tableName
    };
}
