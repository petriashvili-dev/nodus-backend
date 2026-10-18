# Конфигурация клиента (app config)

Код: `AppConfig/`.

Ссылки, версии и прочие настроечные значения для клиента хранятся в БД (таблица `app_config`) и меняются **на работающей системе** командой `config` — без релиза и перезапуска. Клиент получает их публичным `GET /v1/app-config`.

## API

```http
GET /v1/app-config            (без авторизации)
If-None-Match: "<etag>"       (необязательно)

200  ETag: "<etag>"  Cache-Control: no-cache
{
  "values": {
    "links.privacy_policy": "https://nodus.app/privacy",
    "min_supported_version": "1.2.0",
    "features.import_enabled": false,
    "support": { "email": "help@nodus.app" }
  },
  "limits": {
    "ai_draft_text_max_chars": 4000,
    "import_max_chars": 200000,
    "import_ai_max_chars": 30000,
    "display_name_max_chars": 60
  },
  "updated_at": "2026-10-18T12:00:00Z"
}

304  — если ETag совпал
```

- **`values`** — содержимое таблицы: ключ → любое JSON-значение (строка, число, boolean, массив, объект). Набор ключей задаёт договорённость с клиентом (см. [ios/10-app-config.md](ios/10-app-config.md)); сервер их не интерпретирует.
- **`limits`** — только для чтения: копия настроек, которые сервер проверяет сам (`Ai:MaxTextLength`, `Import:*`, длина имени). Их меняют настройками сервера, а не командой: так клиент и сервер всегда используют одни и те же числа.
- **`updated_at`** — момент последнего изменения значения, `null` для пустой таблицы.
- Эндпоинт публичный: ссылки и минимальная версия нужны до входа. Поэтому в `values` **нельзя** класть секреты и что-либо пользовательское. Доступ конкретного пользователя к функциям — в `user.features` ([11](11-plans-and-features.md)).
- `Cache-Control: no-cache` + `ETag` (хеш тела): клиент хранит ответ и каждый раз переспрашивает с `If-None-Match`, получая `304` без тела, если ничего не изменилось.

## Ключи

Строчный snake_case, сегменты через точку, до 100 символов: `links.privacy_policy`, `min_supported_version`. Значение — до 8 КБ JSON.

## Команда `config`

```bash
dotnet run --project Nodus.Api -- config list
dotnet run --project Nodus.Api -- config get links.privacy_policy
dotnet run --project Nodus.Api -- config set links.privacy_policy https://nodus.app/privacy   # строка
dotnet run --project Nodus.Api -- config set features.import_enabled false --json             # JSON-значение
dotnet run --project Nodus.Api -- config set support '{"email":"help@nodus.app"}' --json
dotnet run --project Nodus.Api -- config delete links.faq

docker compose run --rm nodus-api config set links.privacy_policy https://nodus.app/privacy   # на сервере
```

| Код выхода | Значение |
|---|---|
| `0` | значение записано (или уже такое), ключ удалён, `get`/`list` выполнены |
| `1` | ключ не найден (`get`, `delete`) |
| `2` | неверные аргументы, ключ или JSON (справка выводится в консоль) |

Без `--json` значение сохраняется строкой — `1.2.0` и `false` останутся строками. В лог пишется только ключ.

## Кеш

API строит ответ из таблицы и держит его в памяти `AppConfig:CacheSeconds` (15 с). Команда работает отдельным процессом и не может сбросить кеш API, поэтому изменение становится видно клиентам **в течение 15 секунд**. `0` отключает кеш (каждый запрос читает таблицу).

## Почему так

- **БД, а не `appsettings.json`:** значения меняются без релиза и перезапуска.
- **Команда, а не HTTP-админка:** нет новой точки входа с правами записи, которую нужно защищать; менять может только тот, у кого есть доступ к серверу. Как и выдача плана ([11](11-plans-and-features.md#выдача-плана)).
- **Свободный набор ключей:** новая ссылка или переключатель не требуют изменений сервера. Цена — опечатку в ключе сервер не заметит; клиент обязан иметь значения по умолчанию.

Пересмотреть, когда значения начнут менять не разработчики (нужна админка с аудитом) или понадобятся разные значения для разных версий / платформ (параметры запроса и условия на ключах).
