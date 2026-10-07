# Hacker News Best Stories API

ASP.NET Core API with PostgreSQL-backed acquisition, immutable in-memory top results,
streamed updates, and durable asynchronous exports.

**Local-only, unauthenticated deployment.** Operation IDs are not authorization.
Add authenticated ownership and abuse protection before exposing this API publicly.

## Run

Requires Docker Desktop with Linux containers and Compose.

```powershell
Copy-Item .env.example .env
# Replace POSTGRES_PASSWORD in .env with a strong password.
docker compose up --build -d --wait
curl.exe "http://localhost:5080/api/stories/best?count=10"
```

Ports bind to loopback: API `5080`, PostgreSQL `5432`. Override `API_PORT` and
`POSTGRES_PORT` in `.env`. The API uses `db:5432` on Compose's private network.
Secrets and generated artifacts are ignored; never commit `.env`.

If host NuGet restore works but container restore is blocked by corporate TLS:

```powershell
dotnet publish src\HackerNews.Api -c Release -p:UseAppHost=false -o .artifacts\publish
docker compose -f compose.yaml -f compose.host-published.yaml up --build -d --wait
```

This copies portable managed output into the Linux runtime without disabling TLS.
Republish after code changes; use both Compose files for subsequent rebuilds.

For host development, use stable .NET SDK 10.0.401 or a compatible 10.0 patch
(`global.json`). Stop the container API before starting another ingestion owner:

```powershell
docker compose stop api
docker compose up -d --wait db
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=hackernews;Username=hackernews;Password=<password>;Timeout=5;Command Timeout=30" --project src\HackerNews.Api
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet run --project src\HackerNews.Api
```

Alternatively set `ConnectionStrings__Postgres`. Host launch ports come from
`launchSettings.json`; add `-- --urls http://localhost:5080` to match Compose.

## API

| Route | Response |
|-------|----------|
| `GET /api/stories/best?count=10` | Up to count acquired stories; required integer `1..MaximumStoryCount` |
| `POST /api/story-exports` | 202 with operationId, status, statusPath, Location, and Retry-After: 5 |
| `GET /api/story-exports/{id}` | Status, counts, timestamps, generation, safe error, and resultsPath on success |
| `GET /api/story-exports/{id}/results?pageSize=1000` | Completed frozen results: operationId, items, nextContinuationToken |
| `GET /health/live` | Process liveness; no upstream/database calls |
| `GET /health/ready` | Requires usable snapshot and database connectivity |
| `GET /openapi/v1.json` | OpenAPI JSON; no Swagger UI or root-page endpoint |

Stories are ordered by score descending, then ID ascending:

```json
{
  "title": "A story title",
  "uri": "https://example.com/story",
  "postedBy": "username",
  "time": "2026-10-07T00:00:00+00:00",
  "score": 100,
  "commentCount": 12
}
```

Unix time becomes UTC DateTimeOffset; missing URL becomes null and missing
descendants becomes zero. Null, deleted, dead, non-story, missing required fields,
and invalid timestamps are excluded. Results may contain fewer acquired records
than requested; neither endpoint waits for complete upstream acquisition.

### Export example

Send the key as a **header**, not a JSON property. Poll using the returned
**operationId**, not the key:

```powershell
$export = Invoke-RestMethod -Method Post -Uri http://localhost:5080/api/story-exports `
    -ContentType application/json -Headers @{ "Idempotency-Key" = [guid]::NewGuid().ToString() } `
    -Body '{"count":1000000}'
$id = $export.operationId
Invoke-RestMethod "http://localhost:5080/api/story-exports/$id"
# Once status is succeeded:
$page = Invoke-RestMethod "http://localhost:5080/api/story-exports/$id/results?pageSize=1000"
if ($page.nextContinuationToken) {
    $token = [uri]::EscapeDataString($page.nextContinuationToken)
    Invoke-RestMethod "http://localhost:5080/api/story-exports/$id/results?pageSize=1000&continuationToken=$token"
}
```

Creation requires integer count and one global Idempotency-Key containing 1..128
printable ASCII characters without spaces. Same key/count replays the operation;
different count conflicts. Status progresses through queued, inProgress, then
succeeded or failed. Page size defaults to 100 (or the configured ceiling if lower);
keep it unchanged with a token. Pages may be smaller to fit the exact JSON byte
limit; the final token is null. Exports can succeed with zero available records.

Errors use ProblemDetails (`application/problem+json`):

| Status | Meaning |
|--------|---------|
| 400 | Invalid count/body/header/page size or tampered, incompatible, expired token |
| 404 | Unknown operation or expired metadata already removed |
| 409 | Idempotency conflict or results not succeeded |
| 410 | Expired operation while tombstone remains |
| 415 | Non-JSON export creation |
| 429 | Export admission/row quota exhausted; Retry-After: 5 |
| 503 | No initial story snapshot or unavailable export storage/results |

Counts are never clamped. Repeated/missing synchronous count values are invalid.

## Architecture and recovery

```text
HN membership + updates SSE -> durable pending versions -> bounded item batches
                                      |
                  PostgreSQL records + progress + ranked snapshot transaction
                                      |
                  atomic memory snapshot -> synchronous reads
                                      |
                  frozen export rows -> checkpoints -> protected result pages
```

The typed HTTP clients handle upstream I/O; services handle acquisition, mapping,
and publication; endpoints validate requests and translate outcomes. Startup
transactionally migrates schemas, validates source identity, and restores only
bounded top rows using the score/ID index. Corrupt/incompatible/unavailable storage
fails startup; there is no memory-only fallback or silent reset.

One ingestion instance owns each database through a session advisory lock.
Initial acquisition is single-flight and bounded by UpdateBatchSize. Refreshes
do not overlap. Successful batches commit records, notification versions, and
snapshot together before atomic memory publication. Failed item reads retain
previous data and pending work; there are no immediate item retries. Empty refreshes
retain the last nonempty snapshot indefinitely, including across restart.
Reads can serve it during database outages while readiness fails; a lost owner
session fails closed and requires restart when the database recovers.

SSE IDs are durably marked before the next event; duplicates coalesce by version.
Connection/idle timeouts and exponential reconnect backoff apply. Membership
checks and rolling reconciliation repair gaps; no lossless event replay is claimed.
Reconciliation progress and unfinished batches survive restart.

Exports never fetch upstream items. A single worker freezes values/order/generation
in one server-side MVCC statement when preparation starts, then validates bounded
chunks with durable checkpoints. Interrupted freezes roll back and retry a fresh
view; committed rows/checkpoints resume unchanged. Partial output stays private.
Queue locks serialize admission/checkpoints; repeatable-read pages remain consistent
during cleanup. Oversized/invalid stories fail with recordTooLarge/invalidDataset;
SQL failures log/retry until deadline. Request disconnects do not cancel accepted work.

HMAC tokens bind operation, generation, position, page size, version, and expiry.
Their signing key is persisted in PostgreSQL; backups and restarts preserve tokens.
Protect database access/backups. Key rotation and cancellation are not implemented.

## Configuration

Override sections in `appsettings.json` or environment variables, such as
`HackerNews__UpdateBatchSize` and `Exports__MaximumCount`. Options validate at startup:
positive supported timer ranges/counts; absolute HTTP(S) BaseUrl with trailing slash.

| HackerNews setting | Default |
|--------------------|---------|
| BaseUrl | `https://hacker-news.firebaseio.com/v0/` |
| RefreshInterval / ReconciliationInterval | `00:01:00` / `00:30:00` |
| RequestTimeout / InitialLoadWaitTimeout | `00:00:10` / `00:00:15` |
| MaxUpstreamConcurrency / UpdateBatchSize | `10` / `100` |
| MaximumStoryCount | `100` (memory snapshot and synchronous count ceiling) |
| StreamReconnectDelay / StreamIdleTimeout | `00:00:05` / `00:01:00` |

| Exports setting | Default |
|-----------------|---------|
| MaximumCount / MaximumActiveJobs | `1000000` / `10` |
| MaximumReservedRows | `2000000` (requested rows while active; actual rows after success) |
| ChunkSize / MaximumPageSize | `500` / `1000`; chunk must not exceed page ceiling |
| MaximumResponseBytes | `1048576`; minimum 4096, 2 KiB reserved for envelope/token |
| JobDeadline / PollInterval | `00:30:00` including queue time / `00:00:05` |
| Retention / TombstoneRetention | `1.00:00:00` each |

Cleanup releases expired/failed rows and idempotency mappings; tombstones remain
for the additional retention period. MaximumReservedRows must cover MaximumCount.
Row quotas are not disk-byte quotas.

## Tests

Default tests use fake HTTP, test-only storage, TimeProvider, and synchronization
gates; no test calls Hacker News. Coverage includes validation, SSE, concurrency,
snapshot isolation, recovery, transactions, frozen paging, tokens, quotas, and cleanup.

```powershell
dotnet test tests\HackerNews.Api.Tests --filter "FullyQualifiedName~SnapshotTests.GetBest_UnsortedUpstream_MapsSortsAndLimits"
# Optional real PostgreSQL tests: local disposable server; role needs CREATE/DROP DATABASE.
$env:HACKERNEWS_TEST_POSTGRES = "Host=localhost;Port=5432;Database=hackernews;Username=hackernews;Password=<password>;Timeout=5;Command Timeout=30"
dotnet test
# Optional isolated synthetic million-row fixture:
$env:HACKERNEWS_EXPORT_SCALE_TESTS = "1"
dotnet test tests\HackerNews.Api.Tests --filter "FullyQualifiedName~Export_MillionSyntheticRecords" --logger "console;verbosity=normal"
Remove-Item Env:\HACKERNEWS_EXPORT_SCALE_TESTS
Remove-Item Env:\HACKERNEWS_TEST_POSTGRES
```

Database tests create/drop only unique hn_test databases; never target production.
The scale fixture checks 10,000-row checkpoint and 1,000-row page bounds and reports
elapsed time/total allocations, not peak memory or certified throughput.

## Operations and limitations

```powershell
docker compose logs --tail 50 api
docker compose exec db pg_isready -U hackernews -d hackernews
docker compose restart api
docker compose exec db pg_dump -U hackernews -d hackernews -Fc -f /tmp/hackernews.dump
docker compose cp db:/tmp/hackernews.dump .\hackernews.dump
```

Ordinary `docker compose down` preserves beststories_postgres-data.
**`down --volumes` deletes acquired data.** The volume does not protect against host
loss. Store dumps securely; restore only into an empty replacement database with the
API stopped using `pg_restore -U hackernews -d hackernews --exit-on-error`.
Changing `.env` does not rotate an existing PostgreSQL role password.

Acquisition schema v1 creates tables; v2 adds generation; export schema is v1.
Future migrations must preserve work. Major PostgreSQL upgrades require tested
migration/dump-restore, not an image-tag-only change. Production needs TLS,
least-privilege credentials, durable storage, backup schedules, and restore drills.
Compose's bootstrap role is for development. Configure HTTP health probes separately;
Compose --wait alone does not establish API readiness.

The full membership ID list is still handled in memory. Acquisition/reconciliation
still cost upstream calls; concurrency/batch limits are not a strict rate limit.
Backlogs can grow, and publication age is not per-story freshness. Atomic export
freezes/cleanup can consume substantial WAL/disk and delay admission. No million-row
production capacity guarantee, artifact reuse, Redis, broker, multi-node ingestion
failover, authentication, or cancellation is provided.
