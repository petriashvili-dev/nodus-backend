---
name: architecture-decision
description: Making and recording architecture decisions — clarifying requirements and constraints, comparing options with explicit trade-offs (consistency, reliability, cost, complexity, scalability), choosing defaults for an MVP, and writing a short ADR or decisions-table entry. Use for design questions, new subsystems, cross-cutting changes, or when requirements leave open questions.
---

# Architecture decisions

## Process

1. **Restate the problem** in one paragraph: goal, users/clients affected, invariants that must hold.
2. **Constraints:** single API instance (no HA in MVP), PostgreSQL as the only state, offline-first iOS client, at-least-once delivery, privacy (no user content in logs), cost of LLM calls.
3. **Options** (2–3, including "do nothing"/simplest). For each: how it works, failure modes, consistency guarantees, operational cost, migration path later.
4. **Recommend one** with the reason; name what would make you revisit it.
5. **Ask the user** only when the choice is a product decision or irreversible; otherwise pick the sensible default and state it.
6. **Record** it.

## Heuristics for this system

- Correctness boundaries live in the database (constraints, row locks, transactions), not in in-memory coordination.
- Prefer deterministic recomputation over materialised derived state when it is cheap (occurrence projection from revisions).
- Asynchronous work goes through the outbox; realtime channels only signal, sync delivers data.
- Interfaces at infrastructure seams (`ILlmClient`, `IRealtimeHub`, `IBackgroundJobs`) so processes can be split later without touching modules.
- Strategy + resolver (`ISelectionResolver`) for variants selected by a key (format versions, providers).
- Version wire formats explicitly (`/v1`, `source` + `version` in files); never break old readers.
- Optimistic concurrency + explicit conflicts for user edits; idempotent last-writer-wins only for state toggles.
- MVP default: fewer moving parts. Add a broker, cache or separate service only with a measured need.

## Recording

- Small decisions: a row in the decisions table of `README.md` ("Принятые решения") and the relevant `docs/*.md`.
- Significant ones: `docs/adr/NNNN-title.md`:

```markdown
# NNNN. Title
Дата: YYYY-MM-DD · Статус: принято | заменено NNNN

## Контекст
## Решение
## Рассмотренные варианты
## Последствия (плюсы, минусы, что пересмотреть и когда)
```

Write in Russian, like the rest of `docs/`.
