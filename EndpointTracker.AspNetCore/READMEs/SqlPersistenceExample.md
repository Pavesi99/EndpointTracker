# SQL persistence

EndpointTracker can periodically persist Redis metrics to PostgreSQL or SQL Server. The feature is optional: Redis-only and in-memory modes do not require a SQL database.

> SQL persistence is currently prerelease. Validate it against your traffic, database permissions, shutdown behavior, and failure scenarios before production rollout.

## Behavior

When `UseSqlPersistence` is enabled:

1. Startup validates the provider, connection string, and table name.
2. EndpointTracker connects to the existing database and creates its metrics, batch-ledger, and fencing-state tables when needed.
3. A background service transfers accumulated Redis metrics every `SqlPersistIntervalMinutes`.
4. A successfully persisted Redis transfer is removed from Redis.
5. Metrics reads combine SQL history with hits that have not yet been transferred.

The default persistence interval is 10 minutes. It must be at least one minute; invalid configuration is rejected during service registration.

SQL persistence requires Redis because Redis is the live buffer between transfers. Standalone and Sentinel-managed Redis deployments are supported. Redis Cluster is explicitly rejected because the durable transfer protocol uses atomic scripts across multiple Redis keys.

EndpointTracker creates the tables, not the database itself. With the default base name it creates three tables: `EndpointTrackerMetrics`, `EndpointTrackerMetrics_Batches`, and `EndpointTrackerMetrics_State`. The `_State` table contains one row holding the current monotonic fence token.

### Multiple application instances

Instances that share a Redis database and key prefix coordinate SQL persistence with a renewable Redis lease. The lease covers Redis snapshot creation, the idempotent SQL batch write, and Redis cleanup, so only one instance performs a transfer at a time. Each new lease receives a higher fence token. SQL atomically validates that token against the singleton `_State` row before a write, preventing a worker whose lease expired from committing after a newer reset or transfer. If lease renewal fails, the operation is cancelled and the pending Redis batch can be retried by a later lease holder.

`IEndpointTrackerService.Reset()` acquires the same lease before clearing Redis and SQL, preventing reset from racing a transfer on another instance. Reset clears the metrics and `_Batches` rows while advancing and retaining `_State`; do not delete or truncate the `_State` table independently.

## Configuration

Keep non-secret values in `appsettings.json`:

~~~json
{
  "ConnectionStrings": {
    "Redis": "localhost:6379"
  },
  "EndpointTracker": {
    "UseSqlPersistence": true,
    "RedisDatabase": 0,
    "RedisKeyPrefix": "my-api:endpoint-tracker:",
    "SqlProvider": "PostgreSQL",
    "SqlPersistIntervalMinutes": 10,
    "SqlTableName": "EndpointTrackerMetrics"
  }
}
~~~

Supply `ConnectionStrings:EndpointTrackerSql` through user secrets, environment variables, or your deployment's secret manager.

~~~csharp
using EndpointTracker.AspNetCore.Extensions;
using StackExchange.Redis;

var redis = ConnectionMultiplexer.Connect(
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Redis is not configured."));

var section = builder.Configuration.GetSection("EndpointTracker");

builder.Services.AddEndpointTrackerRedis(redis, options =>
{
    options.RedisDatabase = section.GetValue("RedisDatabase", 0);
    options.RedisKeyPrefix =
        section["RedisKeyPrefix"] ?? "endpoint-tracker:";
    options.UseSqlPersistence =
        section.GetValue("UseSqlPersistence", false);
    options.SqlProvider = section["SqlProvider"];
    options.SqlConnectionString =
        builder.Configuration.GetConnectionString("EndpointTrackerSql");
    options.SqlPersistIntervalMinutes =
        section.GetValue("SqlPersistIntervalMinutes", 10);
    options.SqlTableName =
        section["SqlTableName"] ?? "EndpointTrackerMetrics";
});
~~~

Supported provider values are:

| Database | Value |
| --- | --- |
| PostgreSQL | `PostgreSQL` or `Postgres` |
| SQL Server | `SqlServer` |

Use a simple table identifier that starts with a letter or underscore and contains only ASCII letters, digits, or underscores. The configured database must already exist, and the login needs permission to create all three tables on first startup and then select, insert, update, and delete rows.

### Persisted field limits

| Field | SQL persistence limit | Behavior when exceeded |
| --- | --- | --- |
| Endpoint pattern | 450 characters | The endpoint is logged and is not tracked |
| Display name | 1,024 characters | Truncated before it enters the Redis-to-SQL flow |
| HTTP method | 50 characters | Truncated before it enters the Redis-to-SQL flow |

The endpoint pattern is the SQL primary key, so its 450-character limit is enforced rather than truncated to avoid route collisions.

When SQL mode starts against Redis data created earlier, endpoint patterns over 450 characters are rejected and logged. Their active Redis metadata, hit counter, and last-access key are removed so unsupported data cannot block later persistence batches.

## Local PostgreSQL

Start PostgreSQL with a small development memory limit:

~~~bash
export ENDPOINTTRACKER_POSTGRES_PASSWORD='choose-a-local-password'

docker run --detach \
  --name endpointtracker-postgres \
  --publish 5432:5432 \
  --memory 320m \
  --env POSTGRES_DB=endpointtracker \
  --env POSTGRES_USER=endpointtracker \
  --env POSTGRES_PASSWORD="$ENDPOINTTRACKER_POSTGRES_PASSWORD" \
  postgres:16-alpine
~~~

Configure the example without committing the password:

~~~bash
export EndpointTracker__UseSqlPersistence=true
export EndpointTracker__SqlProvider=PostgreSQL
export ConnectionStrings__EndpointTrackerSql="Host=localhost;Port=5432;Database=endpointtracker;Username=endpointtracker;Password=$ENDPOINTTRACKER_POSTGRES_PASSWORD"

dotnet run --project EndpointTracker.Example
~~~

## Local SQL Server

SQL Server needs substantially more memory than Redis or PostgreSQL. The `--platform` option lets the x64 image run through Docker's emulation on Apple silicon.

~~~bash
export ENDPOINTTRACKER_SQLSERVER_PASSWORD='choose-a-strong-local-password'

docker run --detach \
  --name endpointtracker-sqlserver \
  --platform linux/amd64 \
  --publish 1433:1433 \
  --memory 2304m \
  --env ACCEPT_EULA=Y \
  --env MSSQL_PID=Developer \
  --env MSSQL_SA_PASSWORD="$ENDPOINTTRACKER_SQLSERVER_PASSWORD" \
  mcr.microsoft.com/mssql/server:2022-latest
~~~

Wait for SQL Server to become ready, then create the database:

~~~bash
docker exec endpointtracker-sqlserver \
  /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$ENDPOINTTRACKER_SQLSERVER_PASSWORD" -C \
  -Q "CREATE DATABASE EndpointTracker"
~~~

Configure and run the example:

~~~bash
export EndpointTracker__UseSqlPersistence=true
export EndpointTracker__SqlProvider=SqlServer
export ConnectionStrings__EndpointTrackerSql="Server=localhost,1433;Database=EndpointTracker;User Id=sa;Password=$ENDPOINTTRACKER_SQLSERVER_PASSWORD;Encrypt=True;TrustServerCertificate=True"

dotnet run --project EndpointTracker.Example
~~~

The `sa` account is convenient for a disposable local container. Use a least-privilege login in deployed environments.

## Verify

Generate endpoint traffic and read the combined view:

~~~bash
curl http://localhost:5288/weatherforecast
curl http://localhost:5288/weatherforecast
curl http://localhost:5288/metrics/endpoints
~~~

Use the port printed by `dotnet run`. For a fast manual test, temporarily set `EndpointTracker__SqlPersistIntervalMinutes=1`, wait for a transfer, and query the metrics table. The same base name also produces `_Batches` and `_State` tables; `_State` should contain its singleton fencing row.

PostgreSQL:

~~~bash
docker exec endpointtracker-postgres \
  psql --username endpointtracker --dbname endpointtracker \
  --command 'SELECT * FROM "EndpointTrackerMetrics"; SELECT * FROM "EndpointTrackerMetrics_Batches"; SELECT * FROM "EndpointTrackerMetrics_State";'
~~~

SQL Server:

~~~bash
docker exec endpointtracker-sqlserver \
  /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$ENDPOINTTRACKER_SQLSERVER_PASSWORD" -C \
  -d EndpointTracker \
  -Q "SELECT * FROM [EndpointTrackerMetrics]; SELECT * FROM [EndpointTrackerMetrics_Batches]; SELECT * FROM [EndpointTrackerMetrics_State]"
~~~

After a successful transfer, the metrics API should still report the persisted counts even though the transferred live Redis data has been cleared. New requests should appear immediately from Redis and be added to the SQL history in the response.

## Disable SQL persistence

Set:

~~~json
{
  "EndpointTracker": {
    "UseSqlPersistence": false
  }
}
~~~

No SQL provider or connection string is required in this mode. Redis continues to serve metrics normally.

## Troubleshooting

- Startup connection errors: verify the target database already exists and the login can connect.
- Table-creation errors: grant schema-level create permission or create all three tables through your deployment process.
- PostgreSQL identifier errors: keep `SqlTableName` to a simple unqualified identifier.
- SQL Server TLS errors in a local container: use `Encrypt=True;TrustServerCertificate=True` only for local development; configure a trusted certificate in deployed environments.
- Counts do not transfer: check logs from `SqlPersistenceHostedService` and verify the interval.
- Redis Cluster error: use standalone Redis or a Sentinel-managed primary; cluster topology is intentionally unsupported.
- Missing long routes: SQL-backed endpoint patterns longer than 450 characters are rejected and logged.
- Metrics routes return 401/403: authorization is required by default; authenticate or explicitly disable it only for local testing.

## Remove local containers

~~~bash
docker rm --force endpointtracker-redis
docker rm --force endpointtracker-postgres
docker rm --force endpointtracker-sqlserver
~~~

These commands delete the disposable container data.
