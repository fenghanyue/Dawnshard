#!/usr/bin/env bash
# Local/CI only. Exercises the actual production image over UDP and its private HTTP API.
set -euo pipefail
image=${1:?production image required}
checks=${2:?verification image required}
here=$(cd "$(dirname "$0")" && pwd)
trial_dir=$(mktemp -d)
network="dawnshard-rooms-smoke-$$"
redis="dawnshard-rooms-redis-$$"
fake_api="dawnshard-rooms-api-$$"
compose() {
    docker compose -p "dawnshard-rooms-smoke-$$" --project-directory "$trial_dir" -f "$trial_dir/docker-compose.yml" "$@"
}
cleanup() {
    compose down > /dev/null 2>&1 || true
    docker rm -f "$redis" > /dev/null 2>&1 || true
    docker rm -f "$fake_api" > /dev/null 2>&1 || true
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
docker run -d --name "$fake_api" --network "$network" --network-alias dragaliaapi \
    -e ASPNETCORE_URLS=http://0.0.0.0:8080 \
    --mount "type=bind,src=$trial_dir/state-settings.json,dst=/app/appsettings.json,readonly" \
    --entrypoint python3 "$checks" /checks/fake_game_api.py > /dev/null
if ! compose up -d --pull never --wait --wait-timeout 90; then
    compose logs --tail 60
    exit 1
fi
container=$(compose ps -q photon)
[ "$(docker inspect --format '{{.Config.User}}' "$container")" = '65532:65532' ]
[ "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' "$container")" = true ]
# Luxon's test peer accepts numeric endpoints; HTTP uses Docker DNS normally.
address=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$container")
if ! docker run --rm --network "$network" \
    -e LUXON_TEST_EXTERNAL_SERVER="$address" -e LUXON_STATE_URL=http://photon-rooms:5089 -e LUXON_TEST_LEASE=1 \
    -e LUXON_TEST_API_URL=http://dragaliaapi:8080 \
    "$checks"; then
    compose logs --tail 100
    docker logs --tail 25 "$fake_api"
    exit 1
fi
python3 - "$container" <<'PY'
import subprocess
import sys

result = subprocess.run(["docker", "logs", sys.argv[1]], capture_output=True, text=True, check=True)
lines = [line for line in (result.stdout + result.stderr).splitlines() if "Co-op trace" in line]
assert lines, "True wind diagnostics were not exercised"
assert all("quest=210010101" in line for line in lines), "Diagnostics escaped the selected quest"
for evidence in (
    "decision=room-update-during-battle",
    "code=0x02 decision=relay",
    "code=0x15 decision=cancel",
    "code=0x15 decision=server-broadcast-submitted",
    "start confirmation repeated",
    "detail limit reached",
    "reason=5s-after-start",
):
    assert any(evidence in line for line in lines), "Missing diagnostic evidence: " + evidence
for private in ("diagnostic-private-value", "diagnostic-private-payload", "diagnostic-private-key"):
    assert all(private not in line for line in lines), "Diagnostics exposed private content"
details = [line for line in lines if "Co-op trace event:" in line
           or "Co-op trace properties:" in line or "Co-op trace stage:" in line]
assert len(details) <= 160, "Diagnostic details exceeded the per-room limit"
print("真风诊断：Ready 重试确认、事件转发、原有拒绝策略、开战后采样及日志内容隔离检查通过")
PY
echo "联机镜像：普通用户、只读文件系统、真实 UDP 房间/加载/战斗消息/结算接口检查通过（游戏 API 为测试替身）"
