# Realtime: SSE, устройства и push

Код: `Infrastructure/Sse/`, `Devices/`, `Infrastructure/Apns/`, `Notifications/`.

Главное правило: **SSE и APNs — только сигналы «есть новое», а не данные.** Они могут теряться; всё, о чём они сообщают, клиент получает через [Delta Sync](04-sync-and-idempotency.md).

## SSE

```http
GET /v1/events
Authorization: Bearer …
Last-Event-ID: 42          (или ?last_event_id=42)
```

Поток `text/event-stream`:

```
retry: 3000

id: 45
event: sync.required
data: {"revision":45,"event_id":"45"}

: ping
```

- `id` события = ревизия синхронизации пользователя.
- При подключении сервер сравнивает `Last-Event-ID` с текущей ревизией и, если клиент отстал, сразу шлёт `sync.required`. Без `Last-Event-ID` сигнал приходит всегда.
- Далее сигнал приходит после каждого коммита, затрагивающего пользователя (через outbox → in-memory hub). Сигналы не новее уже отправленного отбрасываются.
- `: ping` каждые `Sse:HeartbeatSeconds` (20 с), чтобы прокси не рвали соединение.
- При остановке сервера поток закрывается; клиент переподключается с backoff и **всегда** делает Delta Sync после переподключения.

Fanout — в памяти процесса (`InMemoryRealtimeHub`), этого достаточно для одного экземпляра API. Для нескольких экземпляров замените `IRealtimeHub` на PostgreSQL LISTEN/NOTIFY или брокер.

## Регистрация устройства

```http
POST /v1/devices
{ "installation_id": "…из Keychain…", "apns_token": "ab12…", "apns_environment": "sandbox", "app_version": "1.2" }
```

- Upsert по паре (пользователь, `installation_id`); `Idempotency-Key` не нужен.
- `apns_environment`: `sandbox` (сборки из Xcode), `production` (TestFlight/App Store), можно не указывать.
- Токен принадлежит одной установке: если на том же телефоне вошли в другой аккаунт, токен переходит к новому аккаунту, и старый перестаёт получать push.
- `DELETE /v1/devices/{installationId}` — при выходе из аккаунта.

## APNs

Авторизация по ключу `.p8` (`ApnsTokenProvider`):

1. Сервер подписывает JWT провайдера: ES256, заголовок `kid` = Key ID, payload `iss` = Team ID, `iat`.
2. Токен держится в памяти **50 минут** (Apple принимает до часа и запрещает обновлять чаще раза в 20 минут).
3. На ответ `ExpiredProviderToken` — один раз сбросить и подписать заново.

Отправка (`ApnsClient`): HTTP/2 на `api.push.apple.com` или `api.sandbox.push.apple.com` по окружению устройства. Если окружение неизвестно, пробуется предпочтительный шлюз, а при `BadDeviceToken` — второй; найденное окружение запоминается.

| Ответ Apple | Что делаем |
|---|---|
| 200 | доставлено |
| 410 / `Unregistered` / `BadDeviceToken` / `DeviceTokenNotForTopic` | токен удаляется у устройства (`token_invalidated_at`) |
| 429 / 500 / 503 | повтор с backoff (до 3 попыток) |
| `TooManyProviderTokenUpdates` и прочее | без повторов |

Если APNs не настроен (нет ключа), push молча пропускаются.

## Когда отправляется push

Обычные напоминания по расписанию **не** идут через APNs — iOS планирует их локально по горизонту экземпляров. Push только для внешних событий:

| Событие | `kind` | Заголовок / текст | `data` |
|---|---|---|---|
| С вами поделились узлом | `shared_node_changed` | «Новый совместный узел» / «С вами поделились узлом в Nodus.» | `node_id` |
| Ваше приглашение приняли / связь подтверждена | `connection_changed` | «Приглашение принято» / «Связь установлена» / «Связь подтверждена» | `connection_id` |
| AI-черновики готовы | `ai_drafts_ready` | «Черновики готовы» / «Проверьте и подтвердите новые узлы.» | `job_id` |
| AI-разбор не удался | `ai_drafts_ready` | «Не получилось разобрать запись» | `job_id` |

Тексты нейтральные — без названий узлов и без продиктованной речи (их видно на заблокированном экране). Payload:

```json
{ "aps": { "alert": { "title": "…", "body": "…" }, "sound": "default", "thread-id": "ai_drafts_ready" },
  "nodus": { "kind": "ai_drafts_ready", "event_id": "ai-drafts-…", "job_id": "…" } }
```

## Конвейер доставки

```mermaid
sequenceDiagram
    participant M as Мутация
    participant O as outbox_events
    participant D as OutboxDispatcher
    participant H as Hangfire (notifications)
    participant A as APNs
    M->>O: push.requested (в той же транзакции)
    D->>O: забрать после коммита
    D->>H: PushDeliveryJob
    H->>A: на каждое устройство пользователя
    A-->>H: результат; мёртвые токены удаляются
```

Push отправляется только для закоммиченных данных. Задача повторяется Hangfire, только если ни одно устройство не получило push и ошибка была временной — так повтор почти не создаёт дублей.

## Дедупликация на клиенте

У каждого push есть `event_id`. Клиент сравнивает его с уже показанными in-app/SSE-уведомлениями, чтобы не показывать одно и то же дважды. При открытом приложении предпочтительнее SSE и внутреннее уведомление; полностью подавить системный баннер в гонке foreground/background гарантировать нельзя.
