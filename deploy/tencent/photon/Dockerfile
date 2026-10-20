# syntax=docker/dockerfile:1
FROM busybox:1.37.0-musl@sha256:5cec3fc171c87218698e85a52af7087de727372aae264a787b8112901a5b0092

# v1.5 的静态 Linux x86_64 发布版；校验不通过就停止构建。
ADD --checksum=sha256:012d55eec3001918fe988d9315920470842de6305ed4bdc132fabaf817fe6f96 --chmod=0755 \
    https://github.com/niansa/LuxonServer/releases/download/v1.5/luxon_server.musl-static.release.linux-x86_64 \
    /usr/local/bin/luxon_server

WORKDIR /app
COPY LICENSE.luxon /usr/share/licenses/luxon-server/LICENSE
USER 65532:65532
EXPOSE 5055/udp 5056/udp
HEALTHCHECK --interval=5s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -Y off -q -O /dev/null http://127.0.0.1:5088/ || exit 1
ENTRYPOINT ["/usr/local/bin/luxon_server"]
