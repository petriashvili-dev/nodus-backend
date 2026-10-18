# Nodus backend

.NET 10 · ASP.NET Core Minimal API · PostgreSQL · Hangfire · SSE · APNs · один экземпляр API.

Требования: `nodus_docs/02_Nodus_Backend_Requirements.md`. Ниже — то, как они реализованы, и принятые решения.

Подробное описание функционала по модулям — в [`docs/`](docs/README.md).

## Запуск локально

```bash
# PostgreSQL для разработки и тестов
docker run -d --name nodus-test-pg -p 55432:5432 -e POSTGRES_USER=nodus -e POSTGRES_PASSWORD=nodus -e POSTGRES_DB=nodus postgres:16

export ConnectionStrings__Postgres="Host=localhost;Port=55432;Database=nodus;Username=nodus;Password=nodus"
export Jwt__SigningKey="$(openssl rand -base64 48)"
export EmailOtp__HashPepper="$(openssl rand -base64 24)"

dotnet run --project Nodus.Api -- migrate   # применить миграции и выйти
dotnet run --project Nodus.Api              # http://localhost:5258
dotnet test                                 # unit + интеграционные (нужен PostgreSQL выше или NODUS_TEST_POSTGRES)
dotnet run -c Release --project Nodus.LoadTests -- http://localhost:5258 50 60
```

Без ключа Resend письма не отправляются, а пишутся в лог; без ключа APNs push пропускаются; без LLM-провайдеров AI-задания завершаются `failed / ai_unavailable`.

## Конфигурация и секреты

Всё задаётся через environment (`Section__Key`). Секреты — только через environment/secrets, в репозитории их нет.

| Ключ | Назначение |
|---|---|
| `ConnectionStrings__Postgres` | PostgreSQL (данные + Hangfire в схеме `hangfire`) |
| `Jwt__SigningKey` | **секрет**, HMAC-ключ access JWT, ≥ 32 байт |
| `EmailOtp__HashPepper` | **секрет**, pepper для хешей OTP, кодов приглашений и ключей AI-кеша |
| `Resend__ApiKey`, `Resend__From` | отправка кодов подтверждения email |
| `Apns__KeyId`, `Apns__TeamId`, `Apns__BundleId`, `Apns__PrivateKey` | APNs по ключу `.p8` (содержимое файла; PEM, base64, `\n` в .env допустимы) |
| `Llm__Providers__0__*` | primary neuroapi.host: `ApiKey`, `Model`, `SupportsJsonSchema`, цены за 1M токенов |
| `Llm__Providers__1__*` | secondary Timeweb Cloud (`https://api.timeweb.ai`) |
| `ReverseProxy__TrustForwardedHeaders` | `true` за reverse proxy (IP для rate limit, схема) |
| `Observability__PrometheusEnabled` / `__OtlpEndpoint` | `/metrics` и/или экспорт OTLP |
| `Hangfire__Enabled`, `Outbox__RunInBackground` | выключаются только в тестах |

Остальные лимиты и сроки — в `appsettings.json` (`Occurrences`, `Retention`, `Ai`, `AiContinuations`, `RateLimits`, `Connections`, `Sse`).

## Эксплуатация

- **Docker:** `docker-compose.yml` — развёртывание по схеме eatee-backend (Dokploy + Traefik, образ из GHCR, `.env` по шаблону `.env.example`): `nodus-db` → одноразовый `nodus-migrate` → `nodus-api`. `/metrics` — только на внутреннем порту 9464. Локально — `docker compose -f docker-compose.local.yml up -d --build`. Схема — [docs/09-operations.md](docs/09-operations.md#схема-развёртывания).
- **Миграции** под контролем релиза: шаг `docker run nodus-api migrate` (или сервис `migrate` в compose) перед запуском новой версии. API сам миграции не применяет (`Database__MigrateOnStartup=false`).
- **Health:** `/health/live` (процесс жив), `/health/ready` (PostgreSQL доступен), `/version` (задеплоенный коммит).
- **CI/CD:** `.github/workflows/deploy.yml` — при мердже в `main`: тесты на PostgreSQL, и только после них — образ в GHCR и деплой через Dokploy. См. [docs/09-operations.md](docs/09-operations.md#cicd-github-actions).
- **Graceful shutdown:** 30 с на завершение SSE и воркеров Hangfire; SSE-клиенты переподключаются и догоняют через sync.
- **Резервные копии:**
  ```bash
  docker compose exec -T postgres pg_dump -U nodus -Fc nodus > nodus-$(date +%F).dump
  # проверка восстановления — регулярно, в отдельную БД:
  docker compose exec -T postgres createdb -U nodus nodus_restore_check
  docker compose exec -T postgres pg_restore -U nodus -d nodus_restore_check < nodus-YYYY-MM-DD.dump
  docker compose exec -T postgres psql -U nodus -d nodus_restore_check -c "select count(*) from users; select max(revision) from sync_changes;"
  ```
- **Логи** — JSON в stdout, с `RequestId` (заголовок `X-Request-Id`) и `IdempotencyKey`. Содержимое узлов, промпты и диктовка не логируются (`Llm__LogContent=false`).
- **Метрики** (`Nodus.Api`): `nodus.llm.requests|latency|tokens|cost`, `nodus.outbox.events|lag`, `nodus.idempotency.replays`, `nodus.sync.requests`, `nodus.push.deliveries`, `nodus.continuations` + ASP.NET Core, HttpClient, runtime.
- **Нет HA:** отказ контейнера/VPS останавливает sync, SSE и задания; iOS продолжает работать локально, outbox на сервере и на клиенте догоняет после восстановления.

## Архитектура

```
Nodus.Api
  Authentication/   анонимный аккаунт, JWT + ротация refresh, email OTP (Resend), профиль
  Nodes/            узлы, ревизии шаблонов, права доступа (NodeAccess)
  Recurrence/       правило повторения, календарная арифметика, IANA/DST
  Occurrences/      ключи и состояния экземпляров, проекция истории, лента
  Connections/      доверенные связи и приглашения
  Sharing/          участники совместных узлов
  Sync/             журнал изменений, Delta Sync, идемпотентность, unit of work
  Devices/          установки и APNs-токены
  Notifications/    push-запросы и доставка
  AiDrafts/         голосовой разбор в черновики
  AiContinuations/  «Продолжение нити»
  Infrastructure/   Persistence, Outbox, Hangfire, Llm, Sse, Apns, Resend, Observability
```

Каждая мутация: данные + записи `sync_changes` + события `outbox_events` + сохранённый ответ (`client_operations`) — в одной транзакции. Dispatcher outbox рассылает SSE-сигналы и ставит задания Hangfire (очереди `ai-drafts`, `ai-continuations`, `notifications`, `maintenance`). Провайдеры LLM, APNs и Resend скрыты за интерфейсами; worker и SSE-fanout можно вынести, заменив `IBackgroundJobs`/`IRealtimeHub`.

## Принятые решения (открытые вопросы §13)

| Вопрос | Решение |
|---|---|
| Формат правила | Собственная строгая схема: `daily/weekly/monthly/yearly`, `interval`, `start_date`, `until`, `times[]` (несколько в день), `week_days`, `month_day`. 31-е → последний день месяца, 29.02 → 28.02. |
| История экземпляров | Неизменяемые `node_revisions` + детерминированное воспроизведение: экземпляр принадлежит ревизии, при которой он стал прошлым (timed — момент, all-day — конец дня). Новая ревизия не создаёт экземпляров в датах до своего вступления. Состояния хранятся явно со snapshot. Материализация и ежедневный job не нужны. |
| Ключ экземпляра | `g{generation}-{yyyyMMdd}-{slot}`; generation меняется при смене частоты; `once` для одноразовых. |
| DST | Несуществующее время сдвигается вперёд на длину разрыва, двойное — более раннее. |
| Правка «сегодня» | Применяется к ещё не прошедшим сегодняшним экземплярам; прошедшие остаются как были. |
| Удаление регулярного узла | Soft delete + tombstone; ревизии и состояния сохраняются. |
| Конфликты | Optimistic `expected_version` → `409` с каноническим состоянием (`current`); untie/retie — идемпотентны, побеждает последняя операция. Молчаливого LWW для правок нет. |
| Участники | Управляет и удаляет узел только владелец; участник может выйти; разрыв связи снимает совместный доступ между двумя пользователями. |
| Связь | Приглашение (ссылка или 8-символьный код, 24 ч, одноразовое) → принятие → подтверждение пригласившим. Встречные приглашения сразу дают активную связь. |
| Прошедший невыполненный экземпляр | Сервер отдаёт `is_past` + `state`; показывать ли просрочку — решает клиент. |
| Сроки хранения | idempotency 30 дн., sync-журнал 90 дн. (старше → `410` и snapshot), outbox 7 дн., AI-текст 24 ч, черновики 14 дн., кеш продолжений 180 дн. без использования. |
| Смена часового пояса | `PUT /v1/me/timezone`: свои узлы переходят в новый пояс с тем же местным временем (серии — новой ревизией), прошедшие одноразовые и совместные узлы не трогаются. |
| Доступ к AI-функциям | План `Standard`/`Plus` в `users` + функции плана в конфигурации `Plans`; клиенту — только `features`. Nodus+ выдаётся командой `plan`, без HTTP. AI требует серверного согласия (`PUT /me/ai-consent`). [ADR 0001](docs/adr/0001-user-plans-and-features.md). |
| Вход ревьюера Apple | Для адресов `EmailOtp:ReviewEmails` код = секрет `EmailOtp:ReviewCode`, письмо не отправляется, лимиты те же; включается только на время ревью. |
| Слияние аккаунтов | Не выполняется. Email, занятый другим аккаунтом, → `409 email_already_linked`; вход по email на новом устройстве — `/v1/auth/email/sign-in/*`. |

## API (`/v1`, JSON snake_case, ошибки — problem details с полем `code`)

Мутации требуют заголовок `Idempotency-Key` (кроме auth и `/devices`). Повтор с тем же ключом возвращает тот же ответ (`Idempotent-Replayed: true`), другой payload с тем же ключом — `422`.

| | |
|---|---|
| Auth | `POST /auth/anonymous`, `/auth/refresh`, `/auth/logout`, `/auth/email/start`, `/auth/email/verify`, `/auth/email/sign-in/start`, `/auth/email/sign-in/verify`; `GET/PATCH/DELETE /me`, `PUT /me/timezone`, `PUT /me/ai-consent` |
| Nodes | `POST /nodes`, `POST /nodes/batch`, `GET/PATCH/DELETE /nodes/{id}`, `PUT /nodes/{id}/occurrences/{key}/untie|retie`, `GET /nodes/{id}/occurrences?from&to` |
| Лента | `GET /feed?from&to`, `GET /occurrences?from&to` (горизонт локальных уведомлений) |
| Sync | `GET /sync?cursor&limit`, `GET /sync/snapshot`, `GET /events` (SSE `sync.required`, `Last-Event-ID`) |
| Devices | `POST /devices`, `DELETE /devices/{installationId}` |
| Связи | `GET /connections`, `POST /connections/invites`, `DELETE /connections/invites/{id}`, `POST /connections/invites/{token}/accept`, `POST /connections/invites/accept` (код), `POST /connections/{id}/confirm`, `DELETE /connections/{id}` |
| Sharing | `POST /nodes/{id}/members`, `DELETE /nodes/{id}/members/{userId}` |
| AI drafts | `POST /ai/node-drafts`, `GET /ai/node-drafts`, `GET /ai/node-drafts/{jobId}`, `POST /ai/node-drafts/{jobId}/confirm` (с `nodes` — атомарно), `POST /ai/node-drafts/{jobId}/cancel` |
| Продолжения | `POST /suggestions/{id}/shown`, `/accept` (создаёт узел атомарно), `/dismiss` |
| Экспорт / импорт | `GET /me/export`, `POST /me/import`, `GET /me/imports/{id}`, `POST /me/imports/{id}/confirm`, `POST /me/imports/{id}/cancel` |
