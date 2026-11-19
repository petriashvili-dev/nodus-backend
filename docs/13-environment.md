# Переменные окружения

Полный список настроек сервера. Значения по умолчанию — в `Nodus.Api/appsettings.json` и в коде (классы `*Options`); любой ключ переопределяется через environment как `Секция__Ключ`, элементы списков — по индексу (`Llm__Providers__0__ApiKey`). В скобках — значение по умолчанию, «—» — пусто. Шаблон `.env` для сервера — `.env.example`.

Новая настройка добавляется сюда, в `appsettings.json` (без секретов) и, если её нужно задавать на сервере, — в `.env.example`.

Для production на сервере Eatee подготовлен [полный набор значений и TODO](17-production-environment.md). Шаблон `.env.example` выбирает Email=Live и SMTP Eatee. Секреты нужно заменить. Порты, Production, логи ASP.NET/EF и запрет автомиграций зафиксированы в Stack.

`DatabaseContextFactory` не содержит строки подключения. Для EF-команд, которым нужна БД, задать `ConnectionStrings__Postgres` в окружении либо передать `-- --ConnectionStrings:Postgres="..."`. Генерация модели работает без подключения; при запуске приложения пустое подключение отклоняется. В `appsettings.json` сохранено пустое значение.

## Развёртывание (Swarm Stack)

Домен `nodus.eatee.ru` и образ `ghcr.io/petriashvili-dev/nodus-backend:latest` зафиксированы в Stack. Остальные настройки приложения загружаются из Environment Dokploy через `env_file: .env`. Сертификат обслуживает существующий Traefik сервера Eatee: [18-domain-and-tls.md](18-domain-and-tls.md).

| Переменная | Описание |
|---|---|
| `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | база и пользователь PostgreSQL; в Stack база и пользователь закреплены как nodus; пароль в ConnectionStrings__Postgres задать вручную таким же |
| `ASPNETCORE_ENVIRONMENT` (`Production`) | окружение: `Production` требует SMTP host в режиме Email=Live; тела писем с кодами логируются только локальным логгером в Development, не в Mock |
| `ASPNETCORE_URLS` (`http://+:8080`) | порты Kestrel; в compose добавлен `9464` для `/metrics` |
| `APP_COMMIT`, `APP_BUILD_TIME` | коммит и время сборки для `/version`; задаются при сборке образа (`GIT_SHA`, `BUILD_TIME`) |

## Обязательные

Без них приложение не стартует (проверка при старте).

| Переменная | Описание |
|---|---|
| `ConnectionStrings__Postgres` | строка подключения PostgreSQL |
| `Jwt__SigningKey` | ключ подписи access-токенов, ≥ 32 байт (`openssl rand -base64 48`) |
| `EmailOtp__HashPepper` | секрет для хешей кодов (email, приглашения) и ключей AI-кеша, ≥ 16 символов. **Не менять** на работающей системе: выданные коды и кеш станут недействительными |
| `EmailOptions__Smtp__Host` | SMTP host. Обязателен в Production при ServiceMode__Email=Live; в Mock почта не отправляется и host не нужен |

## База данных и сеть

| Переменная | Описание |
|---|---|
| `Database__MigrateOnStartup` (`false`) | применять миграции при старте — только для локальной разработки; на сервере — команда `migrate` |
| `Database__StartupTimeoutSeconds` (180) | общий таймаут ожидания БД/миграций в командах Docker `serve`, `migrate`, `wait-for-*`; 1–1800 секунд |
| `Database__StartupRetrySeconds` (2) | интервал повторной проверки БД/миграций; 1–30 секунд |
| `ReverseProxy__TrustForwardedHeaders` (`false`) | доверять `X-Forwarded-*` от reverse proxy |

## JWT

| Переменная | Описание |
|---|---|
| `Jwt__Issuer` (`nodus`), `Jwt__Audience` (`nodus-ios`) | issuer и audience токенов |
| `Jwt__AccessTokenMinutes` (15) | срок жизни access-токена |
| `Jwt__RefreshTokenDays` (60) | срок жизни refresh-токена |
| `Jwt__RotationGraceSeconds` (120) | сколько секунд после ротации ещё принимается старый refresh-токен (потерянный ответ) |

## Коды по email

| Переменная | Описание |
|---|---|
| `EmailOtp__CodeExpiryMinutes` (10) | срок действия кода |
| `EmailOtp__ResendCooldownSeconds` (60) | минимальный интервал между кодами на адрес |
| `EmailOtp__MaxCodesPerHour` (5) | кодов в час на адрес |
| `EmailOtp__MaxAttempts` (5) | попыток ввода одного кода |
| `EmailOtp__ReviewEmails__0…` (—) | адреса демо-аккаунтов App Review — **только на время ревью**, см. [02](02-authentication.md#демо-аккаунт-для-app-review) |
| `EmailOtp__ReviewCode` (—) | фиксированный 6-значный код для этих адресов, секрет |

## SMTP

Режим отправки — [15-service-modes.md](15-service-modes.md): `ServiceMode__Email=Live` по умолчанию; `Mock` использует обязательный `EmailOtp__MockCode` из 6 ASCII-цифр и не вызывает SMTP. Обе настройки серверные, применяются после перезапуска и не публикуются в app-config. MockCode по умолчанию пуст и задаётся через secrets/Environment.

Транспорт MailKit и закрытая сеть Postfix — [14-smtp.md](14-smtp.md). Старые `Resend__*` и HTTP-прокси не используются.

| Переменная | Описание |
|---|---|
| `EmailOptions__FromAddress` (`no-reply@nodus.app`) | адрес отправителя и envelope MAIL FROM, без display name; домен должен быть разрешён почтовым сервером |
| `EmailOptions__FromName` (`Nodus`) | имя отправителя, без переводов строки |
| `EmailOptions__Smtp__Host` (пусто) | достижимый с сервера Nodus IP/DNS SMTP, без схемы/порта. Postfix находится на отдельном сервере Eatee; его Swarm-имя `eatee-infrastructure-wajrxt_mail` доступно только внутри сети Eatee |
| `EmailOptions__Smtp__Port` (25) | порт SMTP, 1–65535 |
| `EmailOptions__Smtp__Security` (`None`) | `None` для закрытой сети; `StartTls` — обязательный STARTTLS; `SslOnConnect` — TLS с подключения |
| `EmailOptions__Smtp__Authentication` (`false`) | выполнять SMTP AUTH |
| `EmailOptions__Smtp__Username`, `EmailOptions__Smtp__Password` (пусто) | обязательны при Authentication=true; пароль — секрет |
| `EmailOptions__Smtp__TimeoutSeconds` (15) | таймаут всей отправки, 1–300 секунд |

## APNs

Серверные push по умолчанию выключены: `Apns__Enabled=false`. Включать после настройки Apple, пересоздав backend-контейнер. Локальные напоминания iOS и SSE/sync от выключателя не зависят. Без `KeyId`, `TeamId`, `BundleId` и `PrivateKey` HTTP-запросы к Apple также не выполняются.

| Переменная | Описание |
|---|---|
| `Apns__Enabled` (`false`) | выключатель всего конвейера server push: без новых push-событий outbox, постановки заданий, доставки и запросов к Apple; старые события/задания при обработке в выключенном режиме завершаются без отправки и не копятся для последующего включения |
| `Apns__KeyId` | Key ID ключа `.p8` (Apple Developer → Keys) |
| `Apns__TeamId` | Team ID (Membership) |
| `Apns__BundleId` | bundle id приложения (`apns-topic`) |
| `Apns__PrivateKey` | содержимое `.p8` (PEM, `\n` допустимы), секрет |
| `Apns__PreferSandbox` (`false`) | сначала sandbox для устройств с неизвестным окружением |

## LLM

Без ключа и модели хотя бы одного провайдера AI-задания завершаются `ai_unavailable`, продолжения — только по шаблонам. `N`: `0` — neuroapi, `1` — Timeweb (базовые URL — в `appsettings.json`).

| Переменная | Описание |
|---|---|
| `Llm__TotalTimeoutSeconds` (150) | общий лимит одного вызова по всем попыткам и провайдерам |
| `Llm__CircuitFailureThreshold` (5), `Llm__CircuitBreakSeconds` (60) | сколько ошибок подряд временно отключают провайдера и на сколько секунд |
| `Llm__LogContent` (`false`) | логировать промпты и ответы (в них тексты пользователей) |
| `Llm__Providers__N__Name` | имя провайдера для метрик |
| `Llm__Providers__N__Priority` | порядок fallback |
| `Llm__Providers__N__BaseUrl` | OpenAI-совместимый URL |
| `Llm__Providers__N__ApiKey` | ключ провайдера, секрет |
| `Llm__Providers__N__Model` | модель; провайдер без ключа или модели пропускается |
| `Llm__Providers__N__TimeoutSeconds` (60) | таймаут одного запроса |
| `Llm__Providers__N__MaxAttempts` (2) | попыток на провайдере до перехода к следующему |
| `Llm__Providers__N__SupportsJsonSchema` (`false`) | поддерживает ли провайдер `json_schema` |
| `Llm__Providers__N__MaxRequestsPerMinute` (60) | собственный лимит запросов в минуту; сверх него — следующий провайдер |
| `Llm__Providers__N__InputCostPerMillion`, `…__OutputCostPerMillion` | цена за миллион токенов для оценки расходов |

## AI-функции и планы

| Переменная | Описание |
|---|---|
| `Ai__MaxTextLength` (4000) | максимальная длина текста диктовки |
| `Ai__MaxJobsPerDay` (50) | AI-заданий черновиков в сутки на пользователя |
| `Ai__MaxAttempts` (3) | попыток LLM на задание |
| `Ai__DraftRetentionDays` (14) | хранение черновиков |
| `Ai__InputRetentionHours` (24) | через сколько стирается текст диктовки |
| `AiContinuations__Enabled` (`true`) | глобальный выключатель «Продолжения нити» |
| `AiContinuations__DebounceSeconds` (20) | задержка перед обработкой узла |
| `AiContinuations__MaxLlmCallsPerUserPerDay` (30) | вызовов модели в сутки на пользователя |
| `Plans__Standard__Features__N`, `Plans__Plus__Features__N` | функции планов; по умолчанию у Plus — все AI-функции, у Standard — ни одной. См. [11](11-plans-and-features.md) |

## Импорт, связи, экземпляры

| Переменная | Описание |
|---|---|
| `Import__MaxContentChars` (200 000) | максимальный размер файла импорта |
| `Import__MaxAiInputChars` (30 000) | максимальный размер чужого файла для AI |
| `Import__MaxNodes` (1000) | узлов за один импорт |
| `Import__MaxAiImportsPerDay` (5) | AI-импортов в сутки |
| `Import__MaxAttempts` (3) | попыток LLM на импорт |
| `Import__PreviewRetentionDays` (7) | срок жизни неподтверждённого предпросмотра |
| `Connections__InviteLifetimeHours` (24) | срок жизни приглашения |
| `Connections__MaxPendingInvites` (10) | активных приглашений на пользователя |
| `Connections__InviteLinkBase` (`https://nodus.app/invite/`) | база ссылки-приглашения |
| `Occurrences__HorizonDays` (45) | на сколько дней вперёд узлы несут экземпляры (локальные уведомления) |
| `Occurrences__MaxCompletionBackdateDays` (30) | насколько в прошлое принимается время выполнения от клиента |

## Лимиты запросов

| Переменная | Описание |
|---|---|
| `RateLimits__AuthPerMinute` (20) | анонимный вход и refresh, на IP |
| `RateLimits__OtpPerMinute` (10) | запросы и проверка кодов, на IP |
| `RateLimits__AiPerMinute` (10) | AI-запросы, на пользователя |

## Фоновая обработка и realtime

| Переменная | Описание |
|---|---|
| `Outbox__PollSeconds` (5) | период опроса outbox |
| `Outbox__BatchSize` (100) | событий за проход |
| `Outbox__MaxAttempts` (10) | попыток на событие |
| `Outbox__RunInBackground` (`true`) | фоновый диспетчер (выключен только в тестах) |
| `Hangfire__Enabled` (`true`) | фоновые задачи (выключены только в тестах) |
| `Hangfire__WorkerCount` (4) | число воркеров |
| `Sse__HeartbeatSeconds` (20) | heartbeat SSE |
| `Sse__RetryMilliseconds` (3000) | рекомендуемая клиенту задержка переподключения |
| `Sse__MaxConnectionsPerUser` (5) | открытых SSE-потоков на пользователя |

## Сроки хранения

| Переменная | Описание |
|---|---|
| `Retention__IdempotencyDays` (30) | сохранённые ответы `Idempotency-Key` |
| `Retention__SyncChangeDays` (90) | журнал sync; старше — курсор получает `410` и snapshot |
| `Retention__ProcessedOutboxDays` (7) | обработанные события outbox |
| `Retention__EmailCodeDays` (7) | записи кодов |
| `Retention__ContinuationCacheDays` (180) | неиспользуемые записи кеша продолжений |

## Конфигурация клиента и наблюдаемость

| Переменная | Описание |
|---|---|
| `AppConfig__CacheSeconds` (15) | через сколько изменения `config set` увидят клиенты, см. [12](12-app-config.md) |
| `Observability__PrometheusEnabled` (`false`) | включает `/metrics` (закрыть от интернета) |
| `Observability__MetricsPort` (—) | отдельный внутренний порт для `/metrics` (в compose 9464) |
| `Observability__OtlpEndpoint` (—) | OTLP-экспорт метрик и трассировок |
| `Observability__JsonLogs` (`true`) | структурированные JSON-логи; локально удобнее `false` |
| `Logging__LogLevel__*` | уровни логирования ASP.NET Core |
