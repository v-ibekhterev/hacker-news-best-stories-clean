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
BackgroundService -> shared refresh task -> typed HackerNewsClient -> Hacker News
                        bounded item concurrency -> map/filter/sort -> atomic swap
```

The hosted worker starts loading immediately. Initial callers share one load task and
wait at most 15 seconds each; caller cancellation or wait timeout does not cancel work
shared by other callers. After a failed first attempt, reads remain unavailable until
the worker successfully refreshes; repeated API calls do not retry the upstream.
Host shutdown cancels upstream work.

Every refresh retrieves all IDs, then items with bounded `Parallel.ForEachAsync`.
An item HTTP error, timeout, malformed JSON, or unusable item does not discard other
successful items; failures are logged with the item ID. Publish only after the ID call
succeeds and at least one valid story is available. A complete immutable array is
published in one atomic reference replacement. Reads during refresh use the previous
complete snapshot without upstream calls or waiting.

Refresh triggers coalesce onto a shared task under a short lock. The worker awaits
each refresh before handling another timer tick; missed ticks coalesce, never overlap.
There are **no automatic HTTP retries**: the next periodic refresh retries acquisition.
Failed/empty refreshes retain the last successful snapshot indefinitely, deliberately
favoring availability over a strict freshness SLA.

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
| `RefreshInterval` | `00:01:00` | Positive, within timer range |
| `RequestTimeout` | `00:00:10` | Positive, within HttpClient timeout range |
| `InitialLoadWaitTimeout` | `00:00:15` | Positive, within timer range |
| `MaxUpstreamConcurrency` | `10` | At least 1 |
| `MaximumStoryCount` | `100` | At least 1 |

`RequestTimeout` bounds each upstream request, not the complete refresh. A large list
can take several request-timeout batches to load; initial waits are independently bounded.

## Tests

Unit tests cover mapping/filtering, sorting, partial failures, atomic publication,
stale fallback, single-flight initialization/refresh, cancellation, wait timeout,
and bounded item concurrency. In-process `WebApplicationFactory` tests cover HTTP
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