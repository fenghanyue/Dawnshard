#!/usr/bin/env bash
# 在腾讯云服务器上部署 Dawnshard。由 tat_run.py 通过腾讯云「自动化助手」以 root 身份执行。
#
# tat_run.py 会在本脚本前面加上这些变量：
#   IMAGE        这次要运行的游戏服务器镜像
#   COMPOSE_B64  deploy/tencent/docker-compose.yml 的内容（base64）
#   CONFIG_B64   deploy/tencent/appsettings.Production.json 的内容（base64）
#   GHCR_USER、GHCR_TOKEN  可选，拉 ghcr.io 上非公开镜像用的临时凭证
#
# 步骤：备份数据库 → 换上新的 compose 和配置 → 拉镜像 → 重建游戏服务器容器 → 健康检查，没通过就回滚。
# 数据库和 redis 容器不重建，存档不受影响。
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/dawnshard}"
BACKUP_DIR="$APP_DIR/backups"
KEEP_BACKUPS=10
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/health}"

: "${IMAGE:?IMAGE 没有设置}"
: "${COMPOSE_B64:?COMPOSE_B64 没有设置}"
: "${CONFIG_B64:?CONFIG_B64 没有设置}"

cd "$APP_DIR"
if [ ! -f .env ]; then
    echo "缺少 $APP_DIR/.env（数据库密码等），停止部署" >&2
    exit 1
fi

env_value() { grep -E "^$1=" .env | head -n 1 | cut -d= -f2-; }

restore_files() {
    cp docker-compose.yml.prev docker-compose.yml
    if [ -f appsettings.Production.json.prev ]; then
        cp appsettings.Production.json.prev appsettings.Production.json
    fi
}

rollback() {
    echo "!! $1，回滚到上一个版本" >&2
    restore_files
    docker compose up -d --no-deps --force-recreate dragaliaapi
    exit 1
}

echo "== 1/5 备份数据库"
mkdir -p "$BACKUP_DIR"
backup="$BACKUP_DIR/db-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker compose exec -T postgres \
    pg_dump -U "$(env_value POSTGRES_USER)" -d "$(env_value POSTGRES_DB)" -Fc > "$backup"
if [ ! -s "$backup" ]; then
    echo "备份文件是空的，停止部署" >&2
    exit 1
fi
echo "已备份到 $backup（$(du -h "$backup" | cut -f1)）"
find "$BACKUP_DIR" -maxdepth 1 -name 'db-*.dump' -printf '%T@ %p\n' \
    | sort -rn | tail -n +$((KEEP_BACKUPS + 1)) | cut -d' ' -f2- | xargs -r rm -f --

echo "== 2/5 更新 compose 和配置"
printf '%s' "$COMPOSE_B64" | base64 -d | sed "s#__DRAGALIA_API_IMAGE__#${IMAGE}#" > docker-compose.yml.new
printf '%s' "$CONFIG_B64" | base64 -d > appsettings.Production.json.new
docker compose -f docker-compose.yml.new config --quiet
python3 -m json.tool appsettings.Production.json.new > /dev/null
cp docker-compose.yml docker-compose.yml.prev
if [ -f appsettings.Production.json ]; then
    cp appsettings.Production.json appsettings.Production.json.prev
fi
mv docker-compose.yml.new docker-compose.yml
mv appsettings.Production.json.new appsettings.Production.json
# 容器里的程序不是 root，要能读到这个文件
chmod 644 appsettings.Production.json

echo "== 3/5 拉取镜像 $IMAGE"
if [ -n "${GHCR_TOKEN:-}" ]; then
    printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "${GHCR_USER:-github}" --password-stdin > /dev/null
fi
pull_ok=1
docker compose pull --quiet dragaliaapi || pull_ok=0
if [ -n "${GHCR_TOKEN:-}" ]; then
    docker logout ghcr.io > /dev/null || true
fi
if [ "$pull_ok" != 1 ]; then
    restore_files
    echo "!! 镜像拉取失败，配置已还原，游戏服务器没有动" >&2
    exit 1
fi

echo "== 4/5 重建游戏服务器容器"
docker compose up -d --no-deps --force-recreate dragaliaapi

echo "== 5/5 健康检查"
healthy=0
for _ in $(seq 1 60); do
    if curl -fsS --max-time 5 "$HEALTH_URL" 2>/dev/null \
        | python3 -c 'import json, sys; sys.exit(json.load(sys.stdin).get("status") != "Healthy")' 2>/dev/null; then
        healthy=1
        break
    fi
    sleep 3
done
if [ "$healthy" != 1 ]; then
    docker compose logs --tail 60 dragaliaapi >&2 || true
    rollback "健康检查没通过"
fi

echo "部署完成：$IMAGE"
docker compose ps --format 'table {{.Service}}\t{{.Image}}\t{{.Status}}'
