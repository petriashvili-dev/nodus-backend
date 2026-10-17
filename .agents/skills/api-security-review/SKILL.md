---
name: api-security-review
description: Security review of backend changes against the OWASP API Security Top 10 adapted to this codebase — object-level authorization (IDOR), authentication and token handling, mass assignment, rate limiting and resource consumption, enumeration, injection, secrets and PII in logs, SSRF, push/AI data leakage. Use before committing endpoints that touch authorization, tokens, sharing, uploads, AI or external calls, or when asked for a security review.
---

# API security review

Go through the diff endpoint by endpoint.

## API1 — Object-level authorization (IDOR)

- Every lookup by id is scoped to the caller: nodes via `NodeAccess`, other entities `WHERE user_id = caller`.
- Foreign and missing ids return the **same** `404` body (test it).
- Ids in request bodies (`user_id` to share with) are validated against the caller's relations (active connection).
- Sync feed and snapshot apply the same access rules at read time.

## API2 — Authentication

- Identity only from the validated JWT (`GetUserId()`); never from body/query.
- Refresh tokens: hashed at rest, rotation, reuse detection; deleted accounts' tokens rejected immediately.
- OTP/codes: hashed with pepper, attempt limits, expiry, constant-time compare, identical responses for existing/non-existing accounts.

## API3 — Property-level authorization / mass assignment

- Request records contain only client-settable fields; server-owned fields (`owner_id`, `version`, `status`) are never bound from input.
- Responses do not expose internal or other users' data (no emails of connections, no dictated text, no token values).

## API4 — Unrestricted resource consumption

- Body size limits (`Limits`, per-endpoint `RequestSizeLimit` only where needed), list/batch caps, date-range caps.
- Rate limits on auth, OTP, AI and invites; daily quotas on LLM-backed features.
- Timeouts and deadlines on all outbound calls.

## API5 — Function-level authorization

Role checks inside services (owner vs member), not in clients: deleting a shared node, managing members, confirming a connection.

## API6 — Sensitive business flows

Invites single-use with expiry and mutual confirmation; account deletion clean-up for other users; AI never creates data without confirmation.

## API7 — SSRF / outbound calls

Outbound hosts come from configuration only (LLM base URLs, APNs, Resend); never from user input.

## API8 — Misconfiguration

- No CORS (native client); forwarded headers trusted only behind the configured proxy.
- `/metrics` not publicly routed; errors without stack traces; secrets only in environment (`.env.example` has placeholders only).

## API9 — Inventory

New endpoints listed in `README.md` and `docs/ios/09-api-reference.md`; versioned under `/v1`.

## API10 — Unsafe consumption of third-party output

LLM output is untrusted: strict parsing, schema/limit checks, validation with domain rules, no execution, user confirmation.

## Data protection

- Logs/metrics/traces: no node titles, descriptions, dictated text, prompts, file contents, tokens, codes, emails.
- Push payloads lock-screen safe; retention for sensitive inputs (AI text, import files).
- Injection: EF parameters / interpolated `FromSql`, never string-concatenated SQL.

## Output

List findings as: severity, endpoint/file:line, scenario (who can do what), fix. Add a negative test for every fixed issue.
