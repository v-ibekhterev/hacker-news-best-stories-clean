# Hacker News Best Stories API

Returns the highest-scoring acquired records from Hacker News's best-story candidate
list. PostgreSQL stores acquired items, pending updates, reconciliation progress, and
the last usable snapshot. The API serves a bounded immutable top-results snapshot
from memory; restarting does not discard acquired records or re-fetch every item.

## Start with Docker Compose

Prerequisites: Docker Desktop with the Linux/WSL 2 engine and Docker Compose.

```powershell
Copy-Item .env.example .env
# Edit .env: replace POSTGRES_PASSWORD with a strong local password.
docker compose up --build -d --wait
docker compose ps
curl.exe "http://localhost:5080/health/live"
curl.exe "http://localhost:5080/health/ready"
curl.exe "http://localhost:5080/api/stories/best?count=10"
curl.exe "http://localhost:5080/openapi/v1.json"
```

`.env` is ignored by Git and excluded from the image build. Do not commit it.
Compose uses PostgreSQL 17, a named `beststories_postgres-data` volume, and its
private service network. The API connects to `db:5432`; host ports bind only to
`127.0.0.1`. Customize `POSTGRES_PORT` or `API_PORT` in `.env` if those ports are busy.

If the host can restore NuGet but Docker's network cannot (for example corporate
TLS restrictions), use the explicit host-published runtime target instead:

```powershell
dotnet publish src\HackerNews.Api -c Release -p:UseAppHost=false -o .artifacts\publish
docker compose -f compose.yaml -f compose.host-published.yaml up --build -d --wait
```

This copies portable managed publish output, not a Windows executable, into the
Linux runtime image. Regenerate it after every code change. It does not bypass TLS
verification, and `.artifacts` is ignored by Git. Use the same two Compose files
for subsequent rebuild/up commands when using this fallback.

PostgreSQL's `pg_isready` health check gates API startup. `/health/live` is process-only;
`/health/ready` requires both PostgreSQL connectivity and a usable snapshot. A new
empty installation may report 503 until an initial batch yields valid stories.
The image intentionally does not install curl or a Docker-specific API health command;
configure deployment HTTP probes for `/health/live` and `/health/ready` on port 8080.

```powershell
docker compose logs --tail 50 api
docker compose exec db pg_isready -U hackernews -d hackernews
docker compose restart api
docker compose down
docker compose up -d --wait
```

Ordinary `down` preserves the named volume. **Do not use `down --volumes` unless you
intend to delete all acquired data and force a new acquisition.** A named volume
survives container replacement, not loss of the Docker host; production needs durable
storage and backups.

## Run on the host

Prerequisite: stable .NET SDK 10.0.401 or a newer 10.0 patch (pinned in `global.json`).
Start PostgreSQL first:

```powershell
docker compose up -d --wait db
# Configure using .NET user-secrets; enter the actual password from .env.
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=hackernews;Username=hackernews;Password=<password>;Timeout=5;Command Timeout=30" --project src\HackerNews.Api
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet run --project src\HackerNews.Api -- --urls http://localhost:5080
```

Alternatively set `ConnectionStrings__Postgres` in the environment. Never place the
real connection string in tracked configuration. The shorter
`dotnet run --project src\HackerNews.Api` uses launchSettings.json's port.
Production does not silently fall back to memory when the database is missing.

## HTTP contract

`GET /api/stories/best?count=n`, with one integer in `1..MaximumStoryCount` (default 100).
Missing, malformed, repeated, zero, negative, overflowing, or excessive values return
400 RFC 7807 `ProblemDetails` (`application/problem+json`); values are never clamped.
If no usable snapshot exists after the bounded initial wait, return 503 ProblemDetails.
Requests never expand the dataset or trigger a full initial load.

200 returns at most `count` acquired valid stories, score descending with ID ascending
as the tie-breaker. Partial acquisition may yield fewer stories than requested.
OpenAPI: `/openapi/v1.json`; no Swagger UI is bundled.

```json
[
  {
    "title": "A story title",
    "uri": "https://example.com/story",
    "postedBy": "username",
    "time": "2019-10-12T13:43:01+00:00",
    "score": 1716,
    "commentCount": 572
  }
]
```

`time` is converted from Unix seconds to UTC DateTimeOffset. Scores come from upstream;
missing descendants becomes zero. Missing URL becomes null. Null, deleted, dead,
non-story, missing title/author/time/score, and invalid timestamps are excluded.
Unknown upstream JSON fields are ignored.

## Asynchronous exports

Exports read only the shared persisted candidate dataset; they never fetch upstream
items. The synchronous endpoint and its count limit are unchanged. **Exports have no
authentication: this deployment is local-only. Do not publish it on a public network.**
An operation ID or idempotency key is not authorization. Authentication/ownership
and per-caller rate limits must be added before public deployment.

```powershell
$request = @{ count = 1000000 } | ConvertTo-Json
$created = Invoke-RestMethod -Method Post -Uri http://localhost:5080/api/story-exports `
    -ContentType application/json -Headers @{ "Idempotency-Key" = [guid]::NewGuid().ToString() } -Body $request
$id = $created.operationId
Invoke-RestMethod "http://localhost:5080/api/story-exports/$id"
# Once status is succeeded:
$page = Invoke-RestMethod "http://localhost:5080/api/story-exports/$id/results?pageSize=1000"
$page.items
if ($page.nextContinuationToken) {
    $token = [uri]::EscapeDataString($page.nextContinuationToken)
    Invoke-RestMethod "http://localhost:5080/api/story-exports/$id/results?pageSize=1000&continuationToken=$token"
}
```

| Route | Contract |
|-------|----------|
| `POST /api/story-exports` | JSON integer `count` in `1..Exports:MaximumCount`; one `Idempotency-Key` header, 1..128 printable ASCII characters without spaces. Returns 202 with operationId, status, statusPath, Location, and Retry-After: 5. |
| `GET /api/story-exports/{id}` | 200 with status (`queued`, `inProgress`, `succeeded`, `failed`), requestedCount, processedCount, nullable resultCount, timestamps, nullable datasetGeneration, safe error, and resultsPath only on success. |
| `GET /api/story-exports/{id}/results` | Completed frozen values only. Optional pageSize defaults to 100 (or the configured ceiling if smaller); optional continuationToken. Returns operationId, items, nextContinuationToken (null at end). |

Malformed JSON/count/header/page size or invalid/tampered/expired tokens return
400 ProblemDetails; non-JSON creation returns 415. Same key/count replays the existing
operation; same key/different count returns 409. Idempotency keys are global in this
unauthenticated deployment. Active-job or retained-row capacity exhaustion returns
429 with Retry-After. Unknown operations return 404, incomplete/failed result retrieval
409, expired operations 410 while tombstones remain, and storage unavailability 503.
Expiration may return 404 after tombstone retention. Expired tokens return 400;
query an operation without a token to distinguish expired results.

The single background worker atomically freezes ranking **and values** using a
server-side PostgreSQL INSERT/SELECT with one MVCC statement snapshot. Its source
view is pinned when preparation begins, not when the request is queued; generation
is null until that freeze commits. Exports succeed with fewer available records,
including zero. They do not wait for acquisition to reach the requested count.
Sort order is score descending, then story ID ascending.

Freezing all selected rows is one database transaction, not a million-row application
buffer or a resumable chunked source copy. An interrupted freeze rolls back and is
retried against a fresh view. This avoids retaining upstream record versions or
holding a database snapshot across restarts, at the cost of potentially substantial
transaction duration, WAL, disk, and database sorting/spill work. After the freeze,
bounded validation chunks durably advance processedCount. A restart resumes the last
committed validation checkpoint against the same frozen rows; partial results stay
private. Only a final committed checkpoint exposes success. Database queue locks
serialize admission/processing checkpoints and make repeat execution safe; one worker
is configured, and the existing single-ingestion-instance restriction remains.

Result reads use indexed ordinals and bounded row/byte selection in one repeatable-read
transaction, so cleanup cannot remove half a page. Pages can contain fewer than
pageSize items to honor the **exact serialized JSON byte limit**. A single record that
cannot fit, including a conservative 2 KiB envelope/token allowance, fails the export
with `recordTooLarge` instead of producing unpageable results. Invalid persisted
stories fail with `invalidDataset`; SQL failures are logged and retried until deadline.
Accepted work survives request disconnects. No cancellation endpoint is implemented.

HMAC-protected tokens bind version, operation, dataset generation, last ordinal,
page size, and expiry. Keep pageSize unchanged when following a token. The signing key
is generated once and stored in PostgreSQL, so container restarts and database
backup/restore preserve tokens. Protect database access/backups; key rotation is not
implemented. Restoring a backup also restores its jobs and signing key.

| Exports setting | Default | Meaning |
|-----------------|---------|---------|
| MaximumCount | 1,000,000 | Requested-count ceiling |
| MaximumActiveJobs | 10 | Queued + in-progress admission ceiling |
| MaximumReservedRows | 2,000,000 | Global row budget: requested counts while active, actual counts when succeeded |
| ChunkSize | 500 | Validation/checkpoint rows; positive, at most MaximumPageSize |
| MaximumPageSize | 1,000 | Page-count ceiling |
| MaximumResponseBytes | 1,048,576 | Exact serialized JSON page ceiling; minimum 4 KiB |
| JobDeadline | 00:30:00 | From acceptance, including queue time; timeout fails and deletes private rows |
| Retention | 1.00:00:00 | Results and idempotency mapping retained after success/failure |
| TombstoneRetention | 1.00:00:00 | Additional expired metadata retention |
| PollInterval | 00:00:05 | Idle/retry/cleanup cadence |

Use the `Exports` configuration section or `Exports__...` environment variables.
Limits and timer ranges are startup-validated. Cleanup runs on worker ticks and
admission; failed/expired rows release reserved capacity, and expired mappings allow
key reuse. A page started before cleanup sees a consistent retained view. Row budgets
are **not disk-byte quotas**; monitor PostgreSQL disk/WAL space and size deployment
storage for actual story widths. Queue locking can delay admission while a large
freeze/cleanup runs. No throughput guarantee, artifact reuse, public abuse protection,
or multi-node ingestion failover is implied.

## Durable acquisition and startup

```text
Startup -> transactional schema/source validation -> restore bounded top snapshot
                                                    |
updates SSE -> durable dirty versions/order -> bounded item batch <- minute timer
                                                    |
                         PostgreSQL item results + progress + snapshot transaction
                                                    |
                            immutable top-result snapshot -> normal API reads
```

One API ingestion instance owns each database through a session advisory lock.
A second owner fails explicitly. No multi-node refresh or ownership failover is
implemented. The owner connection is kept open for all state writes; losing it
fails closed and stops background acquisition/application instead of continuing
without fencing. Restart the instance once PostgreSQL is available.

At startup, the schema is created/migrated transactionally if absent or older, and the stored
source URL/schema version is checked. Incompatible state, source mismatch, invalid
snapshot, or unavailable database fails startup explicitly. Never silently clear the
store or reload the whole source in response to a storage failure.
Subsequent starts restore the persisted ranked rows using a `(score DESC, id ASC)`
partial index and a SQL LIMIT, not a full database copy into memory. The last nonempty
snapshot is stored too, so a completely empty candidate cache can still restore
the deliberately retained stale snapshot.

Empty-store acquisition is resumable and **bounded from its first batch**, unlike
the previous full bootstrap. Each minute refreshes membership and fetches at most
UpdateBatchSize items. Candidate rows preserve notification versions, FIFO order,
acquisition timestamps, and uncompleted work. Selecting work does not acknowledge it.
A crash before result commit leaves it pending; a crash after commit restores the
committed state. A newer notification received during a fetch stays pending.

Successful item reads replace or remove the cached item. HTTP/JSON/timeout failures
retain old data and pending work, moving the failure to the queue tail. There are no
immediate item retries. Results, processed versions, and the bounded serving snapshot
are committed together; only after commit is the memory reference replaced.
Requests continue using the previous complete snapshot during work.
If no valid records remain, retain the previous nonempty snapshot indefinitely.
Snapshot age is publication age, not the age of every included item.

Transient command failures are logged and never published as successful commits.
An already loaded snapshot can serve reads during a database outage; database
readiness fails. Loss of the owner session requires an application restart rather
than an unsafe automatic ownership takeover.

## Updates and reconciliation

The long-lived `/updates.json` SSE connection extracts item IDs from Firebase put/patch
events, ignores profiles, and durably marks only existing candidates dirty before
accepting the next event. Multiple IDs are written in one database command.
Duplicates coalesce into a single pending record with a version; no full story records
are supplied by updates. Removing IDs from the feed is not a story-deletion signal.
Membership checks discover new/departed candidates separately.

Connection headers use RequestTimeout; read inactivity (including missing keep-alives)
uses StreamIdleTimeout. EOF, malformed events, transport errors, and Firebase cancel
events reconnect with exponential backoff from StreamReconnectDelay to 60 seconds
(or the configured base when larger). Valid data events reset the delay.

Every ReconciliationInterval, clean candidates are queued for a rolling recheck.
Dirty/pending candidates already scheduled are not reset. Queue state and the
reconciliation timestamp are durable, so restarting continues uncompleted work.
A complete pass still costs one item GET per candidate. The updates feed has no
documented durable replay guarantee; bootstrap/disconnection gaps are repaired by
reconciliation, not claimed to be lossless.

## Configuration

HackerNews options are validated at startup; override via appsettings.json or variables
such as `HackerNews__UpdateBatchSize`. The PostgreSQL connection string is mandatory.

| Setting | Default | Constraint |
|---------|---------|------------|
| BaseUrl | `https://hacker-news.firebaseio.com/v0/` | Absolute HTTP(S), trailing slash |
| RefreshInterval | `00:01:00` | Positive timer range; membership/batch cadence |
| RequestTimeout | `00:00:10` | Positive HttpClient range; per request, not whole batch |
| InitialLoadWaitTimeout | `00:00:15` | Positive timer range; caller wait bound |
| MaxUpstreamConcurrency | `10` | At least 1 |
| MaximumStoryCount | `100` | At least 1; maximum in-memory serving records |
| UpdateBatchSize | `100` | At least 1; per acquisition batch, including first load |
| ReconciliationInterval | `00:30:00` | Positive timer range |
| StreamReconnectDelay | `00:00:05` | Positive timer range |
| StreamIdleTimeout | `00:01:00` | Positive timer range |

## Tests

```powershell
dotnet test
dotnet test tests\HackerNews.Api.Tests --filter "FullyQualifiedName~SnapshotTests.GetBest_UnsortedUpstream_MapsSortsAndLimits"
```

Default tests use a **test-only** transactional state model and fake HTTP handlers.
Real PostgreSQL tests are explicitly skipped without HACKERNEWS_TEST_POSTGRES.
To include them, use the local disposable development database server with an
account allowed to create/drop databases:

```powershell
$env:HACKERNEWS_TEST_POSTGRES = "Host=localhost;Port=5432;Database=hackernews;Username=hackernews;Password=<password>;Timeout=5;Command Timeout=30"
dotnet test
Remove-Item Env:\HACKERNEWS_TEST_POSTGRES
```

Each database test creates a unique `hn_test_<guid>` database and deletes only that
database afterward. Never point tests at an externally managed production server.
Tests cover restart without upstream reads, versioned pending work, interrupted
bootstrap/reconciliation, transaction rollback, owner/source/schema checks, corrupted
snapshot, PostgreSQL health, plus HTTP validation, bounded concurrency, snapshot
isolation, SSE parsing/reconnects, and >100 concurrent callers. TimeProvider, gates,
and channels replace arbitrary test sleeps. Tests never call public Hacker News.

Export tests additionally cover durable checkpoints/freeze rollback, concurrent
idempotency and processing, quotas, expiration/tombstones, frozen order/values,
restart-safe token traversal, oversized records, exact page bytes, and worker
non-overlap. A separate opt-in synthetic million-record fixture creates its own
isolated database and records elapsed time/managed allocations:

```powershell
# Set HACKERNEWS_TEST_POSTGRES as above first.
$env:HACKERNEWS_EXPORT_SCALE_TESTS = "1"
dotnet test tests\HackerNews.Api.Tests --filter "FullyQualifiedName~Export_MillionSyntheticRecords" --logger "console;verbosity=normal"
Remove-Item Env:\HACKERNEWS_EXPORT_SCALE_TESTS
```

The fixture uses larger configured validation chunks (10,000), checks checkpoint
ceilings and a bounded 1,000-row page, and never contacts Hacker News. Total managed
allocations are not peak resident memory; this is a reproducible functional scale
check, not a production load/throughput or memory-capacity certification.

## Backup, restore, and upgrades

For local manual backup without putting credentials in tracked files:

```powershell
docker compose exec db pg_dump -U hackernews -d hackernews -Fc -f /tmp/hackernews.dump
docker compose cp db:/tmp/hackernews.dump .\hackernews.dump
```

Store dumps securely outside the source tree. To restore into an empty replacement
database, stop the API first, copy the dump into the database container, and use
`pg_restore -U hackernews -d hackernews --exit-on-error /tmp/hackernews.dump`.
Do not restore over a live ingestion instance or use destructive restore flags
without intentionally approving replacement. Changing POSTGRES_PASSWORD in `.env`
does not change an existing database role's password; rotate it explicitly.

Version 1 creates acquisition tables; version 2 adds the durable publication generation.
Both run transactionally. Future migrations must
increment/validate schema_version and preserve durable work. PostgreSQL major-version
upgrades require a tested migration or dump/restore, not just changing the image tag
against the same data directory. Configure production TLS, managed credentials,
least-privilege roles, durable volumes, scheduled backups, and restore drills.
Compose's database bootstrap role is suitable only for local development.

## Limitations and future work

PostgreSQL is an explicitly approved extension to the original no-database assignment.
This remains a single-ingestion-instance deployment. No Redis, message broker, generic
repository framework, authentication, or live-dataset pagination has been added.
Large results use the separate asynchronous export API described above.

Persistence avoids restarting a million-item acquisition, but does not eliminate
initial acquisition or reconciliation. Only a bounded top snapshot is kept in memory;
the full best-story **ID list** still must be fetched/handled in memory for membership
comparison. Reconciliation enqueues rows in the database, which can create substantial
write work at large scale. No million-record throughput claim is made.
Batch/concurrency limits are not a strict requests-per-second budget. If incoming
changes exceed processing capacity, backlog and stale age grow. Defaults optimize
for the real small best-story list, not a hypothetical million-item API.
Count increases do not enlarge the source's candidate list.

Future work: async exports (separate plan), explicit rate/freshness budgets, metrics,
fenced multi-node ownership, source membership streaming if required, controlled
large-dataset measurements, and deployment-specific security/backup automation.
