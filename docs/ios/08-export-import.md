# Экспорт и импорт

Серверная сторона и спецификация формата — [../10-export-import.md](../10-export-import.md).

Оба действия — в настройках, только онлайн, не через Outbox.

## Экспорт

```http
GET /v1/me/export
→ 200 application/json, Content-Disposition: attachment; filename="nodus-export-2026-10-18.json"
```

Сохранить ответ во временный файл с именем из заголовка и открыть Share Sheet («Сохранить в Файлы», AirDrop…). В файле только свои активные узлы — пользователю можно так и написать: «Экспортируются текущие дела, привычки и даты; выполненные и удалённые — нет».

## Импорт

1. Выбор файла — `UIDocumentPickerViewController` (JSON, CSV, текст).
2. Прочитать как UTF-8 текст и отправить:
   ```http
   POST /v1/me/import       (Idempotency-Key)
   { "content": "<текст файла>", "file_name": "todoist.csv", "timezone": "<текущий пояс устройства>" }
   ```
   Лимит — 200 000 символов (тело запроса до 1 МБ).

### Файл Nodus — сразу

```json
200 { "mode": "direct", "format": "nodus/v1",
      "result": { "created_nodes": 12, "skipped_duplicates": 3, "skipped_inactive": 2,
                  "issues": [ { "index": 7, "title": null, "code": "missing_title", "message": "…" } ] } }
```

Показать итог («Добавлено 12, уже было 3, неактуальных 2, с ошибками 1»), запустить Delta Sync — новые узлы придут через него.

`400 unsupported_format_version` — файл из более новой версии Nodus: «Обновите приложение, чтобы импортировать этот файл».

### Файл другого приложения — через AI

Только при `ai_import` в `user.features` ([07 → Доступ](07-ai.md#доступ-к-ai-функциям-и-согласие)). Без функции экран и выбор файла говорят только о файлах Nodus; если пользователь всё же выбрал чужой файл, сервер ответит `403 feature_not_available` — показать «Можно импортировать только файл, экспортированный из Nodus». Без согласия — `403 ai_consent_required` → экран согласия.

```json
202 { "mode": "ai", "job": { "id": "…", "status": "queued", … } }
```

1. Экран «Разбираем файл…»; можно закрыть. Готовность — через sync (`entity_type: import_job`), SSE и push `import_ready` (`import_id`).
2. Экран проверки — `GET /v1/me/imports/{id}`:
   ```json
   { "id": "…", "status": "ready", "file_name": "todoist.csv",
     "preview": [ { "index": 0, "type": "one_time", "title": "Сдать отчёт", "date": "2026-10-25", "time": "10:00", … } ],
     "preview_issues": [], "expires_at": "…" }
   ```
   Список с галочками (все включены).
3. «Импортировать» → `POST /v1/me/imports/{id}/confirm { "indexes": [0, 2, 5] }` (без `indexes` — все) → `status: completed` + `result` как у прямого импорта.
4. «Отмена» → `POST /v1/me/imports/{id}/cancel`.

**Узлы до подтверждения не создаются.**

| Ошибка | UI |
|---|---|
| `400 file_too_large_for_ai` | файл больше 30 000 символов — «Слишком большой файл; разделите его или экспортируйте из Nodus» |
| `429 ai_quota_exceeded` | 5 AI-импортов в сутки исчерпаны |
| `status: failed` (`ai_unavailable`, `ai_temporarily_unavailable`, `invalid_ai_response`, `nothing_recognized`, `timed_out`) | «Не получилось разобрать файл» + повторить позже |
| `status: expired` | предпросмотр не подтверждён за 7 дней — загрузить файл снова |

## Что важно сказать пользователю

- Импорт **только добавляет**: существующие узлы не меняются; совпадающие и неактуальные (прошедшие, завершённые) пропускаются. Повторный импорт того же файла ничего не задвоит.
- Узлы без часового пояса создаются в текущем поясе устройства.
