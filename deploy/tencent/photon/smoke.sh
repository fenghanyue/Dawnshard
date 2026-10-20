#!/usr/bin/env bash
# 仅供本地/CI 使用，地址固定为回环地址；不连接真实私服。
set -euo pipefail
image="${1:?需要待检查的镜像名}"
here=$(cd "$(dirname "$0")" && pwd)
trial_dir=$(mktemp -d)
compose() {
    docker compose -p dawnshard-luxon-smoke --project-directory "$trial_dir" -f "$trial_dir/docker-compose.yml" "$@"
}
cleanup() {
    compose down > /dev/null 2>&1 || true
    rm -rf "$trial_dir"
}
trap cleanup EXIT
printf 'PHOTON_PUBLIC_HOST=127.0.0.1\n' > "$trial_dir/.env"
printf '{}\n' > "$trial_dir/settings.json"
python3 "$here/prepare.py" --mode configure --settings "$trial_dir/settings.json" \
    --previous "$trial_dir/previous.json" --env-file "$trial_dir/.env" --output "$trial_dir" --image "$image"
compose config --quiet
if ! compose up -d --pull never --wait --wait-timeout 45; then
    compose logs --tail 80
    exit 1
fi
logs=$(compose logs --no-color photon)
for message in 'Starting MasterServer on port 5055' 'Starting GameServer on port 5056'; do
    if ! printf '%s\n' "$logs" | grep -F "$message" > /dev/null; then
        printf '%s\n' "$logs"
        exit 1
    fi
done
container=$(compose ps -q photon)
[ "$(docker inspect --format '{{.Config.User}}' "$container")" = '65532:65532' ]
echo "Luxon v1.5：普通用户、两个 UDP 服务和本机健康检查通过"
