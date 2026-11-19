# Production на сервере Eatee: переменные и значения

Сценарий: Nodus в том же Swarm, где Eatee, Traefik и SMTP. Готовый Stack без подстановок — [`docker-compose.yml`](../docker-compose.yml), полный Environment — [`.env.example`](../.env.example). Dokploy создаёт `.env`, который читают API, мигратор и PostgreSQL. Поэтому каждый из трёх контейнеров получает полный файл; отдельные env-файлы сервисов здесь не требуются.

## Что заполнить

- `POSTGRES_PASSWORD`: новый пароль отдельной БД Nodus. Этот же пароль вручную указать в `ConnectionStrings__Postgres`; вложенные `${...}` в Environment не использовать.
- `Jwt__SigningKey`: отдельный секрет Nodus не короче 32 байт.
- `EmailOtp__HashPepper`: отдельный секрет Nodus не короче 16 символов.
- APNs: KeyId, TeamId, PrivateKey неизвестны. Без ключей push не отправляется.
- LLM: ключи и модели неизвестны; AI-вызовы без них не работают.
- Ссылка приглашения `https://nodus.app/invite/` остаётся проектным значением: её домен и Universal Links требуют отдельной настройки.

Для каждого из трёх секретов отдельно выполнить `openssl rand -hex 32`. Все `REPLACE_WITH_*` заменить перед развёртыванием. Пароль БД в двух строках должен совпадать. Для уже существующей БД использовать её текущие credentials.

## Что закреплено в Stack

| Параметр | Значение |
|---|---|
| Образ API и мигратора | `ghcr.io/petriashvili-dev/nodus-backend:latest` |
| Домен | `nodus.eatee.ru` |
| Сеть Traefik | external overlay `eatee-proxy` |
| Сеть SMTP | external overlay `eatee-mail` |
| SMTP host | `eatee-infrastructure-wajrxt_mail` в Environment, как у Eatee |
| Volume БД | `nodus_postgres_data`, создаётся автоматически |
| Пользователь и база PostgreSQL | `nodus` / `nodus` |
| Размещение БД | один узел с меткой `nodus.data=true` |
| Окружение | `Production` |
| URLS | `http://+:8080;http://+:9464` |
| Автомиграции API | `Database__MigrateOnStartup=false`; работает отдельный мигратор |
| MetricsPort | `9464` |
| Логи ASP.NET / EF SQL | `Warning` |
| TLS resolver | `regru` у существующего Traefik Eatee |

Поставить метку на узле Swarm сервера Eatee; при первой установке вручную создавать базу и volume не нужно. DNS `nodus.eatee.ru` направить на IP сервера Eatee. В Dokploy выбрать этот deployment server и Compose Type = Stack. Переменные NODUS_IMAGE, NODUS_DOMAIN, NODUS_EDGE_NETWORK и NODUS_POSTGRES_VOLUME больше не используются. В файле Stack нет `${...}` или shell-подстановок.

## Подключение к БД

```dotenv
POSTGRES_DB=nodus
POSTGRES_USER=nodus
POSTGRES_PASSWORD=REPLACE_WITH_NEW_DB_PASSWORD
ConnectionStrings__Postgres=Host=db;Port=5432;Database=nodus;Username=nodus;Password=REPLACE_WITH_NEW_DB_PASSWORD
```

Остальные значения ниже соответствуют полному шаблону. Неизвестные ключи оставлены пустыми, необходимые секреты помечены placeholders. На этом этапе сборки, тесты и Docker validation по просьбе пользователя не запускались.

## Logging

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Logging__LogLevel__Default` | `Information` | Базовый уровень логов. | Задано |

## AllowedHosts

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `AllowedHosts` | `*` | Допустимые HTTP Host; маршрутизация публичного домена выполняется Traefik. | Задано |

## Database

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Database__StartupTimeoutSeconds` | `180` | общий таймаут ожидания БД/миграций в командах Docker `serve`, `migrate`, `wait-for-*`; 1–1800 секунд | Задано |
| `Database__StartupRetrySeconds` | `2` | интервал повторной проверки БД/миграций; 1–30 секунд | Задано |

## Jwt

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Jwt__Issuer` | `nodus` | Издатель JWT. | Задано |
| `Jwt__Audience` | `nodus-ios` | Ожидаемый получатель JWT. | Задано |
| `Jwt__SigningKey` | `REPLACE_WITH_NEW_JWT_SECRET` | ключ подписи access-токенов, ≥ 32 байт (`openssl rand -base64 48`) | TODO: обязательно задать секрет |
| `Jwt__AccessTokenMinutes` | `15` | срок жизни access-токена | Задано |
| `Jwt__RefreshTokenDays` | `60` | срок жизни refresh-токена | Задано |
| `Jwt__RotationGraceSeconds` | `120` | сколько секунд после ротации ещё принимается старый refresh-токен (потерянный ответ) | Задано |

## EmailOtp

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `EmailOtp__CodeExpiryMinutes` | `10` | срок действия кода | Задано |
| `EmailOtp__ResendCooldownSeconds` | `60` | минимальный интервал между кодами на адрес | Задано |
| `EmailOtp__MaxCodesPerHour` | `5` | кодов в час на адрес | Задано |
| `EmailOtp__MaxAttempts` | `5` | попыток ввода одного кода | Задано |
| `EmailOtp__HashPepper` | `REPLACE_WITH_NEW_HASH_PEPPER` | секрет для хешей кодов (email, приглашения) и ключей AI-кеша, ≥ 16 символов. **Не менять** на работающей системе: выданные коды и кеш станут недействительными | TODO: обязательно задать секрет |
| `EmailOtp__MockCode` | **пусто** | Секретный фиксированный код: ровно 6 ASCII-цифр; обязателен в Mock. | Не используется в Live |
| `EmailOtp__ReviewEmails__0` | **пусто** | Первый адрес App Review; пусто означает, что специальный аккаунт не настроен. | Не используется; оставить пустым |
| `EmailOtp__ReviewCode` | **пусто** | Секретный 6-значный код App Review; нужен только с заполненным ReviewEmails. | Не используется; оставить пустым |

## ServiceMode

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `ServiceMode__Email` | `Live` | Mock: фиксированный код без почты. Live: реальный SMTP. | Задано; SMTP Live |

## EmailOptions

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `EmailOptions__FromAddress` | `nodus@eatee.ru` | адрес отправителя и envelope MAIL FROM, без display name; домен должен быть разрешён почтовым сервером | Задано |
| `EmailOptions__FromName` | `Nodus` | имя отправителя, без переводов строки | Задано |
| `EmailOptions__Smtp__Host` | `eatee-infrastructure-wajrxt_mail` | достижимый с сервера Nodus IP/DNS SMTP, без схемы/порта. Postfix находится на отдельном сервере Eatee; его Swarm-имя `eatee-infrastructure-wajrxt_mail` доступно только внутри сети Eatee | Задано; SMTP Live |
| `EmailOptions__Smtp__Port` | `25` | порт SMTP, 1–65535 | Задано; SMTP Live |
| `EmailOptions__Smtp__Security` | `None` | `None` для закрытой сети; `StartTls` — обязательный STARTTLS; `SslOnConnect` — TLS с подключения | Задано; SMTP Live |
| `EmailOptions__Smtp__Authentication` | `false` | выполнять SMTP AUTH | Задано; SMTP Live |
| `EmailOptions__Smtp__Username` | **пусто** | обязательны при Authentication=true; пароль — секрет | Задано; SMTP Live |
| `EmailOptions__Smtp__Password` | **пусто** | обязательны при Authentication=true; пароль — секрет | Задано; SMTP Live |
| `EmailOptions__Smtp__TimeoutSeconds` | `15` | таймаут всей отправки, 1–300 секунд | Задано; SMTP Live |

## RateLimits

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `RateLimits__AuthPerMinute` | `20` | анонимный вход и refresh, на IP | Задано |
| `RateLimits__OtpPerMinute` | `10` | запросы и проверка кодов, на IP | Задано |
| `RateLimits__AiPerMinute` | `10` | AI-запросы, на пользователя | Задано |

## ReverseProxy

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `ReverseProxy__TrustForwardedHeaders` | `true` | доверять `X-Forwarded-*` от reverse proxy | Задано |

## Occurrences

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Occurrences__HorizonDays` | `45` | на сколько дней вперёд узлы несут экземпляры (локальные уведомления) | Задано |
| `Occurrences__MaxCompletionBackdateDays` | `30` | насколько в прошлое принимается время выполнения от клиента | Задано |

## Outbox

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Outbox__PollSeconds` | `5` | период опроса outbox | Задано |
| `Outbox__BatchSize` | `100` | событий за проход | Задано |
| `Outbox__MaxAttempts` | `10` | попыток на событие | Задано |
| `Outbox__RunInBackground` | `true` | фоновый диспетчер (выключен только в тестах) | Задано |

## Retention

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Retention__IdempotencyDays` | `30` | сохранённые ответы `Idempotency-Key` | Задано |
| `Retention__SyncChangeDays` | `90` | журнал sync; старше — курсор получает `410` и snapshot | Задано |
| `Retention__ProcessedOutboxDays` | `7` | обработанные события outbox | Задано |
| `Retention__EmailCodeDays` | `7` | записи кодов | Задано |
| `Retention__ContinuationCacheDays` | `180` | неиспользуемые записи кеша продолжений | Задано |

## Hangfire

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Hangfire__Enabled` | `true` | фоновые задачи (выключены только в тестах) | Задано |
| `Hangfire__WorkerCount` | `4` | число воркеров | Задано |

## Apns

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Apns__Enabled` | `false` | Выключает создание и доставку server push, включая уже поставленные задания. Применяется после пересоздания backend; локальные напоминания и sync не затрагивает. | Задано; push отложены |
| `Apns__KeyId` | **пусто** | Key ID ключа `.p8` (Apple Developer → Keys) | TODO: неизвестно; нужно при включении сервиса |
| `Apns__TeamId` | **пусто** | Team ID (Membership) | TODO: неизвестно; нужно при включении сервиса |
| `Apns__BundleId` | `app.nodus` | Bundle ID текущего iOS-приложения в проекте; должен совпадать с Apple Developer. | Проектное значение; подтвердить для production |
| `Apns__PrivateKey` | **пусто** | содержимое `.p8` (PEM, `\n` допустимы), секрет | TODO: неизвестно; нужно при включении сервиса |
| `Apns__PreferSandbox` | `false` | сначала sandbox для устройств с неизвестным окружением | Задано |

## Sse

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Sse__HeartbeatSeconds` | `20` | heartbeat SSE | Задано |
| `Sse__RetryMilliseconds` | `3000` | рекомендуемая клиенту задержка переподключения | Задано |
| `Sse__MaxConnectionsPerUser` | `5` | открытых SSE-потоков на пользователя | Задано |

## Connections

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Connections__InviteLifetimeHours` | `24` | срок жизни приглашения | Задано |
| `Connections__MaxPendingInvites` | `10` | активных приглашений на пользователя | Задано |
| `Connections__InviteLinkBase` | `https://nodus.app/invite/` | база ссылки-приглашения | Проектное значение; подтвердить для production |

## Llm

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Llm__TotalTimeoutSeconds` | `150` | общий лимит одного вызова по всем попыткам и провайдерам | Задано |
| `Llm__CircuitFailureThreshold` | `5` | Число последовательных ошибок до временного отключения провайдера. | Задано |
| `Llm__CircuitBreakSeconds` | `60` | Длительность временного отключения провайдера в секундах. | Задано |
| `Llm__LogContent` | `false` | логировать промпты и ответы (в них тексты пользователей) | Задано |
| `Llm__Providers__0__Name` | `neuroapi` | имя провайдера для метрик | Задано |
| `Llm__Providers__0__Priority` | `1` | порядок fallback | Задано |
| `Llm__Providers__0__BaseUrl` | `https://neuroapi.host` | OpenAI-совместимый URL | Задано |
| `Llm__Providers__0__ApiKey` | **пусто** | ключ провайдера, секрет | TODO: неизвестно; нужно при включении сервиса |
| `Llm__Providers__0__Model` | **пусто** | модель; провайдер без ключа или модели пропускается | TODO: неизвестно; нужно при включении сервиса |
| `Llm__Providers__0__TimeoutSeconds` | `60` | таймаут одного запроса | Задано |
| `Llm__Providers__0__MaxAttempts` | `2` | попыток на провайдере до перехода к следующему | Задано |
| `Llm__Providers__0__SupportsJsonSchema` | `false` | поддерживает ли провайдер `json_schema` | Задано |
| `Llm__Providers__0__MaxRequestsPerMinute` | `60` | собственный лимит запросов в минуту; сверх него — следующий провайдер | Задано |
| `Llm__Providers__0__InputCostPerMillion` | **пусто** | цена за миллион токенов для оценки расходов | Не задано; учёт стоимости отключён |
| `Llm__Providers__0__OutputCostPerMillion` | **пусто** | Цена миллиона выходных токенов в валюте провайдера; пусто отключает расчёт этой составляющей стоимости. | Не задано; учёт стоимости отключён |
| `Llm__Providers__1__Name` | `timeweb` | имя провайдера для метрик | Задано |
| `Llm__Providers__1__Priority` | `2` | порядок fallback | Задано |
| `Llm__Providers__1__BaseUrl` | `https://api.timeweb.ai` | OpenAI-совместимый URL | Задано |
| `Llm__Providers__1__ApiKey` | **пусто** | ключ провайдера, секрет | TODO: неизвестно; нужно при включении сервиса |
| `Llm__Providers__1__Model` | **пусто** | модель; провайдер без ключа или модели пропускается | TODO: неизвестно; нужно при включении сервиса |
| `Llm__Providers__1__TimeoutSeconds` | `60` | таймаут одного запроса | Задано |
| `Llm__Providers__1__MaxAttempts` | `1` | попыток на провайдере до перехода к следующему | Задано |
| `Llm__Providers__1__SupportsJsonSchema` | `false` | поддерживает ли провайдер `json_schema` | Задано |
| `Llm__Providers__1__MaxRequestsPerMinute` | `60` | собственный лимит запросов в минуту; сверх него — следующий провайдер | Задано |
| `Llm__Providers__1__InputCostPerMillion` | **пусто** | цена за миллион токенов для оценки расходов | Не задано; учёт стоимости отключён |
| `Llm__Providers__1__OutputCostPerMillion` | **пусто** | Цена миллиона выходных токенов в валюте провайдера; пусто отключает расчёт этой составляющей стоимости. | Не задано; учёт стоимости отключён |

## Ai

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Ai__MaxTextLength` | `4000` | максимальная длина текста диктовки | Задано |
| `Ai__MaxJobsPerDay` | `50` | AI-заданий черновиков в сутки на пользователя | Задано |
| `Ai__MaxAttempts` | `3` | попыток LLM на задание | Задано |
| `Ai__DraftRetentionDays` | `14` | хранение черновиков | Задано |
| `Ai__InputRetentionHours` | `24` | через сколько стирается текст диктовки | Задано |

## AppConfig

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `AppConfig__CacheSeconds` | `15` | через сколько изменения `config set` увидят клиенты, см. [12](12-app-config.md) | Задано |

## Plans

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Plans__Plus__Features__0` | `ai_drafts` | AI-черновики для плана Plus. | Задано |
| `Plans__Plus__Features__1` | `ai_continuations` | AI-продолжения для плана Plus. | Задано |
| `Plans__Plus__Features__2` | `ai_import` | AI-импорт для плана Plus. | Задано |

## AiContinuations

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `AiContinuations__Enabled` | `true` | глобальный выключатель «Продолжения нити» | Задано |
| `AiContinuations__DebounceSeconds` | `20` | задержка перед обработкой узла | Задано |
| `AiContinuations__MaxLlmCallsPerUserPerDay` | `30` | вызовов модели в сутки на пользователя | Задано |

## Observability

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Observability__PrometheusEnabled` | `false` | включает `/metrics` (закрыть от интернета) | Задано |
| `Observability__OtlpEndpoint` | **пусто** | OTLP-экспорт метрик и трассировок | Задано |
| `Observability__JsonLogs` | `true` | структурированные JSON-логи; локально удобнее `false` | Задано |

## Import

| Переменная | Значение | Описание | Статус |
|---|---|---|---|
| `Import__MaxContentChars` | `200000` | максимальный размер файла импорта | Задано |
| `Import__MaxAiInputChars` | `30000` | максимальный размер чужого файла для AI | Задано |
| `Import__MaxNodes` | `1000` | узлов за один импорт | Задано |
| `Import__MaxAiImportsPerDay` | `5` | AI-импортов в сутки | Задано |
| `Import__MaxAttempts` | `3` | попыток LLM на импорт | Задано |
| `Import__PreviewRetentionDays` | `7` | срок жизни неподтверждённого предпросмотра | Задано |

## Вычисляемые значения

| Параметр | Откуда берётся |
|---|---|
| `ConnectionStrings__Postgres` | Явно задаётся в Dokploy Environment: host=db, port=5432; пароль совпадает с POSTGRES_PASSWORD. В design-time factory хардкода нет; для EF-команд с подключением передать эту настройку явно. |
| `ASPNETCORE_URLS` | Stack фиксирует `http://+:8080;http://+:9464`. |
| `Database__MigrateOnStartup` | В production Stack зафиксировано false: миграции выполняет отдельный сервис. |
| `DOTNET_gcServer` | В Dockerfile 0. |
| `APP_COMMIT`, `APP_BUILD_TIME` | Build args CI GIT_SHA/BUILD_TIME, вручную не задаются. |
| `Plans__Standard__Features` | Пустой массив в appsettings.json; индексных env-переменных для Standard в шаблоне нет. |

## GitHub Actions: отдельно от Environment backend

| Secret / значение | Статус | Назначение |
|---|---|---|
| `DOKPLOY_URL` | **TODO: неизвестен** | URL панели Dokploy для deploy webhook. |
| `DOKPLOY_API_TOKEN` | **TODO: неизвестен, секрет** | Доступ workflow к deployment API. |
| `DOKPLOY_COMPOSE_ID` | **TODO: неизвестен** | ID сервиса Dokploy с типом Stack. |
| `GITHUB_TOKEN` | Выдаёт GitHub | Авторизация GHCR и публикация образа. |
| `github.repository`, `github.sha`, timestamp | Выдаёт GitHub | Имя образа, SHA-тег и метаданные сборки. |

## Применение

Скопировать `.env.example` в Environment Dokploy, заполнить обязательные TODO, подтвердить сеть и DNS домена и выбрать узел БД. Не добавлять эти секреты в публичный app-config. API и мигратор получают все настройки из созданного Dokploy `.env` через `env_file`; новые ключи и индексы массивов в Stack добавлять не нужно. Короткий `environment` фиксирует инфраструктурные настройки; подключение к БД полностью задаётся через Environment. ACME/REG.RU/proxy credentials в этот Environment не добавлять: ими пользуется Traefik. Изменения применяются после пересоздания задач.

При `ServiceMode__Email=Mock` подтверждение адреса общим кодом служит для разработки и не доказывает доступ к почтовому ящику. Текущий шаблон использует Live и отправляет письма через SMTP Eatee.
