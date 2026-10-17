# Правила работы в репозитории nodus-backend

Общие правила для AI-агентов и разработчиков. Обзор проекта — `README.md`, как работает функционал — `docs/`.

## Ветки и git

- Работаем **только в ветке `dev`**. Фича-ветки не создавать, в `main`/`master` напрямую не коммитить.
- Remote: `origin` = `git@github.com:petriashvili-dev/nodus-backend.git`. Если remote не настроен — привязать его.
- **В начале сессии** подтянуть `main` в локальный `dev`:
  ```bash
  git checkout dev
  git fetch origin
  git merge origin/main
  ```
  Конфликты разрешить и показать пользователю; если слияние нетривиальное (несвязанная история, удаление чужих изменений) — сначала спросить.
- **После реализации фичи** — сборка, тесты, документация (см. ниже), затем отчёт пользователю. **Коммит и пуш в `origin dev` — только после явного подтверждения пользователя**:
  ```bash
  git add -A && git commit
  git push origin dev
  ```
- Одна фича — один коммит. Сообщение на английском: краткий заголовок в повелительном наклонении + список изменений.
- Секреты не коммитить: `appsettings.Development.json`, `appsettings.Local.json`, `.env` в `.gitignore`; ключи только через environment.

## Документация

Каждая новая фича или изменение поведения документируется **в том же коммите**, что и код:

- `docs/*.md` — на русском, описывают **как работает**: потоки, правила, API (запросы/ответы, коды ошибок), принятые решения, ссылки на файлы кода. Новая область → новый нумерованный файл и строка в `docs/README.md`.
- `README.md` — таблица API и таблица принятых решений, если они меняются.
- `docs/09-operations.md` — новые параметры конфигурации, сроки хранения, метрики, тестовые наборы.
- Если меняется контракт для клиента (эндпоинты, поля DTO, коды ошибок, поведение sync/push) — обновить руководство для iOS `docs/ios/` (в т. ч. справочник `docs/ios/09-api-reference.md`) и требования к iOS `~/Desktop/nodus_docs/03_Nodus_iOS_Requirements.md` (вне репозитория).
- Новые настройки — также в `Nodus.Api/appsettings.json` (без секретов).

## Тесты

- xUnit, проект `Nodus.Tests`: unit-тесты (`Recurrence/`, `Ai/`, `Notifications/`) и интеграционные (`Integration/`) — реальное приложение на TestServer + PostgreSQL.
- PostgreSQL для тестов: контейнер на `localhost:55432` (`docker run -d --name nodus-test-pg -p 55432:5432 -e POSTGRES_USER=nodus -e POSTGRES_PASSWORD=nodus postgres:16`) или `NODUS_TEST_POSTGRES`. Каждый набор создаёт и удаляет свою БД.
- Каждая фича покрывается тестами: основной сценарий, граничные случаи, негативные проверки доступа/валидации, идемпотентность повторов.
- Внешние системы в тестах подменяются фейками `TestApp` (`FakeLlmClient`, `FakeApnsClient`, `CapturingEmailSender`), время — `ManualTimeProvider`. Outbox в тестах обрабатывается явно (`OutboxDispatcher.DispatchBatchAsync`).
- Перед отчётом и коммитом: `dotnet build Nodus.sln` без предупреждений компилятора и `dotnet test` — всё зелёное. Не сообщать «готово» при падающих тестах; если что-то не проверено — сказать прямо.

## Архитектурные правила

- Модули — папки в `Nodus.Api/` (`Nodes/`, `Sync/`, `AiDrafts/`…), инфраструктура — `Infrastructure/`. Регистрация — `NodusApplication.cs`.
- Minimal API; обработчики возвращают `ApiResult`, ошибки — problem details со стабильным `code`.
- **Мутации:** `.WithIdempotency()` + изменения через `UnitOfWork` и `ChangeJournal` (запись в sync для всех получателей). Побочные эффекты (push, фоновые задачи) — только через `outbox_events`, задачи — через `IBackgroundJobs`, никогда напрямую после коммита.
- Доступ к узлам — только через `NodeAccess`. Пользователь — только из токена (`GetUserId()`).
- Время — `TimeProvider` (не `DateTime.UtcNow`), моменты в UTC, локальные даты/время + IANA-пояс отдельно.
- Внешние сервисы — за интерфейсами (`ILlmClient`, `IApnsClient`, `IEmailSender`); провайдеры не вызываются из endpoint'ов.
- AI никогда не создаёт узлы без подтверждения пользователя.
- В логах и метриках нет пользовательского контента (тексты узлов, диктовка, промпты, файлы импорта), токенов и кодов.
- Схема БД — EF Core миграции: `cd Nodus.Api && dotnet ef migrations add <Name> -o Infrastructure/Persistence/Migrations`. Миграции применяются отдельным шагом релиза (`migrate`), не при старте.
- Стратегии по ключу (парсеры форматов и т. п.) — `ISelectionResolver` (`Common/Resolvers/`).
- Проверенные подходы к инфраструктуре (APNs, LLM-провайдеры, токены, resolver'ы) — в `~/Desktop/eatee/eatee-backend`; секреты оттуда не копировать.
- Код пишется в стиле окружающего: плотность комментариев, именование, идиомы.

## Skills

Инструкции для типовых задач лежат в **`.agents/skills/`** — общий источник для всех агентов (Codex читает `.agents/skills`, Claude Code — `.claude/skills`, который является симлинком `../.agents/skills`, так что оба видят одно и то же; людям — как чек-листы). Редактировать и добавлять skills — только в `.agents/skills/<name>/SKILL.md`; симлинк не заменять папкой.

| Skill | Когда |
|---|---|
| `minimal-api-feature` | добавление фичи/эндпоинта по конвенциям проекта |
| `csharp-dotnet` | написание и ревью C# / .NET 10 |
| `efcore-postgres` | сущности, запросы, транзакции, блокировки, производительность БД |
| `ef-migrations` | изменение схемы, безопасные миграции, откат |
| `background-jobs` | outbox, Hangfire, идемпотентные задачи |
| `integration-tests` | тесты на TestServer + PostgreSQL, фейки, диагностика |
| `api-security-review` | проверка по OWASP API Top 10 |
| `architecture-decision` | архитектурные решения и ADR |
| `docker-deploy` | Dockerfile, compose, релиз, откат, бэкапы |
| `observability-performance` | логи, метрики, нагрузочные тесты, медленные запросы |

Новый повторяющийся процесс — оформить новым skill.

## Окружение

- .NET 10 SDK; пакеты NuGet могут быть доступны только из локального кэша (нет сети) — новые зависимости сначала проверить в `~/.nuget/packages`.
- Локальный запуск и конфигурация — `docs/09-operations.md`.
