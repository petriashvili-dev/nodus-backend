# Почтовые коды через собственный SMTP

Единственный сетевой транспорт `IEmailSender` — `SmtpEmailSender` на MailKit 4.18.2, как в Eatee. Resend SDK, API key и HTTP-прокси удалены. Эндпоинты, шаблоны OTP, срок действия кодов и ограничения повторов сохранены. Миграция БД и изменения iOS не нужны. Временно подтверждать почту без отправки можно в `ServiceMode__Email=Mock`: см. [15-service-modes.md](15-service-modes.md).

## Настройки Dokploy

Заменить переменные `Resend__*` настройками `EmailOptions__*` из `.env.example`:

```dotenv
EmailOptions__FromAddress=nodus@eatee.ru
EmailOptions__FromName=Nodus
EmailOptions__Smtp__Host=<достижимый-приватный-IP-или-DNS-сервера-Eatee>
EmailOptions__Smtp__Port=25
EmailOptions__Smtp__Security=None
EmailOptions__Smtp__Authentication=false
EmailOptions__Smtp__TimeoutSeconds=15
```

Адрес выше использует домен eatee.ru, разрешённый текущим `ALLOWED_SENDER_DOMAINS`; display name остаётся Nodus. Для адреса на nodus.app потребуется отдельно разрешить и настроить этот домен, включая SPF/DKIM/DMARC. Репозиторий не меняет Postfix, его relay ACL или DNS-записи. Значение Host в примере — заполнитель, не готовый сетевой адрес.

Production Nodus и Eatee находятся на разных серверах. Сеть `eatee-mail` и DNS-имя `eatee-infrastructure-wajrxt_mail` принадлежат Swarm Eatee; добавление одноимённой сети на другом сервере не соединяет их. В compose Nodus зависимости от `eatee-mail` нет. В показанной конфигурации Eatee SMTP-порт не опубликован, поэтому одной замены Host на публичный IP недостаточно.

Предпочтительный маршрут — приватная сеть/VPN между серверами. SMTP должен быть доступен из контейнера Nodus через этот маршрут; доступ ограничивается IP Nodus. В `POSTFIX_mynetworks` на сервере Eatee сохраняется существующая Docker-подсеть и добавляется фактический доверенный адрес источника SMTP-соединения. С учётом Docker NAT этот адрес нужно проверить, а не угадывать. Внутренний режим 25/None/Authentication=false используется только по защищённому каналу.

Альтернатива без приватной сети — настроенный сервером submission endpoint с сертификатом, обязательным TLS и SMTP AUTH. Для него клиент использует Port=587, Security=StartTls, Authentication=true и отдельные Username/Password. Текущая конфигурация Eatee такого endpoint не настраивает. Просто публиковать существующий SMTP без защиты в интернет не следует.

`Security=None` означает отсутствие SMTP TLS на соединении API → Postfix; в межсерверной схеме шифрование обеспечивает защищённый канал. Внешнюю доставку выполняет Postfix. Для другого SMTP доступны обязательный `StartTls` и `SslOnConnect`; при Authentication=true обязательны Username/Password. Проверка сертификата не отключается. `POSTFIX_smtp_tls_security_level` регулирует исходящие SMTP-соединения Postfix и не включает TLS для входящего подключения Nodus.

Настройки проверяются при старте. В Production Live без Host приложение отказывается запускаться; в Mock Host не нужен, транспорт не вызывается. При пустом Host вне Production в режиме Live используется LoggingEmailSender: адрес и тело не логируются, исключение — тело OTP в Development для локального входа. В локальном compose Host задаётся явно для SMTP, достижимого из контейнера; production-сеть к этому compose автоматически не добавляется. Все настройки и значения по умолчанию — [13-environment.md](13-environment.md#smtp).

## Отправка и ошибки

Каждый вызов получает отдельный SMTP-клиент. Письмо — UTF-8 multipart/alternative с HTML и текстовой версией; From и envelope MAIL FROM берутся из настроек, получатель — из существующего потока авторизации. Связанный таймаут ограничивает connect, auth и отправку целиком; отмена HTTP-запроса передаётся наружу.

SMTP 250 означает принятие сервером, не доставку в почтовый ящик. Ошибка QUIT после принятия не превращает отправку в ошибку. При обрыве после DATA до получения ответа результат неопределён, поэтому exactly-once для SMTP не гарантируется.

SMTP 4xx, сеть и таймауты классифицируются как временные, 5xx/адрес/конфигурация — как постоянные. `EmailDeliveryException` содержит только категорию, SMTP-код и признак временности; серверный текст и исходное исключение с возможным содержимым письма не передаются в логи. Добавления автоматических повторов нет: как прежде, EmailOtpService отправляет код синхронно до сохранения его хеша, при ошибке API возвращает обычный internal_error, а запись кода не сохраняется. Успешная отправка затем подтверждается сохранением кода в БД; окно между SMTP и коммитом этим изменением не устраняется.

## Проверки

`Notifications/SmtpEmailSenderTests` использует настоящий MailKit и локальный TCP-сервер: MIME, UTF-8, envelope, отсутствие AUTH/STARTTLS при None, отказ от plaintext при обязательном STARTTLS, 4xx/5xx, безопасные ошибки, таймаут, отмена, недоступность, QUIT после принятия, валидация и отдельный клиент на отправку. `Integration/SmtpAuthenticationTests` проверяет полный путь привязки email через HTTP и реальный SMTP: успешный код принимается API, при SMTP 550 запись кода не сохраняется. Остальные тесты авторизации используют CapturingEmailSender.

```bash
dotnet test Nodus.sln --filter 'FullyQualifiedName~Smtp|FullyQualifiedName~EmailLoggingTests'
```

После развёртывания проверить DNS и приветствие 220 с API-контейнера, затем запросить и проверить код на согласованном почтовом ящике. В оригинале письма проверить From/Return-Path и SPF/DKIM/DMARC. OTP и пароль SMTP не сохранять в логах или shell history. Эти проверки требуют доступа к серверу и ящику; локальные тесты внешнюю доставляемость не подтверждают.
