# Справочник API

Все пути — с префиксом `/v1`. **IK** — нужен `Idempotency-Key`. **Auth** — нужен access-токен. Общие ошибки любого запроса: `400 validation_failed`, `401 unauthorized`, `429 rate_limited`, `500 internal_error`; для IK-запросов также `400 idempotency_key_required`, `422 idempotency_key_reused`, `409 conflict`. Как реагировать — [02](02-outbox-and-errors.md#ответы-и-что-с-ними-делать).

## Авторизация и профиль

| Метод и путь | Auth | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|---|
| `POST /auth/anonymous` | — | — | — | `201` `AuthTokens` | |
| `POST /auth/refresh` | — | — | `{ refresh_token }` | `200` `AuthTokens` | `401 invalid_refresh_token` |
| `POST /auth/logout` | — | — | `{ refresh_token }` | `204` | |
| `POST /auth/email/start` | ✓ | — | `{ email }` | `202 { message }` | |
| `POST /auth/email/verify` | ✓ | — | `{ email, code }` | `200` `UserProfile` | `400 invalid_code`, `409 email_already_linked` |
| `POST /auth/email/sign-in/start` | — | — | `{ email }` | `202 { message }` | |
| `POST /auth/email/sign-in/verify` | — | — | `{ email, code }` | `200` `AuthTokens` | `400 invalid_code` |
| `GET /me` | ✓ | — | — | `200` `UserProfile` | |
| `PATCH /me` | ✓ | ✓ | `{ display_name }` | `200` `UserProfile` | |
| `PUT /me/timezone` | ✓ | ✓ | `{ timezone }` | `200 { timezone, adapted_nodes, skipped_shared_nodes, skipped_past_nodes }` | |
| `DELETE /me` | ✓ | — | — | `204` | |

```text
AuthTokens  { user_id, access_token, access_token_expires_at, refresh_token, refresh_token_expires_at, token_type }
UserProfile { id, is_anonymous, email, email_verified_at, display_name, timezone, version, created_at }
```

## Устройства

| Метод и путь | Auth | IK | Тело | Успех |
|---|---|---|---|---|
| `POST /devices` | ✓ | — | `{ installation_id, apns_token?, apns_environment?, app_version? }` | `200` `DeviceDto` |
| `DELETE /devices/{installation_id}` | ✓ | — | — | `204` |

```text
DeviceDto { id, installation_id, platform, has_push_token, apns_environment (unknown|sandbox|production), app_version, last_seen_at }
```

## Узлы

| Метод и путь | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|
| `POST /nodes` | ✓ | `CreateNode` | `201` `NodeDto` | `409 node_already_exists` |
| `POST /nodes/batch` | ✓ | `{ nodes: [CreateNode] }` (≤ 50) | `201 { nodes: [NodeDto] }` | `409 node_already_exists` |
| `GET /nodes/{id}` | — | — | `200` `NodeDto` | `404 not_found` |
| `PATCH /nodes/{id}` | ✓ | `UpdateNode` | `200` `NodeDto` | `404`, `409 version_conflict` (+`current`) |
| `DELETE /nodes/{id}?expected_version=` | ✓ | — | `204` | `403 forbidden`, `404`, `409 version_conflict` |
| `PUT /nodes/{id}/occurrences/{key}/untie` | ✓ | `{ completed_at? }` | `200` `OccurrenceStateDto` | `404 not_found`, `404 occurrence_not_found` |
| `PUT /nodes/{id}/occurrences/{key}/retie` | ✓ | — | `200` `OccurrenceStateDto` | `404`, `404 occurrence_not_found` |
| `GET /nodes/{id}/occurrences?from&to` | — | — | `200 { node_id, from, to, occurrences: [OccurrenceDto] }` | `404`; диапазон ≤ 400 дней |

```text
CreateNode { id (UUID клиента), type (one_time|recurring|anniversary), title (≤200), description? (≤4000), timezone (IANA),
             date?, time?, recurrence?, track_years? }
UpdateNode { expected_version, title, description?, timezone, date?, time?, recurrence?, track_years? }   — полный шаблон

  one_time:    date? (без даты — «когда-нибудь»), time? (требует date), recurrence = null
  recurring:   recurrence обязателен, date = time = null
  anniversary: date обязателен (исходная дата), time?, track_years (по умолчанию true)

Recurrence { frequency (daily|weekly|monthly|yearly), interval (1–366), start_date, until?, times: ["HH:mm"] (≤12),
             week_days: ["monday"…] (только weekly), month_day? (1–31, только monthly) }

NodeDto { id, type, owner_id, title, description, timezone, date, time, scheduled_at, recurrence, track_years,
          revision, version, created_at, updated_at, members: [{ user_id, display_name }],
          horizon: { from, to }, occurrences: [OccurrenceDto] }

OccurrenceDto { node_id, key, revision, title, description, date, time, scheduled_at, years, state (tied|untied),
                completed_at, is_past }

OccurrenceStateDto { node_id, key, state, completed_at, version, updated_at, updated_by,
                     revision, title, date, time, scheduled_at }
```

## Лента и экземпляры

| Метод и путь | Успех | Ограничения |
|---|---|---|
| `GET /feed?from&to` | `200 { generated_at, from, to, items: [{ node_type, node_version, occurrence: OccurrenceDto }] }` | ≤ 400 дней; по умолчанию вчера … +60 |
| `GET /occurrences?from&to` | `200 { generated_at, from, to, occurrences: [OccurrenceDto] }` | ≤ 120 дней; по умолчанию вчера … +45 |

## Синхронизация

| Метод и путь | Успех | Ошибки |
|---|---|---|
| `GET /sync?cursor&limit` | `200 { changes: [SyncChange], next_cursor, has_more }` | `410 cursor_expired` |
| `GET /sync/snapshot` | `200 { cursor, user, nodes, connections, ai_draft_jobs, suggestions, occurrence_states }` | |
| `GET /events` | `text/event-stream`, события `sync.required` | |

```text
SyncChange { revision, entity_type (user|node|occurrence_state|connection|ai_draft_job|suggestion|import_job),
             entity_id, operation (upsert|delete), version, data }
```

## Связи и совместные узлы

| Метод и путь | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|
| `GET /connections` | — | — | `200 { connections: [ConnectionDto] }` | |
| `POST /connections/invites` | ✓ | — | `201 { id, token, code, link, expires_at }` | `409 too_many_invites` |
| `DELETE /connections/invites/{id}` | — | — | `204` | |
| `POST /connections/invites/{token}/accept` | ✓ | — | `200` `ConnectionDto` | `404 invite_invalid`, `400 own_invite`, `409 already_connected` |
| `POST /connections/invites/accept` | ✓ | `{ code }` | `200` `ConnectionDto` | те же |
| `POST /connections/{id}/confirm` | ✓ | — | `200` `ConnectionDto` | `403 forbidden`, `404` |
| `DELETE /connections/{id}` | ✓ | — | `204` | `404` |
| `POST /nodes/{id}/members` | ✓ | `{ user_id }` | `200` `NodeDto` | `403 forbidden`, `404`, `409 not_connected` |
| `DELETE /nodes/{id}/members/{userId}` | ✓ | — | `204` | `403 forbidden`, `404` |

```text
ConnectionDto { id, user_id (другой человек), display_name, status (pending|active|removed), invited_by_me,
                awaiting_my_confirmation, version, created_at, confirmed_at }
```

## AI-черновики

| Метод и путь | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|
| `POST /ai/node-drafts` | ✓ | `{ text, timezone, reference_time }` | `202` `AiDraftJobDto` | `429 ai_quota_exceeded` |
| `GET /ai/node-drafts` | — | — | `200 { jobs: [AiDraftJobDto] }` (queued/processing/ready) | |
| `GET /ai/node-drafts/{id}` | — | — | `200` `AiDraftJobDto` | `404` |
| `POST /ai/node-drafts/{id}/confirm` | ✓ | `{ nodes?: [CreateNode] }` | `200 { job, created: { nodes } \| null }` | `409 job_not_ready`, `400`/`409` создания узлов |
| `POST /ai/node-drafts/{id}/cancel` | ✓ | — | `200` `AiDraftJobDto` | `404` |

```text
AiDraftJobDto { id, status (queued|processing|ready|confirmed|failed|cancelled|expired), timezone, reference_time,
                error, version, created_at, ready_at, confirmed_at, expires_at, drafts: [NodeDraftDto] }
NodeDraftDto  { index, type, title, description, timezone, date, time, recurrence, track_years,
                issues: [{ code, field, message }] }
```

## Продолжения

| Метод и путь | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|
| `POST /suggestions/{id}/shown` | ✓ | — | `200` `SuggestionDto` | `404` |
| `POST /suggestions/{id}/accept` | ✓ | `{ node: CreateNode }` | `200 { suggestion, node: NodeDto }` | `409 already_accepted` (+`linked_node_id`), `409 suggestion_unavailable`, ошибки создания узла |
| `POST /suggestions/{id}/dismiss` | ✓ | — | `200` `SuggestionDto` | `404` |

```text
SuggestionDto { id, source_node_id, status (ready|accepted|dismissed), title, description,
                shown_at, accepted_at, dismissed_at, linked_node_id, version }
```

## Экспорт и импорт

| Метод и путь | IK | Тело | Успех | Ошибки |
|---|---|---|---|---|
| `GET /me/export` | — | — | `200` файл `nodus` v1 | |
| `POST /me/import` | ✓ | `{ content, file_name?, timezone? }` | `200 { mode: "direct", format, result }` или `202 { mode: "ai", job: ImportJobDto }` | `400 unsupported_format_version`, `400 file_too_large_for_ai`, `429 ai_quota_exceeded` |
| `GET /me/imports/{id}` | — | — | `200` `ImportJobDto` | `404` |
| `POST /me/imports/{id}/confirm` | ✓ | `{ indexes?: [int] }` | `200` `ImportJobDto` (`completed`) | `409 import_not_ready` |
| `POST /me/imports/{id}/cancel` | ✓ | — | `200` `ImportJobDto` | `404` |

```text
ImportResult { created_nodes, skipped_duplicates, skipped_inactive, issues: [{ index, title, code, message }] }
ImportJobDto { id, status (queued|processing|ready|completed|failed|cancelled|expired), file_name, error, version,
               created_at, completed_at, expires_at,
               preview: [{ index, type, title, description, timezone, date, time, recurrence, track_years }],
               preview_issues: [...], result: ImportResult? }
```

## Push `nodus.kind`

`shared_node_changed` (`node_id`), `connection_changed` (`connection_id`), `ai_drafts_ready` (`job_id`), `import_ready` (`import_id`); всегда есть `event_id`. Подробнее — [05](05-push-and-deeplinks.md).
