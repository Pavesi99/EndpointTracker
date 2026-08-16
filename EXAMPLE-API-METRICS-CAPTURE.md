# Example API and SQL Persistence Capture

Run date: 2026-08-16

Example URL: `http://127.0.0.1:5095`

SQL provider: PostgreSQL 16

Redis provider: Redis 7

SQL persistence: enabled

SQL table: `ETExample202608160936`

Redis prefix: `endpoint-tracker-example-202608160936:`

Credentials are intentionally omitted.

## Startup result

```text
SQL persistence tables are ready.
EndpointTracker registered 9 endpoints.
Now listening on: http://127.0.0.1:5095
Hosting environment: Production
```

The example uses `UseHttpsRedirection`, but this local run intentionally exposed only HTTP. ASP.NET logged that it could not determine an HTTPS port; requests continued successfully over localhost HTTP.

## API calls made

| Count | Request | Status | Captured response |
|---:|---|---:|---|
| 3 | `GET /api/users` | 200 | `[{"id":1,"name":"Alice"},{"id":2,"name":"Bob"}]` |
| 2 | `GET /api/users/42` | 200 | `{"id":42,"name":"User 42"}` |
| 1 | `POST /api/users` with `{"name":"Carol"}` | 201 | `{"name":"Carol"}` |
| 1 | `PUT /api/users/7` with `{"name":"Updated User"}` | 204 | empty body |
| 1 | `DELETE /api/users/9` | 204 | empty body |
| 1 | `GET /weatherforecast` | 200 | response below |

Captured weather response:

```json
[
  {
    "date": "2026-08-17",
    "temperatureC": 26,
    "summary": "Bracing",
    "temperatureF": 78
  },
  {
    "date": "2026-08-18",
    "temperatureC": 27,
    "summary": "Balmy",
    "temperatureF": 80
  },
  {
    "date": "2026-08-19",
    "temperatureC": 21,
    "summary": "Bracing",
    "temperatureF": 69
  },
  {
    "date": "2026-08-20",
    "temperatureC": 30,
    "summary": "Sweltering",
    "temperatureF": 85
  },
  {
    "date": "2026-08-21",
    "temperatureC": -9,
    "summary": "Balmy",
    "temperatureF": 16
  }
]
```

## First metrics response

Request: `GET /metrics/endpoints`

Status: 200

This response contains the nine sample API requests. The metrics request that produces a response is tracked after the response is generated, so it is not included in its own snapshot.

```json
{
  "totalEndpoints": 9,
  "usedEndpoints": 6,
  "unusedEndpoints": 3,
  "totalRequests": 9,
  "endpoints": [
    {
      "endpointPattern": "GET /api/users",
      "displayName": "HTTP: GET /api/users",
      "httpMethod": "GET",
      "hitCount": 3,
      "lastAccessedUtc": "2026-08-16T12:51:28.873923Z",
      "registeredUtc": "2026-08-16T12:50:52.63527Z"
    },
    {
      "endpointPattern": "GET /api/users/{id:int}",
      "displayName": "HTTP: GET /api/users/{id:int}",
      "httpMethod": "GET",
      "hitCount": 2,
      "lastAccessedUtc": "2026-08-16T12:51:36.981908Z",
      "registeredUtc": "2026-08-16T12:50:52.635273Z"
    },
    {
      "endpointPattern": "DELETE /api/users/{id:int}",
      "displayName": "HTTP: DELETE /api/users/{id:int}",
      "httpMethod": "DELETE",
      "hitCount": 1,
      "lastAccessedUtc": "2026-08-16T12:51:47.536995Z",
      "registeredUtc": "2026-08-16T12:50:52.635275Z"
    },
    {
      "endpointPattern": "GET /weatherforecast",
      "displayName": "HTTP: GET /weatherforecast",
      "httpMethod": "GET",
      "hitCount": 1,
      "lastAccessedUtc": "2026-08-16T12:51:51.229574Z",
      "registeredUtc": "2026-08-16T12:50:52.635178Z"
    },
    {
      "endpointPattern": "POST /api/users",
      "displayName": "HTTP: POST /api/users",
      "httpMethod": "POST",
      "hitCount": 1,
      "lastAccessedUtc": "2026-08-16T12:51:41.608767Z",
      "registeredUtc": "2026-08-16T12:50:52.635274Z"
    },
    {
      "endpointPattern": "PUT /api/users/{id:int}",
      "displayName": "HTTP: PUT /api/users/{id:int}",
      "httpMethod": "PUT",
      "hitCount": 1,
      "lastAccessedUtc": "2026-08-16T12:51:44.465742Z",
      "registeredUtc": "2026-08-16T12:50:52.635274Z"
    },
    {
      "endpointPattern": "GET /api/admin/settings",
      "displayName": "HTTP: GET /api/admin/settings",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2026-08-16T12:50:52.635275Z"
    },
    {
      "endpointPattern": "GET /metrics/endpoints",
      "displayName": "HTTP: GET /metrics/endpoints",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2026-08-16T12:50:52.639797Z"
    },
    {
      "endpointPattern": "GET /metrics/unused",
      "displayName": "HTTP: GET /metrics/unused",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2026-08-16T12:50:52.639798Z"
    }
  ]
}
```

## Unused endpoints response

Request: `GET /metrics/unused`

Status: 200

The first `/metrics/endpoints` request had already made that endpoint used. The current `/metrics/unused` request is not counted until after its response.

```json
{
  "count": 2,
  "endpoints": [
    {
      "endpointPattern": "GET /api/admin/settings",
      "displayName": "HTTP: GET /api/admin/settings",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2026-08-16T12:50:52.635275Z"
    },
    {
      "endpointPattern": "GET /metrics/unused",
      "displayName": "HTTP: GET /metrics/unused",
      "httpMethod": "GET",
      "hitCount": 0,
      "lastAccessedUtc": null,
      "registeredUtc": "2026-08-16T12:50:52.639798Z"
    }
  ]
}
```

## Second metrics response before shutdown

Request: `GET /metrics/endpoints`

Status: 200

```json
{
  "totalEndpoints": 9,
  "usedEndpoints": 8,
  "unusedEndpoints": 1,
  "totalRequests": 11,
  "endpoints": [
    { "endpointPattern": "GET /api/users", "httpMethod": "GET", "hitCount": 3 },
    { "endpointPattern": "GET /api/users/{id:int}", "httpMethod": "GET", "hitCount": 2 },
    { "endpointPattern": "DELETE /api/users/{id:int}", "httpMethod": "DELETE", "hitCount": 1 },
    { "endpointPattern": "GET /metrics/endpoints", "httpMethod": "GET", "hitCount": 1 },
    { "endpointPattern": "GET /metrics/unused", "httpMethod": "GET", "hitCount": 1 },
    { "endpointPattern": "GET /weatherforecast", "httpMethod": "GET", "hitCount": 1 },
    { "endpointPattern": "POST /api/users", "httpMethod": "POST", "hitCount": 1 },
    { "endpointPattern": "PUT /api/users/{id:int}", "httpMethod": "PUT", "hitCount": 1 },
    { "endpointPattern": "GET /api/admin/settings", "httpMethod": "GET", "hitCount": 0 }
  ]
}
```

## Graceful shutdown persistence

The API was stopped gracefully. The hosted SQL service reported:

```text
SQL persistence is stopping. Persisting final metrics.
Persisted Redis batch b6e05695a1e340fd8eec70e5ce43436b containing 9 endpoint metrics to SQL.
```

PostgreSQL contained:

```text
EndpointPattern                HitCount
GET /api/users                 3
GET /api/users/{id:int}        2
GET /metrics/endpoints         2
DELETE /api/users/{id:int}     1
GET /metrics/unused            1
GET /weatherforecast           1
POST /api/users                1
PUT /api/users/{id:int}        1
GET /api/admin/settings        0
```

The batch ledger contained one committed batch. The SQL fencing state contained `CurrentFence = 15`.

After persistence, Redis contained only:

```text
endpoint-tracker-example-202608160936:endpoints:metadata
endpoint-tracker-example-202608160936:sql-persistence:fence
endpoint-tracker-example-202608160936:sql-persistence:generation
```

There were no Redis hit keys, last-accessed keys, pending batches, or batch payloads.

## Metrics response after application restart

The API was restarted with the same Redis prefix and PostgreSQL table. Before making any new sample API calls, `GET /metrics/endpoints` returned HTTP 200 with the persisted counts:

```json
{
  "totalEndpoints": 9,
  "usedEndpoints": 8,
  "unusedEndpoints": 1,
  "totalRequests": 12,
  "endpoints": [
    { "endpointPattern": "GET /api/users", "httpMethod": "GET", "hitCount": 3 },
    { "endpointPattern": "GET /api/users/{id:int}", "httpMethod": "GET", "hitCount": 2 },
    { "endpointPattern": "GET /metrics/endpoints", "httpMethod": "GET", "hitCount": 2 },
    { "endpointPattern": "DELETE /api/users/{id:int}", "httpMethod": "DELETE", "hitCount": 1 },
    { "endpointPattern": "GET /metrics/unused", "httpMethod": "GET", "hitCount": 1 },
    { "endpointPattern": "GET /weatherforecast", "httpMethod": "GET", "hitCount": 1 },
    { "endpointPattern": "POST /api/users", "httpMethod": "POST", "hitCount": 1 },
    { "endpointPattern": "PUT /api/users/{id:int}", "httpMethod": "PUT", "hitCount": 1 },
    { "endpointPattern": "GET /api/admin/settings", "httpMethod": "GET", "hitCount": 0 }
  ]
}
```

This restart response proves the metrics endpoint retrieved the persisted SQL values after the Redis hit counters had been cleared. As before, the metrics request that generated this response was recorded only after the response was created.

## Result

**PASS**

- All sample API calls returned their expected status codes and bodies.
- Endpoint counts matched the number of calls made.
- The unused endpoint query returned only zero-hit routes at snapshot time.
- Graceful shutdown persisted the metrics to PostgreSQL.
- Redis active counters and pending batches were cleared after SQL persistence.
- Restarting the example returned the same persisted metrics from SQL.
