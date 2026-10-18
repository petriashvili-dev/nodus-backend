# Эксплуатация

## Конфигурация

Значения по умолчанию — в `Nodus.Api/appsettings.json`. Переопределение — через environment: `Секция__Ключ`, элементы списков по индексу (`Llm__Providers__0__ApiKey`).

### Обязательные секреты

| Переменная | Что это |
|---|---|
| `ConnectionStrings__Postgres` | строка подключения PostgreSQL |
| `Jwt__SigningKey` | ключ подписи access-токенов, ≥ 32 байт (`openssl rand -base64 48`) |
| `EmailOtp__HashPepper` | секрет для хешей кодов (email, приглашения) и ключей AI-кеша, ≥ 16 символов. **Не менять** на работающей системе: выданные коды и кеш станут недействительными |

Без них приложение не запустится (проверка при старте).

### Интеграции (необязательные)

| Переменные | Без них |
|---|---|
| `Resend__ApiKey`, `Resend__From` | письма пишутся в лог вместо отправки |
| `Apns__KeyId`, `Apns__TeamId`, `Apns__BundleId`, `Apns__PrivateKey` (содержимое `.p8`) | push пропускаются |
| `Llm__Providers__0__ApiKey`, `__Model` (neuroapi), `Llm__Providers__1__ApiKey`, `__Model` (Timeweb) | AI-задания завершаются `ai_unavailable`, продолжения — только по шаблонам |
| `EmailOtp__ReviewEmails__0`, `EmailOtp__ReviewCode` | демо-аккаунта для App Review нет; задавать **только на время ревью**, см. [02 → Демо-аккаунт](02-authentication.md#демо-аккаунт-для-app-review) |

### Основные параметры

| Секция | Ключевые параметры |
|---|---|
| `Jwt` | `AccessTokenMinutes` 15, `RefreshTokenDays` 60, `RotationGraceSeconds` 120 |
| `EmailOtp` | `CodeExpiryMinutes` 10, `ResendCooldownSeconds` 60, `MaxCodesPerHour` 5, `MaxAttempts` 5, `ReviewEmails` [] |
| `RateLimits` | `AuthPerMinute` 20, `OtpPerMinute` 10, `AiPerMinute` 10 |
| `Occurrences` | `HorizonDays` 45, `MaxCompletionBackdateDays` 30 |
| `Connections` | `InviteLifetimeHours` 24, `MaxPendingInvites` 10, `InviteLinkBase` |
| `Ai` | `MaxTextLength` 4000, `MaxJobsPerDay` 50, `MaxAttempts` 3, `DraftRetentionDays` 14, `InputRetentionHours` 24 |
| `AiContinuations` | `Enabled`, `DebounceSeconds` 20, `MaxLlmCallsPerUserPerDay` 30 |
| `AppConfig` | `CacheSeconds` 15 — через сколько изменение `config set` увидят клиенты; см. [12-app-config.md](12-app-config.md) |
| `Plans` | `Standard:Features` [], `Plus:Features` — все AI-функции; см. [11-plans-and-features.md](11-plans-and-features.md) |
| `Import` | `MaxContentChars` 200 000, `MaxAiInputChars` 30 000, `MaxNodes` 1000, `MaxAiImportsPerDay` 5, `MaxAttempts` 3, `PreviewRetentionDays` 7 |
| `Llm` | `TotalTimeoutSeconds` 150, `CircuitFailureThreshold` 5, `CircuitBreakSeconds` 60, `LogContent` false, `Providers[]` |
| `Outbox` | `PollSeconds` 5, `BatchSize` 100, `MaxAttempts` 10 |
| `Sse` | `HeartbeatSeconds` 20, `RetryMilliseconds` 3000, `MaxConnectionsPerUser` 5 |
| `Retention` | см. ниже |
| `Observability` | `PrometheusEnabled`, `MetricsPort` (отдельный внутренний порт для `/metrics`), `OtlpEndpoint`, `JsonLogs` |
| `ReverseProxy` | `TrustForwardedHeaders` — включить за reverse proxy |

## Запуск локально

```bash
docker run -d --name nodus-test-pg -p 55432:5432 -e POSTGRES_USER=nodus -e POSTGRES_PASSWORD=nodus -e POSTGRES_DB=nodus postgres:16
export ConnectionStrings__Postgres="Host=localhost;Port=55432;Database=nodus;Username=nodus;Password=nodus"
export Jwt__SigningKey="$(openssl rand -base64 48)"
export EmailOtp__HashPepper="$(openssl rand -base64 24)"
dotnet run --project Nodus.Api -- migrate
dotnet run --project Nodus.Api
```

## Миграции

EF Core, `Nodus.Api/Infrastructure/Persistence/Migrations/`. API **не** применяет миграции при старте — это отдельный шаг релиза:

```bash
dotnet run --project Nodus.Api -- migrate      # локально
docker run --env-file .env nodus-api migrate   # в контейнере
```

Новая миграция: `cd Nodus.Api && dotnet ef migrations add <Name> -o Infrastructure/Persistence/Migrations`.

## Выдача плана Nodus+

```bash
dotnet run --project Nodus.Api -- plan set --email user@example.com --plan plus [--expires 2027-01-01T00:00:00Z]
dotnet run --project Nodus.Api -- plan set --user-id <uuid> --plan standard
dotnet run --project Nodus.Api -- plan list
docker compose run --rm nodus-api plan set --email user@example.com --plan plus   # на сервере
```

Подробности и коды выхода — [11-plans-and-features.md](11-plans-and-features.md#выдача-плана).

## Конфигурация клиента

```bash
dotnet run --project Nodus.Api -- config set links.privacy_policy https://nodus.app/privacy
dotnet run --project Nodus.Api -- config set features.import_enabled false --json
dotnet run --project Nodus.Api -- config list
docker compose run --rm nodus-api config set min_supported_version 1.2.0   # на сервере
```

Клиенты увидят изменение в течение `AppConfig:CacheSeconds`. Подробности — [12-app-config.md](12-app-config.md).

До первого развёртывания все миграции схлопнуты в одну `InitialCreate`; дальше — только новые миграции поверх, уже выпущенные не редактируются. CI проверяет, что модель и миграции совпадают (`dotnet ef migrations has-pending-model-changes`).

## CI/CD (GitHub Actions)

`.github/workflows/deploy.yml`, по схеме eatee-backend. Запускается только при мердже в `main`; пуши в `dev` CI не запускают — тесты там гоняются локально перед коммитом. Деплои идут по одному (новый мердж ждёт текущий, не отменяет его).

| Задача | Когда | Что делает |
|---|---|---|
| `test` | мердж (push) в `main` | restore, build Release, проверка миграций, `dotnet test` на PostgreSQL 16 (сервис в job, `NODUS_TEST_POSTGRES`), отчёт `.trx` в артефактах |
| `deploy` | тот же запуск, **только после зелёного `test`** | сборка образа (`GIT_SHA`, `BUILD_TIME`), push в GHCR с тегами `latest` и SHA коммита, вызов Dokploy `compose.deploy` |

Секреты репозитория: `DOKPLOY_URL` (например, `https://deploy.example.ru`), `DOKPLOY_API_TOKEN`, `DOKPLOY_COMPOSE_ID`. `GITHUB_TOKEN` выдаётся автоматически (права `packages: write`).

Откат — передеплой предыдущего тега `ghcr.io/petriashvili-dev/nodus-backend:<sha>`.

## Docker

- `Nodus.Api/Dockerfile` — multi-stage, процесс не от root, `tzdata`, `libgssapi`, healthcheck на `/health/live`, порт 8080.
- `docker-compose.yml` — развёртывание (staging/production) по схеме eatee-backend: Dokploy + Traefik, образ `ghcr.io/petriashvili-dev/nodus-backend:latest`, секреты в `.env` рядом (шаблон — `.env.example`, сам `.env` в git не попадает).
- `docker-compose.local.yml` — локальная сборка образа из исходников, API на `127.0.0.1:8080`.

### Схема развёртывания

```mermaid
flowchart LR
    DEV["git push"] --> GH["GitHub"]
    GH -- "CI: test → build" --> GHCR[("GHCR<br/>nodus-backend:latest")]
    GH -. "deploy webhook" .-> DOK["Dokploy"]

    subgraph Server["Сервер (Docker)"]
        TR["Traefik<br/>:80 → :443, TLS"]
        subgraph Compose["docker-compose.yml"]
            MIG["nodus-migrate<br/>command: migrate<br/>(одноразовый)"]
            API["nodus-api<br/>:8080 API/SSE<br/>:9464 /metrics"]
            DB[("nodus-db<br/>postgres:16<br/>volume nodus_postgres_data")]
        end
    end

    DOK -- "docker compose up" --> Compose
    GHCR -- "pull" --> MIG & API
    Internet["iOS-клиенты"] -- "HTTPS ${NODUS_DOMAIN}" --> TR -- "dokploy-network" --> API
    DB -- "healthy" --> MIG -- "completed successfully" --> API
    API -- "default network" --> DB
    API --> EXT["APNs · neuroapi.host · Timeweb · Resend"]
```

Порядок старта: `nodus-db` (healthcheck `pg_isready`) → `nodus-migrate` применяет миграции и завершается → `nodus-api` стартует только после успешной миграции. Traefik маршрутизирует наружу только порт 8080; `/metrics` на 9464 доступен лишь внутри Docker-сети (`nodus-api:9464`). PostgreSQL наружу не открыт.

```bash
cp .env.example .env    # заполнить секреты и NODUS_DOMAIN
docker compose pull && docker compose up -d       # на сервере (или через Dokploy)
docker compose -f docker-compose.local.yml up -d --build   # локально
```

Имена контейнеров и volume с префиксом `nodus-`, чтобы не пересекаться с eatee на том же сервере Dokploy. Сеть `dokploy-network` — внешняя, её создаёт Dokploy.

## Health и остановка

- `GET /health/live` — процесс жив.
- `GET /health/ready` — есть связь с PostgreSQL.
- `GET /version` — `{ version, commit, build_time }`: какой коммит задеплоен (из build-args образа).
- На SIGTERM у SSE и воркеров Hangfire есть 30 с на завершение (`stop_grace_period` 40 с в compose). Клиенты SSE переподключаются и догоняют через sync.

## Хранение данных (retention)

Ежедневная задача `MaintenanceJob` (Hangfire, 03:00 UTC):

| Что | Срок | Параметр |
|---|---|---|
| Записи идемпотентности | 30 дней | `Retention:IdempotencyDays` |
| Журнал синхронизации (старше → `410` и snapshot) | 90 дней | `Retention:SyncChangeDays` |
| Обработанные события outbox | 7 дней | `Retention:ProcessedOutboxDays` |
| Email-коды | 7 дней после истечения | `Retention:EmailCodeDays` |
| Истёкшие refresh-токены | сразу | — |
| Текст AI-диктовки | 24 ч | `Ai:InputRetentionHours` |
| Неподтверждённые AI-черновики | 14 дней → `expired` | `Ai:DraftRetentionDays` |
| Зависшие AI-задания | 2 ч → `failed` | — |
| Кеш продолжений | 180 дней без использования | `Retention:ContinuationCacheDays` |
| Текст импортируемого файла | сразу после AI-конвертации | — |
| Неподтверждённый предпросмотр AI-импорта | 7 дней → `expired` | `Import:PreviewRetentionDays` |

Корректность не зависит от того, запустилась ли задача вовремя.

## Резервные копии

```bash
docker compose exec -T postgres pg_dump -U nodus -Fc nodus > nodus-$(date +%F).dump
```

Проверка восстановления — регулярно, в отдельную БД:

```bash
docker compose exec -T postgres createdb -U nodus nodus_restore_check
docker compose exec -T postgres pg_restore -U nodus -d nodus_restore_check < nodus-YYYY-MM-DD.dump
docker compose exec -T postgres psql -U nodus -d nodus_restore_check -c "select count(*) from users; select max(revision) from sync_changes;"
```

## Логи

- Вне Development — JSON в stdout, время в UTC, со scope `RequestId` (из `X-Request-Id` клиента или сгенерированный, возвращается в ответе) и `IdempotencyKey`.
- **Не логируются**: содержимое узлов, текст диктовки, промпты и ответы LLM, токены и коды.

## Метрики

OpenTelemetry: `Observability:PrometheusEnabled=true` открывает `/metrics` (закройте его от интернета на proxy), `Observability:OtlpEndpoint` включает экспорт метрик и трассировок по OTLP.

| Метрика | Теги |
|---|---|
| `nodus.llm.requests`, `nodus.llm.latency` (мс) | `provider`, `purpose`, `outcome` |
| `nodus.llm.tokens` | `provider`, `kind` (prompt/completion) |
| `nodus.llm.cost` | `provider` |
| `nodus.outbox.events` | `type`, `outcome` (processed/failed/abandoned) |
| `nodus.outbox.lag` (с) | `type` — задержка от коммита до обработки |
| `nodus.idempotency.replays` | — |
| `nodus.sync.requests` | `kind` (delta/snapshot) |
| `nodus.push.deliveries` | `outcome` |
| `nodus.continuations` | `outcome` (ready/skipped × template/cache/llm) |
| `nodus.ai.jobs` | `kind` (drafts/import), `outcome` (ready, retry, discarded или код ошибки) |
| `nodus.feature.denied` | `feature`, `reason` (not_in_plan/consent_required) |

Плюс стандартные метрики ASP.NET Core, HttpClient и runtime.

## Ограничения MVP

Один экземпляр API, без HA: отказ контейнера или сервера останавливает sync, SSE и фоновые задачи. iOS продолжает работать локально, оба outbox (клиента и сервера) догоняют после восстановления. Для масштабирования: вынести Hangfire server в отдельный процесс, заменить `InMemoryRealtimeHub` на LISTEN/NOTIFY или брокер.

## Тесты

```bash
dotnet test        # нужен PostgreSQL на 55432 или NODUS_TEST_POSTGRES
```

| Набор | Что покрывает |
|---|---|
| `Recurrence/*` | календарь, конец месяца, високосный год, DST, история правок, случайные правила (property-based) |
| `Integration/AuthTests` | анонимный вход, ротация и reuse refresh, email, вход на новом устройстве |
| `Integration/NodeSyncTests` | идемпотентность и гонки, конфликты версий, batch, untie, курсор/tombstones, изоляция пользователей |
| `Integration/RealtimeAndPushTests` | устройства, push и мёртвые токены, SSE |
| `Integration/SharingTests` | приглашения, встречные приглашения, права участников, отзыв доступа |
| `Integration/AiDraftTests`, `Ai/*` | AI-задания, fallback LLM, разбор ответа, подтверждение |
| `Integration/ContinuationTests` | конвейер, кеш, устаревание, принятие |
| `Integration/PlanTests`, `Plans/*` | доступ по плану и согласию, истечение плана, отказ без сохранения ключа, неявный запуск продолжений, сохранение начатых заданий, sync профиля, команда `plan` |
| `Integration/AppConfigTests` | публичный конфиг, лимиты, типы значений, изменения без перезапуска, ETag/`304`, команда `config` и проверка ввода |
| `Integration/ReviewAccountTests` | фиксированный код для демо-аккаунта App Review без письма, лимит попыток, валидация настроек |
| `Integration/ExportImportTests` | только активные узлы в экспорте, round trip без дублей, дефолты, версии, AI-импорт с предпросмотром |
| `Integration/FailureAndSecurityTests` | повторная доставка outbox, поддельные JWT, перебор id, лимиты тела |

Каждый тестовый набор создаёт свою временную БД и удаляет её после прогона.

Нагрузочный тест (не входит в `dotnet test`):

```bash
dotnet run -c Release --project Nodus.LoadTests -- http://localhost:5258 50 60   # url, пользователи, секунды
```

Локальный прогон (ноутбук, PostgreSQL в Docker, 50 пользователей): около 1 300 запросов в секунду, p95 от 25 до 59 мс по операциям, без ошибок. Цель из требований — p95 < 300 мс; на целевом сервере результат нужно перепроверить. Для прогона поднимите `RateLimits__AuthPerMinute`.
