---
name: csharp-dotnet
description: Modern C# 14 / .NET 10 idioms and pitfalls for writing or reviewing C# code — nullable reference types, records, primary constructors, pattern matching, async/await and cancellation, LINQ, collections, disposal, exceptions, DI lifetimes. Use when writing new C# code, refactoring, or reviewing C# for correctness and style in this repository.
---

# C# / .NET 10

Write code like the surrounding code in `Nodus.Api/`: file-scoped namespaces, primary constructors for services, `sealed` by default, records for DTOs, terse expression bodies, XML `<summary>` only where it explains *why*.

## Language

- **Nullable is on.** No `!` to silence warnings unless the invariant is proven nearby (comment why). Prefer `is { } x`, `is not null`, `?? throw`.
- **Records** for DTOs and value objects; `with` for copies. Override `Equals` explicitly when a record holds collections (sequence equality), see `RecurrenceRule`.
- **Primary constructors** for DI services; do not mutate captured parameters. Use explicit fields when a value is derived (`private readonly X _x = options.Value;`).
- **Pattern matching** (`switch` expressions, property/relational patterns) over `if` chains. Mind precedence: `a is X or Y && b` is `(a is X or Y) && b` — add parentheses/line breaks for clarity.
- **`is not var (x, y)` always matches** — use `is not { } t` then deconstruct.
- Collection expressions (`[]`, `[.. a, b]`) and `IReadOnlyList<T>` in public contracts.
- `required` / `init` for entities and options; no public setters on immutable data.
- `const`/`static readonly` for magic numbers; limits live in `Common/Limits.cs`.
- `string.Create(CultureInfo.InvariantCulture, $"…")` / `InvariantCulture` for machine strings (keys, ids, formats). Never format machine data with the current culture.

## Async and cancellation

- Every I/O method is `async Task`/`ValueTask` and takes `CancellationToken ct` last; pass it through. Never `.Result`/`.Wait()`/`GetAwaiter().GetResult()` (sync-over-async).
- No `async void` except event handlers. Fire-and-forget is forbidden — use the outbox + `IBackgroundJobs`.
- `await using` for async disposables (`DbContext` scopes, transactions).
- Catch `OperationCanceledException` only to stop gracefully (`when (ct.IsCancellationRequested)`); do not swallow other exceptions.
- Linked token sources for timeouts: `CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(...)` and distinguish own timeout from caller cancellation.
- Use `TimeProvider` (injected), never `DateTime.Now/UtcNow` or `Task.Delay` for logic that tests must control.

## DI lifetimes

- `DbContext`, `UnitOfWork`, services using them — **scoped**. Singletons must not capture scoped services (use `IServiceScopeFactory`, see `OutboxDispatcher`).
- Typed `HttpClient` via `AddHttpClient`; long-lived singletons create clients per call from `IHttpClientFactory` (DNS rotation).
- Infrastructure defaults that tests replace are registered with `TryAdd*`.

## Exceptions

- Expected outcomes (validation, not found, conflict) are **results** (`ApiResult.Problem`), not exceptions.
- Exceptions for broken invariants and infrastructure failures; never leak messages to clients (global handler returns `internal_error`).
- Map provider errors to domain categories at the boundary (`LlmException`, `ApnsResult`).
- `System.Text.Json`: `JsonElement.TryGetInt32` **throws** on non-number kinds — check `ValueKind == Number` first.

## LINQ and collections

- `ToLookup`/`ToDictionary` for joins in memory; avoid nested `First()` in loops (O(n²)).
- Materialise once (`ToList()`) before enumerating twice.
- `HashSet<string>(StringComparer.Ordinal)` for keys.

## Review checklist

- [ ] Nullability warnings: zero. Build: `dotnet build Nodus.sln` without CS warnings.
- [ ] Every async path takes and forwards `CancellationToken`.
- [ ] No `DateTime.UtcNow`, `Random` in logic without seam, no culture-sensitive formatting of machine data.
- [ ] No user content in logs.
- [ ] Public contracts use read-only collections; DTOs are records.
- [ ] Names and comment density match neighbouring files.
