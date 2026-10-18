# Документация Nodus backend

Описание того, **как работает** backend: модули, потоки данных, правила и принятые решения. Код — в `Nodus.Api/`, ссылки на файлы даны в каждом разделе.

| Файл | О чём |
|---|---|
| [01-architecture.md](01-architecture.md) | Общая схема, модули, жизненный цикл запроса, транзакции, outbox, фоновые задачи |
| [02-authentication.md](02-authentication.md) | Анонимный аккаунт, JWT и refresh-токены, привязка email, вход на новом устройстве, профиль |
| [03-nodes-and-recurrence.md](03-nodes-and-recurrence.md) | Узлы, типы, правило повторения, экземпляры, история правок, DST, лента |
| [04-sync-and-idempotency.md](04-sync-and-idempotency.md) | Idempotency-Key, версии и конфликты, журнал изменений, Delta Sync, snapshot |
| [05-realtime-and-push.md](05-realtime-and-push.md) | SSE, устройства, APNs, когда и что отправляется |
| [06-connections-and-sharing.md](06-connections-and-sharing.md) | Доверенные связи, приглашения, совместные узлы, права |
| [07-ai-drafts.md](07-ai-drafts.md) | Голосовой разбор в черновики, LLM-клиент с fallback |
| [08-ai-continuations.md](08-ai-continuations.md) | «Продолжение нити»: конвейер, кеш, показ и принятие |
| [09-operations.md](09-operations.md) | Конфигурация, запуск, миграции, Docker, метрики, логи, retention, тесты |
| [10-export-import.md](10-export-import.md) | Экспорт активных узлов в JSON, импорт Nodus-файлов по версии и чужих файлов через AI |
| [11-plans-and-features.md](11-plans-and-features.md) | Планы (обычный / Nodus+), доступ к AI-функциям, согласие на AI, выдача плана |
| [12-app-config.md](12-app-config.md) | Конфигурация клиента в БД: ссылки, версии, переключатели; `GET /v1/app-config`, команда `config` |
| [13-environment.md](13-environment.md) | Все переменные окружения: назначение и значения по умолчанию |
| [adr/](adr/) | Записи архитектурных решений (ADR) |
| [ios/](ios/README.md) | **Руководство для iOS-разработки:** что делать на клиенте — авторизация, Outbox и ошибки, sync, лента и уведомления, push, шаринг, AI, импорт, справочник API |

## Быстрые ответы

- **Где источник истины?** На сервере. iOS работает офлайн и догоняет через `GET /v1/sync`.
- **Что гарантирует, что ничего не потеряется?** Каждая мутация в одной транзакции пишет данные, запись в журнал синхронизации, события outbox и сохранённый ответ. См. [01](01-architecture.md#транзакция-мутации).
- **Что если запрос повторился?** Тот же `Idempotency-Key` → тот же ответ байт в байт, без повторного действия. См. [04](04-sync-and-idempotency.md).
- **Почему прошлые экземпляры не меняются после правки?** Шаблон версионируется, каждый экземпляр отображается по той версии, при которой он стал прошлым. См. [03](03-nodes-and-recurrence.md#история-правок).
- **SSE/APNs надёжны?** Нет, и не должны: это только сигнал «сходи в sync». Данные всегда доступны через sync. См. [05](05-realtime-and-push.md).
- **Кому доступны AI-функции?** Пользователям с планом Nodus+ и согласием на AI; клиент видит список в `user.features`. См. [11](11-plans-and-features.md).
- **AI создаёт узлы сам?** Никогда. Только черновики и предложения, узлы создаются после подтверждения пользователем. См. [07](07-ai-drafts.md), [08](08-ai-continuations.md).

## Общие соглашения API

- Префикс `/v1`, JSON в `snake_case`, enum-значения строками (`one_time`, `untied`…).
- Даты — `YYYY-MM-DD`, локальное время — `HH:mm`, моменты — ISO 8601 в UTC.
- Ошибки — RFC 9457 problem details с машинным полем `code`:
  ```json
  { "type": "https://nodus.app/problems/version_conflict", "title": "...", "status": 409, "code": "version_conflict", "current": { } }
  ```
- Валидация — `400 validation_failed` с `errors: { "field": ["message"] }`.
- Все запросы, кроме `/v1/auth/*` (кроме email-привязки) и `/health/*`, требуют `Authorization: Bearer <access_token>`.
