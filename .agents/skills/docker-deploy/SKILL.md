---
name: docker-deploy
description: Containers and releases for this backend — Dockerfile practices, docker-compose for Dokploy + Traefik (as in eatee-backend), .env and secrets, release order with the one-shot migrate service, health checks, graceful shutdown, image tags and rollback, PostgreSQL backups and restore checks, local runs. Use when changing Dockerfile/compose, preparing a release, deploying, or troubleshooting containers.
---

# Docker and deployment

Files: `Nodus.Api/Dockerfile`, `docker-compose.yml` (deploy), `docker-compose.local.yml` (local build), `.env.example`. Details: `docs/09-operations.md`.

## Dockerfile

- Multi-stage: SDK restore with only the `.csproj` copied first (layer cache), then publish `-c Release /p:UseAppHost=false`.
- Runtime `mcr.microsoft.com/dotnet/aspnet:10.0`, non-root `$APP_UID`, `tzdata` (IANA zones), `libgssapi-krb5-2` (Npgsql), `curl` for `HEALTHCHECK /health/live`.
- No secrets in images or build args; config via environment.

## Compose (Dokploy + Traefik)

- Image `ghcr.io/petriashvili-dev/nodus-backend:latest`, `pull_policy: always`, `env_file: .env`.
- Order: `nodus-db` (healthcheck `pg_isready`) → `nodus-migrate` (`command: ["migrate"]`, exits) → `nodus-api` (`depends_on: service_completed_successfully`).
- Traefik labels route only port 8080 for `${NODUS_DOMAIN}` with HTTPS redirect; `/metrics` on 9464 stays internal; PostgreSQL only on the default network.
- Prefixed names (`nodus-*`) to coexist with eatee on the same host; `dokploy-network` is external.
- `stop_grace_period`: API 40 s (SSE/Hangfire drain within 30 s), PostgreSQL 1 m (checkpoint).

Validate before committing:

```bash
cp .env.example .env   # with dummy values, then delete
docker compose -f docker-compose.yml config --quiet
docker compose -f docker-compose.local.yml config --quiet
```

## Release

1. Tests green locally on `dev` (CI does not run on `dev`); merge to `main` — that triggers the workflow.
2. `.github/workflows/deploy.yml`: `test` job (PostgreSQL service, migration check) → `deploy` job builds the image with `GIT_SHA`/`BUILD_TIME`, pushes `latest` and `<sha>` to GHCR and calls Dokploy (`DOKPLOY_URL`, `DOKPLOY_API_TOKEN`, `DOKPLOY_COMPOSE_ID` secrets).
3. Before risky migrations: `pg_dump` (see below).
4. After deploy: `/version` shows the new commit, `/health/ready` = Healthy, logs without errors, smoke request (`POST /v1/auth/anonymous` on staging).

## Rollback

- Redeploy the previous image tag (keep SHA tags for this). Migrations are forward-only: a rollback image must tolerate the newer schema (expand/contract, skill `ef-migrations`), otherwise restore the backup.

## Backups

```bash
docker compose exec -T nodus-db pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB" > nodus-$(date +%F).dump
# restore check into a scratch database, regularly
docker compose exec -T nodus-db createdb -U "$POSTGRES_USER" nodus_restore_check
docker compose exec -T nodus-db pg_restore -U "$POSTGRES_USER" -d nodus_restore_check < nodus-YYYY-MM-DD.dump
```

## Troubleshooting

| Symptom | Check |
|---|---|
| API restarts in a loop | missing required secret (`Jwt__SigningKey`, `EmailOtp__HashPepper`) — options validation fails at start |
| API never starts | `nodus-migrate` failed: `docker logs nodus-migrate` |
| `Failed to connect to 127.0.0.1:5432` | connection string not applied (env interpolation, shell quoting) |
| `libgssapi_krb5.so.2` message | package missing in the runtime image |
| SSE drops behind proxy | proxy buffering/timeouts; heartbeat every 20 s should keep it alive |
| wrong client IPs in rate limits | `ReverseProxy__TrustForwardedHeaders=true` behind Traefik |
