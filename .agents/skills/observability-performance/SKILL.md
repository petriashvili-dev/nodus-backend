---
name: observability-performance
description: Logging, metrics, tracing and performance work for this backend — structured logs with correlation ids and no PII, OpenTelemetry meters and tag cardinality, Prometheus/OTLP, health checks, SLOs (p95 < 300 ms), load testing with Nodus.LoadTests, finding slow queries with EXPLAIN ANALYZE, and common .NET performance pitfalls. Use when adding instrumentation, investigating latency or errors, or validating performance.
---

# Observability and performance

## Logs

- `ILogger<T>` with message templates (`"Draft job {JobId} failed: {Category}"`), never string interpolation.
- Scope already carries `RequestId` and `IdempotencyKey`; log ids and categories, **never** titles, descriptions, dictated text, prompts, file contents, emails, tokens, codes.
- Levels: `Information` for business milestones (rare), `Warning` for recoverable failures, `Error` for failures needing attention. No logging in hot loops.

## Metrics

- Add instruments to `Infrastructure/Observability/NodusMetrics.cs` (meter `Nodus.Api`): counters for events, histograms for durations (ms/s), names `nodus.<area>.<thing>`.
- Tags: low-cardinality categories only (`provider`, `outcome`, `type`) — never user ids, node ids, free text.
- An instrument appears in `/metrics` only after its first measurement.
- Export: `Observability:PrometheusEnabled` (+ `MetricsPort` 9464 internal) and/or `OtlpEndpoint`.

## Health

`/health/live` (process), `/health/ready` (PostgreSQL). Readiness must stay cheap; do not add external dependencies (LLM, APNs) to it.

## Performance targets and testing

- SLO: p95 < 300 ms for non-LLM endpoints at target load.
- Load test: `dotnet run -c Release --project Nodus.LoadTests -- <url> <users> <seconds>` against a Release build; raise `RateLimits__AuthPerMinute` for the run. Compare p50/p95/p99 before and after a change.

## Finding slow spots

1. Reproduce with the load test or a single request + timing.
2. Enable EF command logging locally (`Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information`) and look for N+1 or repeated queries.
3. `EXPLAIN (ANALYZE, BUFFERS)` the slow SQL in `psql`; add/adjust indexes; prefer keyset pagination.
4. `dotnet-counters monitor -n Nodus.Api` for GC/thread-pool starvation; `dotnet-trace` for CPU hot paths.

Common culprits: queries in loops, missing `AsNoTracking`, loading whole histories when a window suffices, sync-over-async, unbounded result sets, per-request `JsonSerializerOptions` creation, regexes without `GeneratedRegex`.

## Checklist

- [ ] New background flow has a metric with an outcome tag.
- [ ] Errors are logged once, with ids and category, without user content.
- [ ] Hot endpoints checked for query count and indexes.
- [ ] Load test numbers attached to performance-sensitive changes.
