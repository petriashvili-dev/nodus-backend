---
name: background-jobs
description: Reliable asynchronous processing — transactional outbox, at-least-once delivery, idempotent handlers and Hangfire jobs, queues, retries with backoff and jitter, cancellation races, re-check after slow external calls, retention jobs. Use when work must happen after a commit (push, AI, imports), when adding a Hangfire job, or when debugging lost/duplicated jobs.
---

# Outbox and background jobs

## Rule

Nothing slow or external runs inside a request, and nothing is enqueued "after commit" directly. Instead:

```text
request transaction:  data + sync_changes + outbox_events(type, payload)  → COMMIT
OutboxDispatcher:     SELECT … FOR UPDATE SKIP LOCKED → IOutboxHandler → IBackgroundJobs.Enqueue/Schedule
Hangfire job:         does the work, writes results through UnitOfWork (+ new outbox events)
```

This prevents the classic loss between commit and enqueue, and duplicate side effects on retries.

## Adding a job

1. Outbox event type const in `OutboxEventTypes` (`"area.verb"`), JSON payload record (ids only, no user content if avoidable).
2. `IOutboxHandler` — fast and idempotent: deserialize, `jobs.EnqueueAsync<TJob>(j => j.RunAsync(id, CancellationToken.None), HangfireQueues.X)` or `ScheduleAsync(..., delay)` (queue from `[Queue]`).
3. Job class (scoped, registered) with `[AutomaticRetry(Attempts = n, DelaysInSeconds = [...])]` and `[DisableConcurrentExecution]` when it mutates one entity.
4. New queue → `HangfireQueues` + `All`.

## AI-backed jobs

Derive from `Infrastructure/Llm/AiJobRunner<TJob, TOutput>` (entity implements `IAiJob`): it already does the pending check, attempt counting, transient-vs-permanent handling, the `FOR UPDATE` re-check and `nodus.ai.jobs` metrics. Implement only status mapping, `BuildRequest`, `Parse`, `CompleteAsync`, `Fail`.

## Writing idempotent jobs

- Start by loading state and **exit if already done** (`status is not (Queued or Processing)`).
- Persist progress (`Processing`, `Attempts++`) before slow calls.
- After a slow external call: open a transaction, re-read the row `FOR UPDATE`, and drop the result if the entity changed (cancelled, edited — compare status/content hash/version).
- Replace, do not append, derived rows on retry (delete old drafts, insert new).
- Transient failure and attempts left → record category, `throw` (Hangfire retries). Permanent or exhausted → set `Failed` with a code, notify, return.
- Retries can duplicate external effects (push): include an `event_id` for client-side dedupe; minimise by throwing only when nothing succeeded.

## Debounce and staleness

Schedule with a delay and carry the content hash/version in the payload; the job does nothing if the entity no longer matches (see `ContinuationRunner`).

## Retention

Periodic cleanup in `MaintenanceJob` (daily, `maintenance` queue). Correctness must not depend on it running on time — it only deletes or expires.

## Tests

Hangfire is disabled in tests; `InlineBackgroundJobs` runs jobs during `DispatchBatchAsync`. Cover: success, transient retry (outbox backoff), permanent failure, cancellation during processing, duplicate enqueue.

## Checklist

- [ ] Side effect written to the outbox in the same transaction as the data.
- [ ] Handler and job are idempotent; duplicate delivery is harmless.
- [ ] Re-check after slow calls; cancellation respected.
- [ ] Status visible to clients via sync (+ push if the user may have left).
- [ ] No user content in logs; error categories only.
