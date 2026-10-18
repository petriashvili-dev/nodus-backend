# Nodus iOS: руководство по интеграции с backend

Документы для iOS-разработки: **что и как делать на клиенте**, чтобы работать с backend Nodus. Продуктовые требования — `nodus_docs/01_Nodus_Business_Description.md` и `03_Nodus_iOS_Requirements.md`; как устроен сервер внутри — [`docs/`](../README.md).

| Файл | Содержание |
|---|---|
| [01-auth-and-account.md](01-auth-and-account.md) | первый запуск, токены и Keychain, refresh, устройство, профиль, часовой пояс, восстановление, удаление аккаунта |
| [02-outbox-and-errors.md](02-outbox-and-errors.md) | модель операции, Idempotency-Key, порядок, `expected_version`, что делать с каждым кодом ошибки |
| [03-sync-engine.md](03-sync-engine.md) | когда синхронизироваться, Delta Sync, snapshot, применение изменений, SSE-клиент |
| [04-feed-occurrences-notifications.md](04-feed-occurrences-notifications.md) | узлы и экземпляры, лента, горизонт, локальные уведомления, смена дня и пояса, офлайн-созданные узлы |
| [05-push-and-deeplinks.md](05-push-and-deeplinks.md) | APNs, типы push, маршрутизация, дедупликация |
| [06-connections-and-sharing.md](06-connections-and-sharing.md) | приглашения, подтверждение связи, совместные узлы, права, конфликты |
| [07-ai.md](07-ai.md) | доступ к AI-функциям и согласие, голосовые AI-черновики, «Продолжение нити» |
| [08-export-import.md](08-export-import.md) | экспорт и импорт в настройках |
| [09-api-reference.md](09-api-reference.md) | все эндпоинты: запрос, ответ, ошибки |
| [10-app-config.md](10-app-config.md) | ссылки, минимальная версия, лимиты с сервера: `GET /v1/app-config` |

## Порядок внедрения

Соответствует §16 требований к iOS:

1. Локальное хранилище (`LocalStore`) и модели по контракту из [09](09-api-reference.md).
2. Durable Outbox + API-клиент + анонимная авторизация и Keychain ([01](01-auth-and-account.md), [02](02-outbox-and-errors.md)).
3. Delta Sync + SSE + конфликты ([03](03-sync-engine.md)).
4. Лента, экземпляры, история, локальные уведомления, смена пояса ([04](04-feed-occurrences-notifications.md)).
5. Связи, совместные узлы, APNs и deep links ([05](05-push-and-deeplinks.md), [06](06-connections-and-sharing.md)).
6. Голосовой ввод и AI-черновики ([07](07-ai.md)).
7. AI-продолжения ([07](07-ai.md#продолжение-нити)).
8. Экспорт/импорт, удаление аккаунта ([08](08-export-import.md), [01](01-auth-and-account.md#удаление-аккаунта)).

## Общие соглашения

| | |
|---|---|
| Base URL | `https://<host>/v1` |
| Формат | JSON, ключи `snake_case`, enum — строки (`one_time`, `untied`) |
| Дата | `"2026-10-18"` |
| Локальное время | `"09:30"` (24 ч, минуты) |
| Момент | ISO 8601 UTC, `"2026-10-18T06:30:00+00:00"` |
| Пояс | IANA (`TimeZone.current.identifier`), например `"Europe/Moscow"` |
| ID | UUID; id узлов генерирует **клиент** |
| Авторизация | `Authorization: Bearer <access_token>` на всём, кроме `/auth/anonymous`, `/auth/refresh`, `/auth/logout`, `/auth/email/sign-in/*`, `/app-config` |
| Мутации | заголовок `Idempotency-Key: <UUID>` (кроме `/auth/*`, `/devices`, `DELETE /me`, `DELETE /connections/invites/{id}`) |
| Корреляция | можно слать `X-Request-Id`; сервер всегда возвращает его в ответе — логировать на клиенте для поддержки |

Ошибка всегда в формате problem details, решения принимаются **по полю `code`**, не по тексту:

```json
{ "type": "https://nodus.app/problems/version_conflict", "title": "…", "status": 409, "code": "version_conflict" }
```

Валидация: `400 validation_failed` с `errors: { "field": ["message"] }` — имена полей в snake_case (`nodes[1].title`).

## Ключевые правила (кратко)

- Сервер — источник истины. Клиент показывает локальное состояние и отправляет намерения через Outbox.
- Каждая мутация: локальное изменение + запись Outbox в **одной транзакции** локальной БД.
- Повторять операцию — только **с тем же** `Idempotency-Key`; payload операции после постановки в очередь не менять.
- SSE и push — только сигнал «сходи в sync». Данные — только из sync и ответов API.
- Экземпляры регулярных узлов считает сервер; клиент их не вычисляет (кроме [ближайшего у офлайн-созданного узла](04-feed-occurrences-notifications.md#узел-создан-без-сети)).
- AI ничего не создаёт без подтверждения пользователя.
