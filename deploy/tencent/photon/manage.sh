#!/usr/bin/env bash
# 由 deploy.sh 解包后 source。详细输出只留在服务器，不能打印到 Actions。
new_dir="${new_dir:?deploy.sh 必须设置 new_dir}"
PHOTON_DIR="$APP_DIR/photon"
photon_ready=0

photon_compose() {
    docker compose --project-directory "$PHOTON_DIR" -f "$PHOTON_DIR/docker-compose.yml" "$@"
}

photon_settings() {
    local room_args=()
    [ "${PHOTON_ROOMS:-0}" != 1 ] || room_args+=(--rooms)
    python3 "$new_dir/photon/prepare.py" --mode "$1" \
        --settings "$new_dir/appsettings.Production.json" --previous "$APP_DIR/appsettings.Production.json" \
        --env-file "$APP_DIR/.env" --output "$new_dir/photon-runtime" --image "${PHOTON_IMAGE:-}" "${room_args[@]}"
}

prepare_photon() {
    if [ "${DISABLE_PHOTON:-0}" = 1 ]; then
        photon_settings disable
        echo "本次部署将停用联机试连"
        return
    fi
    if [ -n "${PHOTON_IMAGE:-}" ] && [ -n "${PHOTON_DIGEST:-}" ]; then
        # 直连也按 digest 拉，最后再打上本次 tag；不改变游戏镜像变量。
        if (
            if ! have_image "$PHOTON_IMAGE" "$PHOTON_DIGEST" && ! pull_from_mirror "$PHOTON_IMAGE" "$PHOTON_DIGEST"; then
                ref="${PHOTON_IMAGE%:*}@$PHOTON_DIGEST"
                pull_from_registry "$ref" && docker tag "$ref" "$PHOTON_IMAGE"
            fi
        ) > "$new_dir/photon/prepare.log" 2>&1 && photon_settings configure; then
            photon_ready=1
            echo "联机试连镜像和配置已准备好（地址不公开）"
            return
        fi
        if mkdir -p "$PHOTON_DIR" && chmod 700 "$PHOTON_DIR"; then
            cp "$new_dir/photon/prepare.log" "$PHOTON_DIR/prepare.log" || true
            chmod 600 "$PHOTON_DIR/prepare.log" || true
        fi
        echo "!! 联机试连准备失败，保留原联机地址，继续部署游戏服务器" >&2
    else
        echo "本次不更新联机试连（手动回滚游戏镜像时保留原配置）"
    fi
    photon_settings keep
}

start_photon() {
    if [ "${DISABLE_PHOTON:-0}" = 1 ]; then
        if [ -f "$PHOTON_DIR/docker-compose.yml" ]; then
            if ! photon_compose down > "$PHOTON_DIR/deploy.log" 2>&1; then
                echo "!! 联机试连停止失败；游戏服务器已部署，请通过 TAT 查看" >&2
                return 1
            fi
        fi
        echo "联机试连已停用"
        return
    fi
    [ "$photon_ready" = 1 ] || return 0
    if ! mkdir -p "$PHOTON_DIR" || ! chmod 700 "$PHOTON_DIR"; then
        echo "!! 无法准备联机目录；游戏服务器已部署" >&2
        return 1
    fi
    local previous
    previous=$(mktemp -d "$PHOTON_DIR/.previous.XXXXXX") || return 1
    local f
    for f in config.yml docker-compose.yml state-settings.json photon-token; do
        if [ -f "$PHOTON_DIR/$f" ]; then
            if ! cp "$PHOTON_DIR/$f" "$previous/$f"; then
                rm -rf "$previous"
                echo "!! 无法备份联机配置，保留原服务；游戏服务器已部署" >&2
                return 1
            fi
        fi
    done
    local copied=1
    for f in config.yml docker-compose.yml state-settings.json photon-token; do
        if [ -f "$new_dir/photon-runtime/$f" ]; then
            cp "$new_dir/photon-runtime/$f" "$PHOTON_DIR/$f" && chmod 644 "$PHOTON_DIR/$f" || copied=0
        fi
    done
    if [ "$copied" = 1 ] && (
        photon_compose config --quiet && photon_compose up -d --force-recreate --pull never --wait --wait-timeout 40
    ) > "$PHOTON_DIR/deploy.log" 2>&1; then
        rm -rf "$previous"
        echo "联机服务已启动，容器健康检查通过；仍需手机验证准备界面"
        return
    fi
    # 联机单独回退，不回滚已经健康的游戏服务器。
    if [ -f "$previous/docker-compose.yml" ]; then
        for f in config.yml docker-compose.yml state-settings.json photon-token; do
            if [ -f "$previous/$f" ]; then
                cp "$previous/$f" "$PHOTON_DIR/$f"
            else
                rm -f "$PHOTON_DIR/$f"
            fi
        done
        photon_compose up -d --force-recreate --pull never >> "$PHOTON_DIR/deploy.log" 2>&1 || true
    else
        photon_compose down >> "$PHOTON_DIR/deploy.log" 2>&1 || true
    fi
    rm -rf "$previous"
    echo "!! 联机试连启动失败；游戏服务器已部署，请通过 TAT 查看联机日志" >&2
    return 1
}
