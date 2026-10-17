---
name: integration-tests
description: Writing and running unit and integration tests for this repository with xUnit — TestApp on TestServer with a disposable PostgreSQL database, fakes for LLM/APNs/email, ManualTimeProvider, explicit outbox dispatch, SSE stream reading, property-based style tests, and diagnosing failures from server logs. Use when adding tests, fixing failing tests, or verifying a feature end to end.
---

# Tests

## Run

```bash
docker start nodus-test-pg 2>/dev/null || docker run -d --name nodus-test-pg -p 55432:5432 \
  -e POSTGRES_USER=nodus -e POSTGRES_PASSWORD=nodus postgres:16
dotnet test                                   # all
dotnet test --filter "FullyQualifiedName~SharingTests"
dotnet test --no-build --filter "FullyQualifiedName~Name" --logger "console;verbosity=detailed"   # server logs/exceptions
```

`NODUS_TEST_POSTGRES` overrides the server connection. Each `TestApp` creates and drops its own database.

## Structure

- Unit: `Nodus.Tests/Recurrence`, `Ai`, `Notifications` — pure logic, no database.
- Integration: `Nodus.Tests/Integration` — `IClassFixture<TestApp>`; tests inside a class share the app/database and run sequentially.
- Need special settings or extra services? Build your own `TestApp { ExtraServices = … }`, set `Settings[...]`, and implement `IAsyncLifetime`.

## TestApp toolbox (`Integration/TestApp.cs`)

| Need | Use |
|---|---|
| a signed-in user | `await app.CreateUserAsync()` → `TestUser(Id, AccessToken, RefreshToken, Client)` |
| send JSON + Idempotency-Key | `client.PostJsonAsync(url, body, key)`, `SendJsonAsync(method, …)` |
| read JSON / problem code | `await response.ReadJsonAsync()`, `json.Code()` |
| control time | `app.Time.Set(...)` / `Advance(...)` — **set the clock before creating users** (tokens use the app clock) |
| emails / OTP codes | `app.Emails.LastCodeFor(email)` |
| APNs | `app.Apns.Sent`, `app.Apns.Outcomes[token] = ApnsOutcome.InvalidToken` |
| LLM | `app.Llm.Respond = request => "…json…"`, `app.Llm.Requests` |
| run outbox handlers/jobs | `await app.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(default)` (background dispatch and Hangfire are off; jobs run inline) |
| inspect the database | `await app.WithDbAsync(db => db.Nodes.CountAsync(...))` |

## What to cover for a feature

- happy path + response shape (snake_case fields);
- validation (`400 validation_failed`, field names);
- authorization: another user gets `404`/`403`, foreign ids are indistinguishable from missing;
- idempotency: same key replays byte-identically, no duplicates (`Idempotent-Replayed`);
- concurrency/conflicts (`409 version_conflict` with `current`);
- sync: recipients get `upsert`, users who lost access get `delete`;
- failure modes of external services via fakes (transient vs permanent);
- time-dependent rules with `ManualTimeProvider` (DST, day boundaries).

Property-style tests: deterministic `new Random(seed)`, many iterations, assert invariants (see `RecurrenceCalculatorTests`).

## Pitfalls

- Shared fixture state: unique emails/ids per test (`Guid.NewGuid()`), do not assume empty tables.
- Moving the clock backwards after issuing tokens makes them "not yet valid".
- SSE: `HttpCompletionOption.ResponseHeadersRead` + read lines with a timeout.
- Never weaken an assertion to make a test pass; find the bug (several real bugs were found this way).
