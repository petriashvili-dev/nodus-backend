# Домен и TLS при развёртывании на сервере Eatee

Production API: `https://nodus.eatee.ru/v1`. Этот адрес закреплён в Stack и iOS Release. Debug использует localhost. Для установленного клиента смена адреса требует новой сборки.

## Текущая схема

Dokploy может управлять сервером удалённо. Для этого Stack нужно выбрать deployment server Eatee, на котором уже работают Eatee, SMTP и Traefik. DNS `nodus.eatee.ru` направить на публичный IP этого сервера; прежний IP другого сервера Nodus не подходит.

API подключён к external overlay `eatee-proxy`; порт 8080 не публикуется. Labels находятся в deploy.labels, домен nodus.eatee.ru, entrypoints web/websecure, resolver regru. HTTP redirect использует собственный middleware nodus-redirect и не зависит от dynamic-файлов другого Traefik.

В `eatee-backend/docker-compose.prod.yaml` существующий Traefik Eatee настроен на DNS-01 через REG.RU и заказ `eatee.ru` + `*.eatee.ru`. Уже выпущенный и загруженный wildcard покрывает nodus.eatee.ru. Если подходящего сертификата нет, resolver regru должен выполнить выпуск. Наличие сертификата на работающем сервере в рамках правки файлов не проверялось.

ACME_EMAIL, REGRU credentials и HTTPS_PROXY относятся к существующему Traefik Eatee. Их в Environment приложения Nodus не копировать. Xray/ACME предыдущего сервера Nodus для текущего размещения не используются.

## Перед запуском

- Убедиться, что DNS указывает на Eatee и входящий HTTPS доступен.
- Убедиться, что Traefik Eatee подключён к eatee-proxy, читает Swarm labels и имеет resolver regru.
- Подтвердить сертификат в его хранилище или успешный выпуск в логах.
- После публикации образа и deployment отдельно проверить HTTPS, /version и /health/ready.

Сборки, тесты, Docker validation и deployment по просьбе пользователя не запускались.

Источники: [Traefik: TLS и resolver](https://doc.traefik.io/traefik/reference/routing-configuration/http/tls/overview/), [Dokploy: ручные labels и Stack](https://docs.dokploy.com/docs/core/docker-compose/example).
