#!/usr/bin/env bash
# 在腾讯云服务器上部署 Dawnshard。由 tat_run.py 通过腾讯云「自动化助手」以 root 身份执行。
#
# tat_run.py 会在本脚本前面加上这些变量：
#   IMAGE       这次要运行的游戏服务器镜像
#   BUNDLE_B64  docker-compose.yml 和各个配置文件打成的 tar.gz（base64），清单见 tat_run.py 的 CONFIG_FILES
#   GHCR_USER、GHCR_TOKEN  可选，拉 ghcr.io 上非公开镜像用的临时凭证
#
# 步骤：备份数据库 → 换上新的 compose 和配置 → 拉镜像 → 重建游戏服务器容器 → 健康检查，没通过就回滚。
# 数据库和 redis 容器不重建，存档不受影响。
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/dawnshard}"
BACKUP_DIR="$APP_DIR/backups"
# 这次部署前的文件，回滚时从这里还原
PREV_DIR="$APP_DIR/.deploy-prev"
KEEP_BACKUPS=10
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/health}"

: "${IMAGE:?IMAGE 没有设置}"
: "${BUNDLE_B64:?BUNDLE_B64 没有设置}"

cd "$APP_DIR"
if [ ! -f .env ]; then
    echo "缺少 $APP_DIR/.env（数据库密码等），停止部署" >&2
    exit 1
fi

env_value() { grep -E "^$1=" .env | head -n 1 | cut -d= -f2-; }

files=()
restore_files() {
    for f in "${files[@]}"; do
        if [ -f "$PREV_DIR/$f" ]; then
            cp "$PREV_DIR/$f" "$f"
        else
            rm -f -- "$f"
        fi
    done
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
new_dir=$(mktemp -d)
trap 'rm -rf "$new_dir"' EXIT
printf '%s' "$BUNDLE_B64" | base64 -d | tar -xz --no-same-owner -C "$new_dir"
sed -i "s#__DRAGALIA_API_IMAGE__#${IMAGE}#" "$new_dir/docker-compose.yml"
mapfile -t files < <(find "$new_dir" -maxdepth 1 -type f -printf '%f\n' | sort)
for f in "${files[@]}"; do
    case "$f" in
        *.json) python3 -m json.tool "$new_dir/$f" > /dev/null ;;
    esac
done
docker compose -f "$new_dir/docker-compose.yml" --project-directory "$APP_DIR" config --quiet
rm -rf "$PREV_DIR"
mkdir -p "$PREV_DIR"
for f in "${files[@]}"; do
    if [ -f "$f" ]; then
        cp "$f" "$PREV_DIR/$f"
    fi
    cp "$new_dir/$f" "$f"
    # 容器里的程序不是 root，要能读到这些文件
    chmod 644 "$f"
done
echo "已更新：${files[*]}"

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
