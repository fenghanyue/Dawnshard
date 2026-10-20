#!/usr/bin/env bash
# Local/CI only. Exercises the actual production image over UDP and its private HTTP API.
set -euo pipefail
image=${1:?production image required}
checks=${2:?verification image required}
here=$(cd "$(dirname "$0")" && pwd)
trial_dir=$(mktemp -d)
network="dawnshard-rooms-smoke-$$"
redis="dawnshard-rooms-redis-$$"
compose() {
    docker compose -p "dawnshard-rooms-smoke-$$" --project-directory "$trial_dir" -f "$trial_dir/docker-compose.yml" "$@"
}
cleanup() {
    compose down > /dev/null 2>&1 || true
    docker rm -f "$redis" > /dev/null 2>&1 || true
    docker network rm "$network" > /dev/null 2>&1 || true
    rm -rf "$trial_dir"
}
trap cleanup EXIT
printf 'PHOTON_PUBLIC_HOST=127.0.0.1\n' > "$trial_dir/.env"
printf '{}\n' > "$trial_dir/settings.json"
python3 "$here/prepare.py" --mode configure --rooms --settings "$trial_dir/settings.json" \
    --previous "$trial_dir/previous.json" --env-file "$trial_dir/.env" --output "$trial_dir" --image "$image"
# The smoke test owns this network and Redis. It never joins a developer's game network.
sed -i "s/name: dawnshard_default/name: $network/" "$trial_dir/docker-compose.yml"
docker network create "$network" > /dev/null
docker run -d --name "$redis" --network "$network" --network-alias redis redis:8 > /dev/null
if ! compose up -d --pull never --wait --wait-timeout 90; then
    compose logs --tail 60
    exit 1
fi
container=$(compose ps -q photon)
[ "$(docker inspect --format '{{.Config.User}}' "$container")" = '65532:65532' ]
[ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' "$container")" = true ]
# Luxon's test peer accepts numeric endpoints; HTTP uses Docker DNS normally.
address=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")
docker run --rm --network "$network" \
    -e LUXON_TEST_EXTERNAL_SERVER="$address" -e LUXON_STATE_URL=http://photon-rooms:5089 -e LUXON_TEST_LEASE=1 \
    "$checks"
echo "房间镜像：普通用户、只读文件系统、真实 UDP 建房/加人/权限/退出检查通过"
