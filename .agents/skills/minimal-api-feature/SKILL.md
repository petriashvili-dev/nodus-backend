---
name: minimal-api-feature
description: Step-by-step recipe for adding or changing a backend feature in this repository following its conventions — module layout, Minimal API endpoints returning ApiResult, validation and problem codes, idempotent mutations through UnitOfWork and ChangeJournal (sync), side effects via outbox, access checks via NodeAccess, options/config, migrations, tests and docs. Use when implementing any new endpoint or feature.
---

# Adding a feature

Follow `AGENTS.md`. Typical order:

## 1. Design

- Which module (`Nodus.Api/<Module>/`)? New module = new folder + `Map<Module>Endpoints` + registrations in `NodusApplication.cs`.
- Contract first: request/response records (snake_case on the wire comes from `NodusJson`), status codes, problem `code`s.
- Who may do it? Who must see the change (sync recipients)? What happens after commit (push, job)?

## 2. Data

- Entity + configuration in `NodusDbContext`; migration (skill `ef-migrations`).
- Version column + concurrency token if clients edit it concurrently.

## 3. Endpoint

```csharp
group.MapPost("/things", (CreateThingRequest request, HttpContext http, ThingService service, CancellationToken ct) =>
    service.CreateAsync(http.User.GetUserId(), request, ct)).WithIdempotency();
```

- Handlers are thin; logic in a scoped service returning `ApiResult`.
- User id **only** from `http.User.GetUserId()`.
- Validation: `ValidationErrors` → `400 validation_failed`; business errors: `ApiResult.Problem(status, "snake_code", "Title.")`. Codes are stable API — document them.
- Mutations: `.WithIdempotency()` (except naturally idempotent device/auth endpoints). Return `ApiResult` (the filter persists and replays it).
- Rate limits for expensive/abusable endpoints: `.RequireRateLimiting(RateLimits.Ai|Otp|Auth)`.
- Query parameters in snake_case: `[FromQuery(Name = "expected_version")]`.

## 4. Service

- Access: `NodeAccess.FindAsync` / `Accessible` for nodes; for other owned entities filter by `UserId` and return `404` for foreign ids (indistinguishable from missing).
- Mutate tracked entities, bump `Version`, record sync changes:
  ```csharp
  unitOfWork.Journal.Record(SyncEntityType.X, id.ToString(), entity.Version, SyncOperation.Upsert, recipients);
  await unitOfWork.CommitAsync(ct);
  ```
  New entity type → add to `SyncEntityType` and an `ISyncEntityProvider` in the module (bulk load + access check; picked up automatically by the resolver); add it to `/sync/snapshot` if clients need it at first launch. Do not edit the delta loop in `SyncEndpoints`.
- After-commit work → `db.OutboxEvents.Add(OutboxEvent.Create(type, payload, now))` + an `IOutboxHandler` that enqueues via `IBackgroundJobs` (skill `background-jobs`). Pushes → `PushNotifier.Request(...)` with lock-screen-safe text.
- Time from `TimeProvider`; zones via `ZonedTime`.
- AI output never creates user data without explicit confirmation.

## 5. Config

- Options class with `Section` const, bound in `NodusApplication`, defaults in code **and** `appsettings.json`; secrets only via environment (add to `.env.example`).

## 6. Tests (skill `integration-tests`)

Happy path, validation, foreign user (`404`), idempotent replay, concurrency/conflict where relevant, sync delivery to recipients.

## 7. Docs

- `docs/<area>.md` (Russian, how it works), `README.md` API/decisions tables, `docs/13-environment.md` config.
- Client contract changed → `docs/ios/` (+ `09-api-reference.md`) and `~/Desktop/nodus_docs/03_Nodus_iOS_Requirements.md`.

## 8. Verify and hand over

`dotnet build Nodus.sln` (no CS warnings) and `dotnet test` green → report → commit and push to `dev` only after the user confirms.
