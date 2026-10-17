---
name: ef-migrations
description: Create, review and ship EF Core migrations safely for this repository — generation command and output folder, reviewing generated SQL, zero-downtime patterns (expand/contract), data backfills, rollback, and the release-time migrate step. Use whenever the data model changes or a migration fails.
---

# EF Core migrations

## Create

```bash
cd Nodus.Api
dotnet ef migrations add <PascalCaseName> -o Infrastructure/Persistence/Migrations
dotnet ef migrations has-pending-model-changes      # must say: no changes
```

- One migration per feature/commit; name by intent (`ImportJobs`, `UserTimezone`).
- Never edit a migration that is already on `main`/deployed — add a new one.
- Unpushed and unapplied? Fix by `dotnet ef migrations remove --force` and regenerate.
- The design-time factory (`NodusDbContextFactory`) needs no database.

## Review the generated code

- Column types, lengths, nullability, defaults are what you intended (`dotnet ef migrations script <From> <To>` to read SQL).
- Indexes and constraints present; filtered/partial indexes have the right predicate.
- Destructive operations (`DropColumn`, `DropTable`, type changes) are deliberate.
- New non-null column on a populated table has a default or a backfill.

## Safe change patterns (the app and the migration run in sequence, single instance)

| Change | Do |
|---|---|
| Add column | nullable or with default; backfill in SQL in the same migration if small |
| Rename column | add new → copy → switch code → drop old in a later release |
| Make nullable → not null | backfill first, then alter in a later migration |
| Drop column/table | remove code usage first, drop in a later release |
| Large backfill | batched SQL (`UPDATE … WHERE id IN (SELECT … LIMIT 10000)`), or a maintenance job |
| New unique index on existing data | clean duplicates first; consider `CREATE UNIQUE INDEX CONCURRENTLY` via `migrationBuilder.Sql` with `suppressTransaction: true` |

Use `migrationBuilder.Sql(...)` for data fixes; keep them idempotent.

## Apply

- Releases: one-shot `migrate` command before the API starts (`docker-compose.yml` service `nodus.migrate`, or `dotnet run --project Nodus.Api -- migrate`). The API never migrates at startup in production.
- Tests apply all migrations to a fresh database per test class — a broken migration fails the suite.

## Rollback

- Prefer roll-forward (a new migration fixing the issue).
- Emergency: restore from the pre-release `pg_dump` (see `docs/09-operations.md`) and redeploy the previous image tag.
- `dotnet ef database update <PreviousMigration>` only on non-production databases.

## Checklist

- [ ] `has-pending-model-changes` clean after generating.
- [ ] Generated SQL reviewed; destructive steps intentional.
- [ ] Backward-compatible with the previous app version for the duration of the release.
- [ ] `dotnet test` green (migrations applied from scratch).
- [ ] `docs/` updated if tables/retention changed.
