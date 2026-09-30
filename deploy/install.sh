#!/usr/bin/env bash
# Установка и обновление сервера Sentinel на Linux (Ubuntu 22.04+/Debian 12+; на других — при уже установленном Docker).
#
#   curl -fsSL https://raw.githubusercontent.com/cefiro777/sentinel-monitor/main/deploy/install.sh | sudo bash
#
# Что делает: ставит Docker (если нет), скачивает файлы развёртывания в /opt/sentinel, спрашивает режим
# (домен с Let's Encrypt / только IP / за существующим nginx), генерирует пароли и запускает стек.
# Повторный запуск — обновление до последней версии с сохранением данных и настроек.
#
# Без вопросов (автоматизация) — через переменные окружения:
#   SENTINEL_MODE=domain|ip|nginx  SENTINEL_DOMAIN=monitor.example.com  ADMIN_EMAIL=admin@example.com
#   SENTINEL_PORT=8085 (для nginx)  SENTINEL_VERSION=0.4.0 (по умолчанию latest)  SENTINEL_DIR=/opt/sentinel
#   SENTINEL_FILES_FROM=/path/deploy — взять файлы развёртывания из локальной папки, без скачивания
set -euo pipefail

# Весь скрипт — в фигурных скобках: bash прочитает его целиком до запуска. Иначе при «curl | bash»
# команды, читающие stdin (docker compose exec), съедят остаток скрипта, и он молча оборвётся.
{

REPO="cefiro777/sentinel-monitor"
DIR="${SENTINEL_DIR:-/opt/sentinel}"
VERSION="${SENTINEL_VERSION:-latest}"
DEPLOY="$DIR/deploy"
FILES="docker-compose.yml docker-compose.nginx.yml docker-compose.build.yml Caddyfile Caddyfile.ip env.example nginx-sentinel.conf.example"

c_ok() { printf '\033[32m%s\033[0m\n' "$*"; }
c_warn() { printf '\033[33m%s\033[0m\n' "$*"; }
die() { printf '\033[31mОшибка: %s\033[0m\n' "$*" >&2; exit 1; }

# При запуске через «curl | bash» stdin занят скриптом — вопросы читаем с терминала.
ask() { # ask <переменная> <вопрос> [по умолчанию]
    local __var=$1 __q=$2 __def=${3:-} __ans=""
    if [ -n "${!__var:-}" ]; then return; fi
    if [ -r /dev/tty ]; then
        read -r -p "$__q${__def:+ [$__def]}: " __ans </dev/tty || true
    fi
    printf -v "$__var" '%s' "${__ans:-$__def}"
}

[ "$(id -u)" -eq 0 ] || die "запустите от root: curl -fsSL … | sudo bash"
command -v curl >/dev/null || die "нужен curl"

compose() { (cd "$DEPLOY" && docker compose "${COMPOSE_FILES[@]}" "$@"); }

# ------------------------------------------------------------------ Docker
if ! command -v docker >/dev/null || ! docker compose version >/dev/null 2>&1; then
    echo "Устанавливаю Docker (официальный скрипт get.docker.com) ..."
    curl -fsSL https://get.docker.com | sh
    systemctl enable --now docker
fi
docker compose version >/dev/null 2>&1 || die "не найден docker compose v2"

# ------------------------------------------------------------------ файлы развёртывания
if [ "$VERSION" = "latest" ]; then REF="main"; else REF="v${VERSION#v}"; fi
mkdir -p "$DEPLOY"
if [ -n "${SENTINEL_FILES_FROM:-}" ]; then
    # Офлайн: файлы из распакованного архива релиза (папка deploy)
    echo "Беру файлы развёртывания из $SENTINEL_FILES_FROM ..."
    for f in $FILES; do cp "$SENTINEL_FILES_FROM/$f" "$DEPLOY/$f" || die "нет $SENTINEL_FILES_FROM/$f"; done
else
    echo "Скачиваю файлы развёртывания ($REF) в $DEPLOY ..."
    for f in $FILES; do
        curl -fsSL "https://raw.githubusercontent.com/$REPO/$REF/deploy/$f" -o "$DEPLOY/$f.new" || die "не удалось скачать $f"
        mv "$DEPLOY/$f.new" "$DEPLOY/$f"
    done
fi

# ------------------------------------------------------------------ настройки (.env)
ENV="$DEPLOY/.env"
UPDATE=0
if [ -f "$ENV" ]; then
    UPDATE=1
    c_ok "Найдена установка в $DIR — обновляю, настройки и данные сохраняются."
    set -a
    # shellcheck source=/dev/null
    . "$ENV"
    set +a
    if grep -q '^SENTINEL_MODE=' "$ENV"; then MODE=$SENTINEL_MODE
    elif grep -q '^CADDYFILE=Caddyfile.ip' "$ENV"; then MODE=ip
    else MODE=domain; fi
    if [ "$VERSION" != "latest" ]; then sed -i "s/^SENTINEL_VERSION=.*/SENTINEL_VERSION=${VERSION#v}/" "$ENV"; fi
else
    echo
    echo "Как сервер будет доступен агентам и инженерам?"
    echo "  1) domain — свой домен (monitor.example.com), порты 80/443 свободны: HTTPS от Let's Encrypt автоматически"
    echo "  2) ip     — только IP-адрес, без домена: HTTPS с внутренним сертификатом (агенты доверяют по pin)"
    echo "  3) nginx  — на сервере уже есть nginx/Apache на 80/443: Sentinel слушает локальный порт, проксируете сами"
    MODE_IN=${SENTINEL_MODE:-}
    ask MODE_IN "Режим (1/2/3)" "1"
    case "$MODE_IN" in 1|domain) MODE=domain ;; 2|ip) MODE=ip ;; 3|nginx) MODE=nginx ;; *) die "неизвестный режим" ;; esac

    DEFAULT_HOST=""
    if [ "$MODE" = "ip" ]; then DEFAULT_HOST=$(curl -fsS4 --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}'); fi
    ask SENTINEL_DOMAIN "$([ "$MODE" = ip ] && echo 'IP-адрес сервера' || echo 'Домен (A-запись уже указывает на этот сервер)')" "$DEFAULT_HOST"
    [ -n "$SENTINEL_DOMAIN" ] || die "не указан домен/IP"
    ask ADMIN_EMAIL "Email администратора (логин в дашборд)" "admin@${SENTINEL_DOMAIN}"
    if [ "$MODE" = "nginx" ]; then ask SENTINEL_PORT "Локальный порт для проксирования" "8085"; fi
    SENTINEL_PORT=${SENTINEL_PORT:-8085}

    umask 077
    cat > "$ENV" <<EOF
# Создан install.sh $(date '+%Y-%m-%d %H:%M'). Изменения применяются: cd $DEPLOY && docker compose ... up -d
SENTINEL_MODE=$MODE
SENTINEL_DOMAIN=$SENTINEL_DOMAIN
SENTINEL_VERSION=${VERSION#v}
POSTGRES_PASSWORD=$(tr -dc 'A-Za-z0-9' </dev/urandom | head -c 32)
ADMIN_EMAIL=$ADMIN_EMAIL
ADMIN_PASSWORD=
SENTINEL_PORT=$SENTINEL_PORT
$([ "$MODE" = ip ] && echo "CADDYFILE=Caddyfile.ip" || echo "#CADDYFILE=Caddyfile.ip")
# SOCKS5/HTTP-прокси для Telegram, если api.telegram.org недоступен: socks5://user:pass@host:1080
TELEGRAM_PROXY=
EOF
    chmod 600 "$ENV"
fi

COMPOSE_FILES=(-f docker-compose.yml)
[ "$MODE" = "nginx" ] && COMPOSE_FILES+=(-f docker-compose.nginx.yml)

# ------------------------------------------------------------------ запуск
mkdir -p "$DEPLOY/data"
echo "Загружаю образы ..."
# Без доступа к реестру (офлайн, образы загружены через docker load) — продолжаем с локальными.
compose pull || c_warn "Не удалось обновить образы из реестра — запускаю с имеющимися локально."
compose up -d --remove-orphans

echo -n "Жду запуска сервера "
for _ in $(seq 1 90); do
    if compose exec -T server sh -c 'exit 0' </dev/null >/dev/null 2>&1 && \
       compose logs server 2>/dev/null | grep -q "Application started"; then break; fi
    echo -n "."; sleep 2
done
echo

# shellcheck source=/dev/null
. "$ENV"
case "$MODE" in
    domain|ip) URL="https://$SENTINEL_DOMAIN" ;;
    nginx) URL="https://$SENTINEL_DOMAIN (после настройки nginx; сейчас http://127.0.0.1:$SENTINEL_PORT)" ;;
esac

c_ok "Sentinel запущен: $URL"
PWD_FILE="$DEPLOY/data/server/initial-admin-password.txt"
if [ "$UPDATE" = 0 ] && [ -f "$PWD_FILE" ]; then
    echo; sed '1s/^\xEF\xBB\xBF//; s/\r$//' "$PWD_FILE"; echo
    c_warn "Пароль сохранён в $PWD_FILE — смените его после входа и удалите файл."
fi
if [ "$MODE" = "nginx" ] && [ "$UPDATE" = 0 ]; then
    echo; echo "Настройте nginx по примеру: $DEPLOY/nginx-sentinel.conf.example (proxy_pass на 127.0.0.1:$SENTINEL_PORT), затем certbot --nginx -d $SENTINEL_DOMAIN"
fi
if [ "$MODE" = "ip" ] && [ "$UPDATE" = 0 ]; then
    echo; echo "Браузер предупредит о сертификате (внутренний CA). Агенты доверяют серверу по pin — команда установки на странице «Установка агентов» уже его содержит."
fi
echo
echo "Бэкапы БД и ключей: $DEPLOY/data/backups (ежесуточно, 14 копий) — копируйте их за пределы сервера."
echo "Обновление: повторите эту же команду установки."
exit 0
}
