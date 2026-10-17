# Архитектура

## Схема

Один процесс API (MVP, без HA) обслуживает всё:

```mermaid
flowchart LR
    iOS[iOS-клиент] -- HTTPS /v1 --> API[ASP.NET Core API]
    iOS -- SSE /v1/events --> API
    API -- EF Core --> PG[(PostgreSQL)]
    API -- outbox dispatcher --> Hub[In-memory realtime hub]
    Hub -- sync.required --> iOS
    API -- outbox dispatcher --> HF[Hangfire server]
    HF --> PG
    HF -- AI drafts / continuations --> LLM[neuroapi.host → Timeweb Cloud]
    HF -- push --> APNs[Apple APNs]
    API -- OTP --> Resend[Resend]
    APNs --> iOS
```

В одном контейнере работают: HTTP API, SSE, диспетчер outbox (`BackgroundService`) и Hangfire server. Все внешние зависимости скрыты за интерфейсами (`ILlmClient`, `IApnsClient`, `IEmailSender`, `IRealtimeHub`, `IBackgroundJobs`), поэтому worker и SSE-fanout можно позже вынести в отдельные процессы.

## Модули (`Nodus.Api/`)

| Папка | Ответственность |
|---|---|
| `Authentication/` | пользователь, JWT, refresh-токены, email OTP, профиль |
| `Nodes/` | узлы, ревизии, валидация шаблона, права (`NodeAccess`), DTO |
| `Recurrence/` | правило повторения, календарная арифметика, часовые пояса |
| `Occurrences/` | ключи и состояния экземпляров, проекция истории, лента |
| `Sync/` | журнал изменений, Delta Sync, идемпотентность, unit of work |
| `Devices/` | установки приложения и APNs-токены |
| `Notifications/` | запрос push и его доставка |
| `Connections/`, `Sharing/` | доверенные связи и совместные узлы |
| `AiDrafts/`, `AiContinuations/` | AI-функции |
| `DataPortability/` | экспорт и импорт, парсеры форматов по версиям |
| `Infrastructure/` | Persistence, Outbox, Hangfire, Llm, Sse, Apns, Resend, Observability |
| `NodusApplication.cs` | composition root: регистрация сервисов, middleware, маршруты |

## Жизненный цикл запроса

Порядок middleware (`NodusApplication.Build`):

1. `ForwardedHeaders` — реальный IP и схема за reverse proxy.
2. Observability — `X-Request-Id` (берётся у клиента или генерируется), scope логов с `RequestId` и `IdempotencyKey`.
3. Обработчик исключений — `400 bad_request` для битого тела, `500 internal_error` без внутренних деталей.
4. Буферизация тела мутаций (нужно, чтобы посчитать хеш запроса для идемпотентности).
5. Аутентификация JWT → авторизация → rate limiting.
6. Endpoint, на мутациях — фильтр идемпотентности.

Пользователь определяется **только** по проверенному токену (`ClaimsPrincipal.GetUserId()`); идентификаторы из тела запроса никогда не дают доступа.

## Транзакция мутации

Это центральный механизм надёжности. Любое изменение (создать узел, развязать экземпляр, принять приглашение…) проходит так:

```mermaid
sequenceDiagram
    participant C as Клиент
    participant F as IdempotencyFilter
    participant H as Обработчик
    participant DB as PostgreSQL
    C->>F: POST + Idempotency-Key
    F->>DB: есть ли client_operations(user, key)?
    alt уже выполнялось
        F-->>C: сохранённый ответ (Idempotent-Replayed: true)
    else новое
        F->>DB: BEGIN
        F->>H: выполнить
        H->>DB: изменить данные
        H->>DB: sync_changes (ревизии получателей)
        H->>DB: outbox_events
        F->>DB: client_operations (код + тело ответа)
        F->>DB: COMMIT
        F-->>C: ответ
        F->>F: разбудить outbox dispatcher
    end
```

Ключевые классы:

- `Sync/IdempotencyFilter.cs` — открывает транзакцию, сохраняет ответ, обрабатывает гонки.
- `Sync/UnitOfWork.cs` — `CommitAsync()`: сбросить журнал изменений и сохранить. Внутри транзакции фильтра коммит делает фильтр.
- `Sync/ChangeJournal.cs` — копит изменения сущностей и при сбросе выделяет ревизии каждому получателю.

Если обработчик вернул ошибку (4xx), всё, кроме записи о самом результате, откатывается.

## Transactional outbox

`outbox_events` — таблица событий, записанных **в той же транзакции**, что и данные. Отдельный `OutboxDispatcher` (`Infrastructure/Outbox/OutboxDispatcher.cs`) забирает их:

- просыпается сразу после коммита (сигнал) или раз в `Outbox:PollSeconds` (5 с);
- выбирает пачку `FOR UPDATE SKIP LOCKED` (безопасно и для будущего второго воркера);
- вызывает обработчик по типу события; при ошибке — повтор с экспоненциальной задержкой и jitter, после `Outbox:MaxAttempts` (10) событие помечается `failed_at`.

Доставка «как минимум один раз», поэтому все обработчики идемпотентны.

| Тип события | Обработчик | Что делает |
|---|---|---|
| `sync.changed` | `SyncChangedOutboxHandler` | шлёт SSE-сигнал получателям |
| `push.requested` | `PushRequestedOutboxHandler` | ставит задачу доставки push (очередь `notifications`) |
| `ai.draft.requested` | `AiDraftRequestedOutboxHandler` | ставит разбор текста (очередь `ai-drafts`) |
| `ai.continuation.requested` | `ContinuationRequestedOutboxHandler` | откладывает конвейер продолжения (очередь `ai-continuations`) |
| `import.requested` | `ImportRequestedOutboxHandler` | ставит AI-конвертацию файла (очередь `imports`) |

Зачем это нужно: нельзя «сначала закоммитить, потом поставить задачу в Hangfire» — между этими шагами процесс может упасть, и задание потеряется. С outbox задание либо записано вместе с данными, либо не записано вообще.

## Фоновые задачи (Hangfire)

Хранилище — тот же PostgreSQL (схема `hangfire`). Очереди: `ai-drafts`, `ai-continuations`, `imports`, `notifications`, `maintenance`, `default`.

| Задача | Очередь | Когда |
|---|---|---|
| `AiDraftRunner` | `ai-drafts` | после создания AI-задания |
| `ContinuationRunner` | `ai-continuations` | через 20 с после создания/правки одноразового узла |
| `ImportRunner` | `imports` | после загрузки чужого файла на импорт |
| `PushDeliveryJob` | `notifications` | по запросу push |
| `MaintenanceJob` | `maintenance` | ежедневно в 03:00 UTC (retention) |

Задачи ставятся только через `IBackgroundJobs` (`Infrastructure/Hangfire/BackgroundJobs.cs`). Когда Hangfire выключен (тесты), используется `InlineBackgroundJobs`, который выполняет задачу сразу.

## Хранение данных

PostgreSQL, EF Core, имена в `snake_case`. Все моменты времени хранятся в UTC (`timestamptz`); любой пришедший offset нормализуется конвертером в `NodusDbContext`. Локальные даты/время — отдельно (`date`, `time`) вместе с IANA-зоной.

Основные таблицы: `users`, `refresh_tokens`, `email_verification_codes`, `nodes`, `node_revisions`, `node_occurrence_states`, `sync_changes`, `client_operations`, `outbox_events`, `devices`, `connections`, `connection_invites`, `shared_node_members`, `ai_draft_jobs`, `ai_node_drafts`, `node_suggestions`, `continuation_cache`.

Ключевые ограничения целостности:

- `client_operations (user_id, idempotency_key)` — PK;
- `node_occurrence_states (node_id, occurrence_key)` — PK;
- `connections (user_low_id, user_high_id)` — уникально + check `user_low_id < user_high_id`;
- `shared_node_members (node_id, user_id)` — PK;
- `sync_changes (recipient_user_id, revision)` — PK;
- `users.email`, `devices.apns_token`, `refresh_tokens.token_hash` — уникальны.
