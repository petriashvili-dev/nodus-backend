# Связи и совместные узлы

Серверная сторона — [../06-connections-and-sharing.md](../06-connections-and-sharing.md).

## Экраны

1. **Связи** — список `GET /v1/connections` (также приходят в sync как `connection`).
2. **Пригласить** — ссылка и код.
3. **Принять приглашение** — по ссылке (deep link) или вводом кода.
4. **Подтверждение** — карточка «Аня приняла ваше приглашение — подтвердить?».
5. **Участники узла** — в карточке узла (у владельца — управление).
6. **Имя** — в настройках профиля (`PATCH /v1/me { display_name }`), его видят связи. Предложить задать имя перед первым приглашением.

## Пригласить

```http
POST /v1/connections/invites       (Idempotency-Key)
→ 201 { "id": "…", "token": "…", "code": "K7M2Q9TP", "link": "https://nodus.app/invite/…", "expires_at": "…" }
```

- Показать ссылку (Share Sheet) и код крупно (группами `K7M2 Q9TP`).
- Действует 24 часа, одноразовое; не больше 10 активных (`409 too_many_invites`).
- Отозвать: `DELETE /v1/connections/invites/{id}`.

## Принять

- Ссылка `https://nodus.app/invite/{token}` (Universal Link) → `POST /v1/connections/invites/{token}/accept`.
- Код (регистр, пробелы и дефисы не важны; `O`→`0`, `I`/`L`→`1`) → `POST /v1/connections/invites/accept { "code": "…" }`.
- Обе — с `Idempotency-Key`, только онлайн.

| Ответ | Показать |
|---|---|
| `200`, `status: pending` | «Ждём подтверждения от Ани» |
| `200`, `status: active` | «Связь установлена» (так бывает при встречных приглашениях) |
| `404 invite_invalid` | «Приглашение недействительно или истекло» |
| `400 own_invite` | «Это ваше приглашение» |
| `409 already_connected` | «Вы уже связаны» |

## Подтвердить / отклонить

`ConnectionDto`:

```json
{ "id": "…", "user_id": "<другой человек>", "display_name": "Аня", "status": "pending",
  "invited_by_me": true, "awaiting_my_confirmation": true, "version": 1, "created_at": "…", "confirmed_at": null }
```

- `awaiting_my_confirmation` → кнопки «Подтвердить» (`POST /v1/connections/{id}/confirm`) и «Отклонить» (`DELETE /v1/connections/{id}`).
- Разорвать активную связь — `DELETE /v1/connections/{id}` с предупреждением: совместные узлы между вами перестанут быть общими.

## Поделиться узлом

Только владелец, только с `active`-связями:

```http
POST   /v1/nodes/{nodeId}/members              { "user_id": "…" }     (Idempotency-Key)
DELETE /v1/nodes/{nodeId}/members/{userId}                              (Idempotency-Key)
```

| Действие | Владелец | Участник |
|---|---|---|
| правка, развязать/перевязать | ✓ | ✓ |
| добавить участника | ✓ | кнопку не показывать (`403`) |
| удалить участника | ✓ любого | только себя — «Выйти из узла» |
| удалить узел | ✓ | кнопку не показывать (`403`) |

- Владелец/участник определяется по `owner_id` в `NodeDto`; список — `members`.
- `409 not_connected` — связь не активна.
- Новый общий узел у получателя появляется через sync (+ push `shared_node_changed`) — мягкая анимация.
- Потеря доступа (вас удалили, связь разорвана, владелец удалил аккаунт) — `delete` в sync: убрать узел и его уведомления.

## Конфликты в общих узлах

Два участника правят один узел: второй получит `409 version_conflict` с `current` — показать выбор «оставить моё / оставить их» ([02](02-outbox-and-errors.md#конфликт-версий)). Развязывание — без конфликтов, побеждает последнее.

Смена часового пояса участника совместные узлы не двигает ([04](04-feed-occurrences-notifications.md#смена-часового-пояса)).
