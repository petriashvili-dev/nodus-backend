# Production через Docker Swarm Stack

`docker-compose.yml` — Stack-файл v3.8 для Dokploy/Swarm с готовым образом из GHCR. `docker-compose.local.yml` остаётся обычным Compose для локальной сборки. Новые startup-команды входят в образ: перед переходом нужно собрать и опубликовать образ с этими изменениями, образ `ghcr.io/petriashvili-dev/nodus-backend:latest` закреплён в обоих сервисах.

## Подготовка в Dokploy

Текущий Stack предназначен для deployment server Eatee: существующие overlay-сети `eatee-proxy` (Traefik) и `eatee-mail` (SMTP), resolver `regru`. Все значения Stack зафиксированы; `${...}` отсутствуют.

1. Выбрать сервер Eatee, Compose Type = Stack, файл `docker-compose.yml`.
2. Скопировать `.env.example` в Environment. Заменить три секрета; пароль БД повторить в полной строке `ConnectionStrings__Postgres`. Шаблон Email=Live использует SMTP Eatee. Подробнее — [17-production-environment.md](17-production-environment.md).
3. DNS `nodus.eatee.ru` направить на публичный IP сервера Eatee.
4. Убедиться, что выбранный Swarm содержит сети `eatee-proxy` и `eatee-mail`; API подключается к обеим, мигратор и БД — к приватной сети Stack.
5. Выбрать ровно один узел для данных PostgreSQL:

```bash
docker node ls
docker node update --label-add nodus.data=true <NODE_ID>
```

Метка закрепляет БД за диском выбранного узла. Volume `nodus_postgres_data`, пользователь/база `nodus` и таблицы создаются автоматически при первой установке. Для существующей БД сначала сохранить backup и не менять пароль/volume без плана переноса. На нескольких узлах одну метку не ставить.

## Сервисы и старт

| Сервис | Назначение |
|---|---|
| `db` | PostgreSQL 16, одна реплика на узле с nodus.data=true; постоянный именованный volume, /dev/shm 256 МиБ через tmpfs |
| `migrate` | ждёт соединения с БД, выполняет `migrate`, завершается с кодом 0; при неудаче до 5 перезапусков |
| `api` | команда образа `serve`: ждёт соединения и отсутствия pending EF migrations, затем запускает API, SSE и фоновые задачи |

В Swarm нет зависимости от `depends_on`. Сервисы планируются независимо, но новая версия API не начинает обслуживать запросы до готовности миграций **своего образа**. Если схема уже актуальна, API не обязан ждать окончания пустого прохода мигратора. API не применяет миграции: `Database__MigrateOnStartup=false` зафиксирован в Stack.

Ожидание ограничено `Database__StartupTimeoutSeconds` (180 с), проверки повторяются через `Database__StartupRetrySeconds` (2 с). Таймаут даёт ненулевой код выхода; Swarm перезапускает API. Для мигратора отключён HTTP healthcheck образа, поскольку он не запускает HTTP-сервер. Завершённый мигратор с `0/1` running и задачей `Complete / exit 0` — штатный результат, не падение. При новой версии образа/spec мигратор запускается снова.

Healthcheck API проверяет `/health/ready`; startup period — 5 минут. Startup-команды `wait-for-database` и `wait-for-schema` доступны для диагностики и ничего не мигрируют. Обычный `dotnet run` вне Docker сохраняет прежний локальный поток: сначала отдельный `migrate`.

## Сети и параметры

Внутренняя сеть Stack — overlay. PostgreSQL доступен как `db:5432`, API — на 8080, metrics — 9464. Порты не публикуются; внешний HTTPS принимает существующий Traefik Eatee в сети `eatee-proxy`.

API подключён к `eatee-mail` и обращается к `eatee-infrastructure-wajrxt_mail:25` без AUTH/TLS, как Eatee. Это внутренний relay с ограничением по доверенным адресам; отправитель — `nodus@eatee.ru`.

Все три контейнера читают созданный Dokploy `.env` через `env_file`. Пароль БД и полная строка подключения задаются явно, без вложенной интерполяции. API и мигратор дополнительно фиксируют Production, порты, MetricsPort, уровни логов инфраструктуры и Database__MigrateOnStartup=false. PostgreSQL фиксирует имена базы и пользователя nodus. Эти значения имеют приоритет над `.env`.

Все labels находятся в `deploy.labels`; HTTPS использует resolver `regru`, HTTP — собственный middleware `nodus-redirect`. Существующий wildcard Traefik Eatee может обслуживать `nodus.eatee.ru`; выпуском и продлением управляет Traefik, не API. Подробнее — [18-domain-and-tls.md](18-domain-and-tls.md).

ACME/REG.RU/proxy credentials не включать в Environment приложения. Все новые настройки приложения и индексы массивов можно добавлять в Environment без правки Stack.

## Проверка и команды

Stack читает `.env` благодаря явному `env_file`; автоматическая интерполяция переменных здесь не используется. В Dokploy использовать Environment. При ручном CLI-запуске создать `.env` рядом со Stack-файлом, затем:

```bash
docker stack config --compose-file docker-compose.yml > /dev/null
docker stack deploy --with-registry-auth --compose-file docker-compose.yml nodus
docker stack services nodus
docker service ps nodus_migrate --no-trunc
docker service logs nodus_migrate
docker service logs nodus_api
```

Если Dokploy назначил Stack другое имя, заменить префикс `nodus_`. Для приватного GHCR предварительно настроить registry credentials; `--with-registry-auth` передаёт их Swarm-узлам.

Проверить `/version`, `/health/ready`, привязку email с реальной доставкой SMTP и синхронизацию. Полное развёртывание на реальном Swarm и HTTPS проверяются после публикации образа; локальный `stack config` подтверждает совместимость формата, а не наличие узла, volume и сети на сервере.

До последующих изменений Dockerfile, полного Environment и переименования классов локально прошли `docker stack config`, 212 тестов backend и запуск проверочного образа на пустой БД с мигратором и Production Mock. Эти результаты относятся к предыдущей версии. Текущие изменения по просьбе пользователя не собирались и не тестировались; Docker config также повторно не запускался. Рабочий Swarm не развёртывался.

Также из build context исключены старые Windows-style каталоги bin\Release: они вызывали ошибку Linux MSBuild при публикации. Локальные артефакты на диске не удалялись.

## Кеширование сборки

Как в Eatee, runtime-слой с системными пакетами отделён от исходников. Сначала копируется csproj и выполняется restore, затем исходники и publish с `--no-restore`. Изменение только C#-кода позволяет переиспользовать слой NuGet. Метаданные коммита и времени добавляются в конце Dockerfile.

GitHub Actions использует Buildx и постоянный кеш слоёв `type=gha,scope=backend-image,mode=max` между запусками. Для отдельного .NET job добавлен кеш NuGet. Публикуются `latest` и SHA-тег; кеш ускоряет сборку, а SHA-тег позволяет выбрать конкретный образ.

## Обновление и откат

API остаётся в одной реплике, обновление — `stop-first`: экземпляры с локальным SSE hub не перекрываются. Это допускает короткий перерыв. При провале обновления Swarm откатывает API; PostgreSQL и мигратор при провале обновления приостанавливаются для ручной диагностики.

Миграции могут выполняться параллельно со старой версией API, поскольку Swarm обновляет сервисы независимо. Они должны быть совместимыми с предыдущим образом (expand/contract). Для отката образа заменить `latest` на нужный SHA-тег в обоих сервисах Stack. Откат API не откатывает схему БД; перед изменениями нужен backup.

Административную команду запускать внутри работающей API-задачи на узле, где она размещена:

```bash
docker ps --filter label=com.docker.swarm.service.name=nodus_api
docker exec <API_CONTAINER_ID> dotnet Nodus.Api.dll config list
docker exec <API_CONTAINER_ID> dotnet Nodus.Api.dll plan set --email user@example.com --plan plus
```

Backup выполняется на узле PostgreSQL через контейнер задачи `nodus_db`:

```bash
docker ps --filter label=com.docker.swarm.service.name=nodus_db
docker exec <DB_CONTAINER_ID> pg_dump -U nodus -Fc nodus > nodus-backup.dump
```

Подставить собственные POSTGRES_USER/POSTGRES_DB. Восстановление проверять в отдельной БД; работающий volume не удалять.

Источники: [Docker Stack](https://docs.docker.com/reference/cli/docker/stack/deploy/), [переменные и Swarm](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/), [Traefik Swarm](https://doc.traefik.io/traefik/routing/providers/swarm/), [Dokploy Stack и Environment](https://docs.dokploy.com/docs/core/docker-compose).
