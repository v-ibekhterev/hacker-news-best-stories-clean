# Hacker News Best Stories API

Returns the highest-scoring stories from the complete Hacker News `beststories` list.
One API project and one test project; no database, Redis, authentication, or pagination.

## Run and verify

Prerequisite: stable .NET SDK **10.0.401** (pinned in `global.json`), or a newer 10.0 patch.
From the repository root in PowerShell:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
dotnet format --verify-no-changes
dotnet run --project src\HackerNews.Api -- --urls http://localhost:5080
```

The shorter `dotnet run --project src\HackerNews.Api` uses the port in
`src\HackerNews.Api\Properties\launchSettings.json`.

```powershell
curl.exe "http://localhost:5080/api/stories/best?count=10"
curl.exe "http://localhost:5080/health/live"
curl.exe "http://localhost:5080/health/ready"
curl.exe "http://localhost:5080/openapi/v1.json"
dotnet test tests\HackerNews.Api.Tests --filter "FullyQualifiedName~SnapshotTests.GetBest_UnsortedUpstream_MapsSortsAndLimits"
```

OpenAPI is available at `/openapi/v1.json`, including the required query parameter
and 200/400/503 responses. A Swagger UI is not bundled.

## HTTP contract

`GET /api/stories/best?count=n`, where `n` is an integer in `1..MaximumStoryCount`.
Missing, malformed, repeated, zero, negative, overflowing, or excessive values return
**400** with RFC 7807 `ProblemDetails` (`application/problem+json`); values are never clamped.
Before a usable snapshot exists, failed initialization or an expired initial wait returns
**503** with `ProblemDetails`.

**200** returns a JSON array with at most `count` entries, sorted by current upstream
`score` descending (ID ascending breaks ties). A smaller available snapshot returns fewer stories.

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

`time` is a UTC `DateTimeOffset` converted from Unix seconds. `score` is never computed
locally. Missing `descendants` becomes zero. Stories without a URL are retained with
`uri: null` (including Ask HN stories). Null, deleted, dead, non-story, missing
title/author/time/score, or out-of-range timestamp items are excluded. Unknown upstream
JSON fields are ignored. Duplicate upstream IDs are retrieved once.

## Architecture and failure behavior

```text
Requests -> endpoint (validation/HTTP mapping) -> BestStoriesService -> immutable snapshot
                                                  ^
Initial load / minute timer -> shared refresh task -> typed HackerNewsClient
                                     ^                  |
Updates SSE worker -> deduplicated pending IDs       Hacker News
                                     |
                    bounded batches -> merge/filter/sort -> atomic swap
```

The hosted worker starts loading immediately. Initial callers share one load task and
wait at most 15 seconds each; caller cancellation or wait timeout does not cancel work
shared by other callers. After a failed first attempt, reads remain unavailable until
the worker successfully refreshes; repeated API calls do not retry the upstream.
Host shutdown cancels upstream work and the stream.

The initial load retrieves all distinct positive best-story IDs and their items with
bounded `Parallel.ForEachAsync`. Later refreshes retrieve the membership list but fetch
only newly added candidates, notified changes, deferred failures, and reconciliation
work. Departed candidates are removed. Each normal batch fetches at most
`UpdateBatchSize` items, with at most `MaxUpstreamConcurrency` simultaneous item calls.
The initial load remains a full acquisition, not a limited top-100 fetch.

One long-lived connection subscribes to `updates.json` with `Accept: text/event-stream`.
Firebase `put` and `patch` payloads supply item IDs; item values, not array indexes,
are extracted. Profiles are ignored, and IDs outside the current candidate set are
discarded before making any item request. Events are notifications, not item records:
we still GET each relevant item to obtain its current score. The listener records
notifications immediately; the minute timer processes the deduplicated queue.
Removing an ID from the updates feed is not interpreted as deletion of that story.

The pending FIFO queue is bounded by the tracked candidate universe, not by event
volume. Duplicate notifications coalesce; overflow waits for later batches. An update
arriving while its item is being fetched queues another read, so finishing the old
read cannot erase a newer notification. One shared refresh task owns cache changes.
The API and health probes never read that mutable cache, only its immutable snapshot.

For a successful item GET, null/deleted/dead/non-story/unusable data removes the old
cached record. HTTP errors, timeouts, and malformed responses retain the previous
record and requeue that ID for a later batch, with no immediate retry. Failures are
logged with item IDs. Other successful changes are merged with unchanged records.
Publish only after the membership call succeeds and a nonempty valid cache exists.
A complete immutable array is published in one atomic reference replacement. Reads
during refresh use the previous complete snapshot without upstream calls or waiting.
Ticks with no item work or membership changes retain the existing snapshot and timestamp.

Every `ReconciliationInterval`, all current candidates are queued for a **rolling**
recheck. The same batch ceiling and FIFO queue apply; this is not a second full-refresh
fan-out. A complete pass still requires one item GET per candidate, spread over ticks.
It catches missed notifications without fetching the whole list every minute.

Refresh triggers coalesce onto a shared task under a short lock. The worker awaits
each refresh before handling another timer tick; missed ticks coalesce, never overlap.
There are **no immediate item HTTP retries**: failed IDs wait for a later batch.
Failed/empty refreshes retain the last successful snapshot indefinitely, deliberately
favoring availability over a strict freshness SLA.

The stream uses a separate client with no overall lifetime timeout. Connection headers
are bounded by `RequestTimeout`; reads are canceled after `StreamIdleTimeout` with no
data/keep-alive lines. EOF, transport errors, malformed events, Firebase cancellation,
and idle timeout trigger reconnects. Reconnect delay doubles from `StreamReconnectDelay`
to a 60-second cap (or the configured base delay if larger), resetting after a valid
data event. Reconnects do not trigger a full acquisition. Firebase redirects are handled
by the HTTP handler. Events over 1 MiB of assembled data are rejected and logged.

HN does not document replay cursors, retention, or complete change-delivery guarantees.
Streaming reduces polling gaps but is **not** a durable change log. Notifications before
initial membership is known may be ignored; notifications during item acquisition are
preserved. Disconnect gaps and bootstrap gaps are recovered by rolling reconciliation.
There is no guarantee of an instantaneous globally consistent ranking from separate
item GETs. Ordering uses the latest successfully acquired score of each cached item.

`/health/live` reports process liveness; `/health/ready` returns 503 until a snapshot
exists and remains healthy while stale data is usable. Neither probe calls upstream.
Readiness check data records load time and age; publication/failure/wait events are
structured logs. The default health HTTP response is the status text.

## Configuration

Strongly typed `HackerNews` options are validated at startup. Override using
`appsettings.json` or environment variables such as `HackerNews__MaxUpstreamConcurrency`.

| Setting | Default | Constraint |
|---------|---------|------------|
| `BaseUrl` | `https://hacker-news.firebaseio.com/v0/` | Absolute HTTP(S) URL ending in `/` |
| `RefreshInterval` | `00:01:00` | Positive, within timer range; membership check and batch cadence |
| `RequestTimeout` | `00:00:10` | Positive, within HttpClient timeout range |
| `InitialLoadWaitTimeout` | `00:00:15` | Positive, within timer range |
| `MaxUpstreamConcurrency` | `10` | At least 1 |
| `MaximumStoryCount` | `100` | At least 1 |
| `UpdateBatchSize` | `100` | At least 1; item reads per incremental tick |
| `ReconciliationInterval` | `00:30:00` | Positive, within timer range; enqueue rolling rechecks |
| `StreamReconnectDelay` | `00:00:05` | Positive, within timer range; base reconnect backoff |
| `StreamIdleTimeout` | `00:01:00` | Positive, within timer range; stream read inactivity |

`RequestTimeout` bounds each upstream request, not the complete refresh. A large list
can take several request-timeout batches to load; initial waits are independently bounded.

## Tests

Unit tests cover mapping/filtering, sorting, partial failures, atomic publication,
stale fallback, single-flight initialization/refresh, cancellation, wait timeout,
bounded item concurrency, incremental batch limits, deduplication, irrelevant updates,
membership changes, confirmed deletion, retry backlog, and notifications during
bootstrap/item reads. SSE tests cover initial `put`, nested `patch`, multiline data,
profiles, keep-alives, errors, cancellation, idle timeout, and reconnect backoff.
Rolling reconciliation tests prove all candidates are eventually visited across batches.
In-process `WebApplicationFactory` tests cover HTTP
validation, exact response fields, 503, health, OpenAPI, and 120 concurrent requests.
All upstream calls use stubbed handlers. Timer tests use `FakeTimeProvider`; concurrency
tests use gates/channels, not arbitrary sleeps. Safety deadlines only detect hung tests.
The concurrency test measures current/maximum in-flight requests and asserts both
the configured ceiling and real parallelism. These are correctness tests, not throughput benchmarks.

## Scaling, assumptions, and limitations

Request rate, deployment topology, freshness SLA, maximum count, and cross-node
consistency were not specified. The defaults above are deliberate assumptions.
Each node owns an independent in-memory snapshot and refresher; adding nodes multiplies
upstream acquisition traffic and nodes may briefly return different snapshots.
Two or three nodes remain simple; there is no shared refresh ownership or consistency guarantee.

Redis would add deployment, network, serialization, availability, and lease-management
complexity without a specified requirement. At larger scale, a dedicated refresher
could publish to a distributed cache, with API nodes reading a shared snapshot and a
single owner/lease preventing cross-node stampedes. This is not implemented.

The service is not a full Hacker News ranking engine: it sorts only the upstream best
list, and partial refreshes may contain fewer stories. It has no maximum stale age.
`LoadedAt` is the snapshot publication time, not a guarantee that every included item
was retrieved at that time. Traffic is approximately one membership GET per tick plus
up to `UpdateBatchSize` item GETs, excluding the one-time full initial load and stream
reconnects. If changes arrive faster than batches drain, freshness falls behind.
Concurrency and batch limits are not a strict requests-per-second limit. Rolling
reconciliation has no hard completion deadline; for a million candidates at 100 per
minute it would take at least 10,000 ticks, even without other pending work. Full
in-memory caching and sorting still limit scale; this is not a million-record storage
or export implementation. Increasing a caller's count never expands the source dataset
or triggers acquisition.
Future work, guided by real requirements: snapshot-age/refresh-duration metrics,
OpenTelemetry, controlled load testing, CI/dependency scanning, freshness budgets,
and measured retry/circuit-breaker policies.

## Optional container

```powershell
docker build -t hackernews-api .
docker run --rm -p 5080:8080 hackernews-api
```

The multi-stage image runs as the ASP.NET image's non-root user. Configure platform
health probes against port 8080: `/health/live` for liveness and `/health/ready` for
readiness. The image does not install curl or add a Docker-specific health command.