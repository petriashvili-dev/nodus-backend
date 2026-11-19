---
name: efcore-postgres
description: EF Core 10 with Npgsql/PostgreSQL best practices — modelling (snake_case, jsonb, timestamptz, indexes, constraints), querying without N+1, tracking vs AsNoTracking, ExecuteUpdate/ExecuteDelete, raw SQL with FOR UPDATE / SKIP LOCKED, transactions and isolation, optimistic concurrency, unique-violation handling. Use when adding entities, writing queries, or diagnosing database correctness or performance issues.
---

# EF Core + PostgreSQL

Context: `Nodus.Api/Infrastructure/Persistence/NodusDbContext.cs`, snake_case via `EFCore.NamingConventions`, PostgreSQL 16.

## Modelling

- Configure entities in `OnModelCreating` next to the others; every string gets `HasMaxLength`; enums as strings (`HasConversion<string>().HasMaxLength(16)`).
- **Time:** `DateTimeOffset` in UTC only (`timestamptz`); a convention converter normalises offsets. Local calendar values are `DateOnly`/`TimeOnly` + IANA zone column.
- **JSON:** `jsonb` with an explicit `ValueConverter` + `ValueComparer` (see `RecurrenceRule`). Use `text`, not `jsonb`, when bytes must round-trip exactly (jsonb reorders keys — see `client_operations.response_body`).
- Integrity in the database, not only in code: unique indexes (filtered when nullable: `HasFilter("email IS NOT NULL")`), check constraints (`user_low_id < user_high_id`), FKs with explicit `OnDelete`.
- Concurrency: `Version` as `IsConcurrencyToken()` and increment it in code on every change.
- Index what you filter/order by (`(user_id, status)`, `(recipient_user_id, revision)` PK, partial index for pending outbox).

## Querying

- Reads: `AsNoTracking()`. Writes: load tracked, mutate, `SaveChanges` once per unit of work.
- **No N+1:** load related rows in bulk with `Contains(ids)` then `ToLookup` (see `NodeHistoryLoader`). Never query inside a loop over entities unless bounded and small.
- Project to what you need (`Select(n => new { … })`) for large reads.
- Bulk changes: `ExecuteUpdateAsync`/`ExecuteDeleteAsync` (no tracking, single statement). Remember they bypass the change tracker and concurrency tokens.
- Correlated subqueries are fine (`db.SharedNodeMembers.Any(m => m.NodeId == n.Id)`); check the plan if it is hot.
- Pagination by keyset (`revision > cursor ORDER BY revision LIMIT n+1`), not `OFFSET`.

## Raw SQL and locking

- `FromSql($"… {param} …")` / `SqlQuery<T>($"…")` — interpolation is parameterised; never concatenate strings. Scalar `SqlQuery<long>` needs the column aliased `AS "Value"`.
- Row locks: `SELECT … FOR UPDATE` inside an explicit transaction (invites, connections, job state re-check).
- Work queues: `FOR UPDATE SKIP LOCKED` (outbox dispatcher).
- Monotonic per-user counters: `UPDATE users SET x = x + n WHERE id = … RETURNING x` inside the transaction — the row lock orders commits (sync revisions). Lock multiple rows in a stable order (by id) to avoid deadlocks.

## Transactions

- One business operation = one transaction (`UnitOfWork.CommitAsync`; the idempotency filter owns the outer transaction for mutations).
- `ExecuteUpdate`/raw SQL that must be atomic with tracked changes runs inside the same explicit transaction.
- `RepeatableRead` for consistent multi-query snapshots (`/sync/snapshot`).
- After a DB error (unique violation) the PostgreSQL transaction is aborted: roll back, then decide.

## Errors

- Unique violation → `DbUpdateException.IsUniqueViolation()` → domain `409`, not `500`.
- `DbUpdateConcurrencyException` → `409 version_conflict`.

## Checklist

- [ ] Max lengths, indexes and constraints for every new column/filter.
- [ ] No queries in loops; bulk-load related data.
- [ ] `AsNoTracking` on read paths.
- [ ] Times in UTC; local values with zone.
- [ ] Migration generated and reviewed (skill `ef-migrations`).
- [ ] Integration test on real PostgreSQL covers the query (not an in-memory provider).
