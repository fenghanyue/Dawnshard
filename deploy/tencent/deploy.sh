#!/usr/bin/env bash
# 在腾讯云服务器上部署 Dawnshard。由 tat_run.py 通过腾讯云「自动化助手」以 root 身份执行。
#
# tat_run.py 会在本脚本前面加上这些变量：
#   IMAGE         这次要运行的游戏服务器镜像（带 tag）
#   IMAGE_DIGEST  可选，镜像的 digest（sha256:...），有了才会去国内镜像站拉
#   BUNDLE_B64    要发到服务器的文件打成的 tar.gz（base64），清单见 tat_run.py：
#                 最外层是 /opt/dawnshard 的 compose 和配置，asset-cache/ 是资源缓存的配置，
#                 client-master/ 是改手机端游戏数据表的脚本
#   GHCR_USER、GHCR_TOKEN  可选，拉 ghcr.io 上非公开镜像用的临时凭证
#
# 步骤：拉镜像 → 生成手机端的游戏数据表 → 备份数据库 → 更新资源缓存 → 换上新的 compose 和配置
#       → 重建游戏服务器容器 → 健康检查，没通过就回滚。
# 前两步不动正在用的东西，失败、超时都不会留下改了一半的配置。
# 数据库和 redis 容器不重建，存档不受影响。
set -euo pipefail

APP_DIR="${APP_DIR:-/opt/dawnshard}"
BACKUP_DIR="$APP_DIR/backups"
# 这次部署前的文件，回滚时从这里还原
PREV_DIR="$APP_DIR/.deploy-prev"
KEEP_BACKUPS=10
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/health}"
# 服务器在国内，直连 ghcr.io 每秒只有几十 KB，还会断线；南京大学的镜像站快得多
GHCR_MIRROR="${GHCR_MIRROR:-ghcr.nju.edu.cn}"
# 资源缓存（8081 端口的 nginx），手机经它下载游戏资源
ASSET_CACHE_DIR="${ASSET_CACHE_DIR:-/opt/asset-cache}"
ASSET_CACHE_URL="${ASSET_CACHE_URL:-http://127.0.0.1:8081}"
# 改手机端游戏数据表用的 Python 环境，和下载下来的原版文件
CLIENT_MASTER_DIR="$APP_DIR/client-master"

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

# 服务器上已经有这个镜像，并且就是这个 digest
have_image() {
    [ -n "${IMAGE_DIGEST:-}" ] \
        && docker image inspect --format '{{join .RepoDigests "\n"}}' "$IMAGE" 2>/dev/null \
        | grep -F "@$IMAGE_DIGEST" > /dev/null
}

# 按 digest 拉，docker 会校验内容，镜像站没法偷换
pull_from_mirror() {
    [ -n "${IMAGE_DIGEST:-}" ] && [[ "$IMAGE" == ghcr.io/* ]] || return 1
    local repo="${IMAGE#ghcr.io/}"
    local ref="$GHCR_MIRROR/${repo%:*}@$IMAGE_DIGEST"
    echo "从镜像站拉取 $ref"
    timeout 600 docker pull --quiet "$ref" > /dev/null && docker tag "$ref" "$IMAGE"
}

pull_from_registry() {
    # 上次部署中途被掐断时会留下过期的登录，不清掉连公开镜像都拉不下来
    docker logout ghcr.io > /dev/null 2>&1 || true
    if [ -n "${GHCR_TOKEN:-}" ]; then
        printf '%s' "$GHCR_TOKEN" | docker login ghcr.io -u "${GHCR_USER:-github}" --password-stdin > /dev/null
    fi
    echo "直接拉取 $IMAGE"
    local ok=1
    docker pull --quiet "$IMAGE" > /dev/null || ok=0
    docker logout ghcr.io > /dev/null 2>&1 || true
    [ "$ok" = 1 ]
}

asset_cache_compose() {
    docker compose --project-directory "$ASSET_CACHE_DIR" -f "$ASSET_CACHE_DIR/docker-compose.yml" "$@"
}

echo "== 1/7 拉取镜像 $IMAGE"
if have_image; then
    echo "服务器上已经有这个镜像"
elif ! pull_from_mirror && ! pull_from_registry; then
    echo "!! 镜像拉取失败，服务器没有动" >&2
    exit 1
fi

new_dir=$(mktemp -d)
trap 'rm -rf "$new_dir"' EXIT
printf '%s' "$BUNDLE_B64" | base64 -d | tar -xz --no-same-owner -C "$new_dir"

echo "== 2/7 生成手机端的游戏数据表"
# 按 appsettings.Production.json 排的活动改数据表里的日期，生成新的资源版本，说明见 client-master/client_master.py
tool_dir="$new_dir/client-master"
venv="$CLIENT_MASTER_DIR/venv"
requirements_hash=$(cat "$tool_dir/requirements-build.txt" "$tool_dir/requirements.txt" | sha256sum | cut -d' ' -f1)
install_requirements() {
    local pip=("$venv/bin/pip" install --quiet --disable-pip-version-check --require-hashes --no-deps "$@")
    "${pip[@]}" -r "$tool_dir/requirements-build.txt" \
        && "${pip[@]}" --no-build-isolation -r "$tool_dir/requirements.txt"
}
if [ "$(cat "$venv/.requirements" 2>/dev/null)" != "$requirements_hash" ]; then
    echo "安装改表用的 Python 库"
    rm -rf "$venv"
    python3 -m venv "$venv"
    # 默认用服务器配好的 PyPI 镜像；镜像缺版本或连不上就直接从 pypi.org 装，都会核对校验值
    if ! install_requirements; then
        echo "从 PyPI 镜像安装失败，改从 pypi.org 安装"
        install_requirements --index-url https://pypi.org/simple
    fi
    echo "$requirements_hash" > "$venv/.requirements"
fi
# 活动召唤的奖品配置以这次的镜像里的为准
container=$(docker create "$IMAGE")
copied=1
docker cp "$container:/app/Resources/eventSummon.json" "$tool_dir/eventSummon.json" > /dev/null || copied=0
docker rm "$container" > /dev/null
[ "$copied" = 1 ]
resource_version=$("$venv/bin/python" "$tool_dir/client_master.py" \
    --events "$new_dir/appsettings.Production.json" \
    --event-summons "$tool_dir/eventSummon.json" \
    --source-url "$ASSET_CACHE_URL/dl" \
    --cache-dir "$CLIENT_MASTER_DIR/cache" \
    --out-dir "$ASSET_CACHE_DIR/overrides")
if [[ ! "$resource_version" =~ ^[A-Za-z0-9]{16}$ ]]; then
    echo "!! 生成的资源版本号不对：$resource_version，服务器没有动" >&2
    exit 1
fi
# 服务器把这个版本号告诉手机，版本号变了手机就会去下新的资源清单
python3 - "$new_dir/appsettings.Production.json" "$resource_version" <<'EOF'
import json
import sys

path, version = sys.argv[1:]
with open(path, encoding="utf-8-sig") as f:
    settings = json.load(f)
settings.setdefault("ResourceVersionOptions", {})["Android"] = version
with open(path, "w", encoding="utf-8") as f:
    json.dump(settings, f, ensure_ascii=False, indent=4)
    f.write("\n")
EOF
echo "安卓的资源版本：$resource_version"

echo "== 3/7 备份数据库"
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

echo "== 4/7 更新资源缓存"
asset_files=(default.conf docker-compose.yml)
changed=()
for f in "${asset_files[@]}"; do
    cmp -s "$new_dir/asset-cache/$f" "$ASSET_CACHE_DIR/$f" || changed+=("$f")
done
if [ ${#changed[@]} -gt 0 ]; then
    # 先用临时容器检查新配置，有错就不动正在用的资源缓存
    docker compose --project-directory "$ASSET_CACHE_DIR" -f "$new_dir/asset-cache/docker-compose.yml" config --quiet
    asset_image=$(docker compose --project-directory "$ASSET_CACHE_DIR" -f "$new_dir/asset-cache/docker-compose.yml" config --images | head -n 1)
    if ! docker run --rm --entrypoint nginx -v "$new_dir/asset-cache/default.conf:/etc/nginx/conf.d/default.conf:ro" \
        "$asset_image" -t -q; then
        echo "!! 资源缓存的新配置有错，资源缓存和游戏服务器都没有动" >&2
        exit 1
    fi
    asset_prev="$ASSET_CACHE_DIR/.deploy-prev"
    rm -rf "$asset_prev"
    mkdir -p "$asset_prev" "$ASSET_CACHE_DIR/overrides"
    for f in "${asset_files[@]}"; do
        if [ -f "$ASSET_CACHE_DIR/$f" ]; then
            cp "$ASSET_CACHE_DIR/$f" "$asset_prev/$f"
        fi
    done
    for f in "${changed[@]}"; do
        # cp 覆盖原文件（不换 inode），容器里挂载的单个文件才能看到新内容
        cp "$new_dir/asset-cache/$f" "$ASSET_CACHE_DIR/$f"
        chmod 644 "$ASSET_CACHE_DIR/$f"
    done
    # 配置改得很少，直接重建容器，手机正在下的文件会断一下，游戏会自己重试
    if ! asset_cache_compose up -d --force-recreate; then
        for f in "${asset_files[@]}"; do
            if [ -f "$asset_prev/$f" ]; then
                cp "$asset_prev/$f" "$ASSET_CACHE_DIR/$f"
            fi
        done
        asset_cache_compose up -d --force-recreate || true
        echo "!! 资源缓存更新失败，已还原，游戏服务器没有动" >&2
        exit 1
    fi
    echo "已更新：${changed[*]}"
else
    echo "资源缓存的配置没有变"
fi
# 手机要能从资源缓存下到新生成的文件，才能把版本号告诉手机
version_dir="$ASSET_CACHE_DIR/overrides/dl/manifests/Android/$resource_version"
if [ -d "$version_dir" ]; then
    served=0
    for _ in $(seq 1 20); do
        served=1
        for path in "$version_dir"/*.manifest; do
            name=$(basename "$path")
            if [ "$(curl -fsS --max-time 30 "$ASSET_CACHE_URL/dl/manifests/Android/$resource_version/$name" 2>/dev/null | sha256sum)" != "$(sha256sum < "$path")" ]; then
                served=0
                break
            fi
        done
        [ "$served" = 1 ] && break
        sleep 3
    done
    if [ "$served" != 1 ]; then
        echo "!! 资源缓存给不出新生成的资源清单，游戏服务器没有动" >&2
        exit 1
    fi
    echo "资源缓存能给出资源版本 $resource_version 的清单"
fi

echo "== 5/7 更新 compose 和配置"
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

echo "== 6/7 重建游戏服务器容器"
# 镜像在第 1 步已经拉好，这里不会再去下载
docker compose up -d --no-deps --force-recreate dragaliaapi

echo "== 7/7 健康检查"
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
