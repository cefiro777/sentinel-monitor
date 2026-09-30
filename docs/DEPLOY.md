# Развёртывание сервера Sentinel

Быстрый путь на Linux — одна команда (ставит Docker, спрашивает режим, генерирует пароли, запускает; повторный запуск — обновление):

```bash
curl -fsSL https://raw.githubusercontent.com/cefiro777/sentinel-monitor/main/deploy/install.sh | sudo bash
# без вопросов:  ... | sudo SENTINEL_MODE=domain SENTINEL_DOMAIN=monitor.example.com ADMIN_EMAIL=admin@example.com bash
```

На Windows Server 2016+ — установщик `SentinelServer-Setup-x.y.z.exe` из релизов (PostgreSQL внутри, см. раздел «Windows» в конце).

Ниже — ручная установка через Docker Compose и подробности.

## Требования

- VPS: Ubuntu 22.04/24.04 (или Debian 12), 2 vCPU, 2 ГБ RAM, 20 ГБ диска. Для десятков клиентов этого хватает с запасом.
- Открытые порты **80 и 443** (входящие). Больше ничего снаружи не нужно: агенты сами подключаются к серверу.
- Один из вариантов адреса:
  - **Домен** (рекомендуется): A-запись `monitor.example.com → IP VPS`. Caddy сам получит и будет обновлять сертификат Let's Encrypt.
  - **Только IP**: Caddy поднимет внутренний CA, агенты доверяют серверу по pin корневого сертификата (pin автоматически попадает в команду установки). Браузер будет предупреждать о сертификате.

## 1. Docker

```bash
curl -fsSL https://get.docker.com | sh
```

## 2. Код и настройки

```bash
git clone https://github.com/cefiro777/sentinel-monitor.git /opt/sentinel && cd /opt/sentinel/deploy
cp env.example .env
nano .env
```

`.env`:

```
SENTINEL_DOMAIN=monitor.example.com      # или IP: 203.0.113.10
#CADDYFILE=Caddyfile.ip                  # раскомментировать для режима IP
POSTGRES_PASSWORD=<длинный случайный>
ADMIN_EMAIL=admin@example.com
ADMIN_PASSWORD=                          # пусто — сгенерируется и будет в логе
TELEGRAM_PROXY=                          # если api.telegram.org с VPS недоступен: socks5://user:pass@host:1080
```

## 3. Запуск

```bash
docker compose up -d                 # готовый образ ghcr.io/cefiro777/sentinel-monitor-server (SENTINEL_VERSION в .env)
docker compose logs -f server        # ждём "Now listening"
cat data/server/initial-admin-password.txt   # пароль админа, если ADMIN_PASSWORD пуст
```

Обновление: `docker compose pull && docker compose up -d`.
Собрать образ из исходников (свои правки): `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build` (5–10 минут).

### Вариант: за уже работающим nginx

Если на VPS порты 80/443 заняты другим nginx (другие сайты), Caddy не нужен:

```bash
docker compose -f docker-compose.yml -f docker-compose.nginx.yml up -d --build   # сервер только на 127.0.0.1:${SENTINEL_PORT:-8085}
sudo cp nginx-sentinel.conf.example /etc/nginx/sites-available/sentinel          # поправить server_name
sudo ln -s /etc/nginx/sites-available/sentinel /etc/nginx/sites-enabled/ && sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d monitor.example.com --redirect                            # HTTPS от Let's Encrypt
```

Все дальнейшие команды `docker compose` в этом варианте — с обоими `-f`, иначе поднимется Caddy и упадёт на занятых портах.

## 4. Первый вход

1. `https://<домен или IP>` → логин админа.
2. **Безопасность** → настроить 2FA (без неё недоступны команды на серверах клиентов).
3. **Пользователи** → завести инженеров (роль Engineer) — не работайте под админом.
4. **Оповещения** → канал Telegram/Max/Email → «Тест» → маршрут «все клиенты, Внимание и выше».

## 5. Дистрибутив агента

На машине с .NET SDK 10 (можно на своей рабочей):

```powershell
.\deploy\build-agent.ps1 -Version 0.2.0
```

Полученный `artifacts\SentinelAgent-0.2.0.zip` загрузить на странице **Установка агентов → Дистрибутив агента → Опубликовать**. Сервер подпишет пакет; агенты потом обновляются сами.

## 6. Первый агент

**Клиенты** → создать клиента. **Установка агентов** → токен → скопировать команду PowerShell → выполнить на сервере клиента от администратора.
Через минуту хост появится в «Обзоре» с проверкой «Система». Дальше — проверки/бэкапы/регистраторы на странице хоста.

Для Windows Server 2008 R2: сначала убедитесь, что установлен .NET Framework 4.8 и включён TLS 1.2 (KB3140245 + агент сам пропишет реестр при `install`); если PowerShell 2.0 не может скачать по HTTPS — используйте «ручной» вариант со страницы (скачать zip, `enroll`, `install`).

## 7. Что бэкапить

`deploy/data/`:
- `server/keys/` — **критично**: `data.key` (секреты в БД), `signing.key` (подпись команд и пакетов), `jwt.key`. Потеря = переустановка всех агентов.
- `backups/` — сервис `backup` кладёт сюда ежесуточно дамп БД и архив ключей (14 копий). Забирайте эту папку наружу (rsync на другой сервер, облако).
- `postgres/` — живая БД (для восстановления удобнее дамп из `backups/`).
- `caddy/` — сертификаты (в режиме IP — корневой CA, к которому привязаны агенты; терять нельзя).

Восстановление: развернуть стек на новом VPS, остановить `server`, `pg_restore -d sentinel <dump>`, положить `keys/` в `data/server/keys`, запустить.

## 8. Полезное

```bash
docker compose logs -f server                 # логи сервера
docker compose exec postgres psql -U sentinel # консоль БД
docker compose restart server                 # перезапуск
docker compose down && docker compose up -d --build   # обновление с пересборкой
```

Настройки сервера — переменные `Sentinel__*` в `docker-compose.yml` (см. `src/Sentinel.Server/appsettings.json`): сроки хранения истории (`Sentinel__Retention__MetricsDays` и т.д.), порог офлайна агента, TTL команд.

## Windows (без Docker)

`SentinelServer-Setup-x.y.z.exe` — Windows Server 2016+ / Windows 10+ x64. Тихая установка:
`SentinelServer-Setup.exe /VERYSILENT /HOST=monitor.corp.local /PORT=443 /ADMIN=admin@corp.local`.

| Что | Где |
|---|---|
| Программа (сервер, PostgreSQL 16) | `C:\Program Files\Sentinel\Server` |
| Настройки (`appsettings.json`: адрес, порт, строка подключения, TLS) | `%ProgramData%\Sentinel\Server` |
| Ключи, сертификат, логи, бэкапы | `%ProgramData%\Sentinel\Server\{keys,tls,logs,backups}` |
| Кластер PostgreSQL (порт 5432 или следующий свободный, только localhost) | `%ProgramData%\Sentinel\PostgreSQL` |
| Службы | `SentinelPostgreSQL` (NetworkService), `SentinelServer` (виртуальная учётка `NT SERVICE\SentinelServer`) |
| Журнал установки | `%ProgramData%\Sentinel\Server\setup.log` |

HTTPS — самоподписанный сертификат (`tls\server.pfx`, 20 лет): агенты доверяют ему по отпечатку (pin выдаётся при регистрации),
браузер предупредит один раз. Свой сертификат: в `appsettings.json` поставьте `"Tls": { "Mode": "pfx", "PfxPath": "C:\\certs\\monitor.pfx", "PfxPassword": "..." }`
и перезапустите службу `SentinelServer` (агентам, зарегистрированным с pin самоподписанного сертификата, после смены нужна перерегистрация).

Бэкап — задача планировщика «Sentinel Server Backup» (03:00, 14 копий дампа и архива ключей с настройками). Удаление программы данные не трогает:
повторная установка подхватит базу и настройки.
