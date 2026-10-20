"""在服务器上生成试连配置，不输出公网地址，也不修改 .env。"""

import argparse
import ipaddress
import json
import re
import shlex
import secrets
import sys
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
METADATA_URL = "http://metadata.tencentyun.com/latest/meta-data/public-ipv4"


def public_host(env_file: Path) -> str:
    configured = None
    for line in env_file.read_text().splitlines():
        if line.strip().startswith("PHOTON_PUBLIC_HOST="):
            fields = shlex.split(line.strip().partition("=")[2], comments=True)
            if len(fields) > 1:
                raise ValueError("invalid host")
            configured = fields[0] if fields else ""
            break

    if configured:
        host = configured
        try:
            if ipaddress.ip_address(host).version != 4:
                raise ValueError("IPv4 required")
        except ValueError:
            if len(host) > 253 or not all(
                re.fullmatch(r"[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?", label)
                for label in host.split(".")
            ):
                raise ValueError("invalid host") from None
    else:
        # 元数据是服务器本机的服务，不经公网代理。
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(METADATA_URL, timeout=5) as response:
            host = response.read(256).decode().strip()
        address = ipaddress.ip_address(host)
        if address.version != 4 or not address.is_global:
            raise ValueError("public IPv4 required")
    return host


def prepare(
    mode: str,
    settings: Path,
    previous: Path,
    env_file: Path,
    output: Path,
    image: str,
    rooms: bool = False,
):
    data = json.loads(settings.read_text(encoding="utf-8-sig"))
    options = data.setdefault("PhotonOptions", {})
    if mode == "configure":
        host = public_host(env_file)
        config = (
            (HERE / "config.template.yml")
            .read_text()
            .replace("__PHOTON_PUBLIC_HOST__", host)
        )
        if not re.fullmatch(r"[a-zA-Z0-9][a-zA-Z0-9._/@:-]+", image):
            raise ValueError("invalid image")
        template = "docker-compose.rooms.yml" if rooms else "docker-compose.yml"
        compose = (HERE / template).read_text().replace("__PHOTON_IMAGE__", image)
        output.mkdir(parents=True, exist_ok=True, mode=0o700)
        output.chmod(0o700)
        (output / "config.yml").write_text(config)
        (output / "docker-compose.yml").write_text(compose)
        for name in ("config.yml", "docker-compose.yml"):
            (output / name).chmod(0o644)
        options["ServerUrl"] = f"{host}:5055"
        if rooms:
            old_token = env_file.parent / "photon/photon-token"
            token = (
                old_token.read_text().strip()
                if old_token.is_file()
                else secrets.token_hex(32)
            )
            if not re.fullmatch(r"[0-9a-f]{64}", token):
                raise ValueError("invalid room token")
            (output / "photon-token").write_text(token + "\n")
            (output / "state-settings.json").write_text(
                json.dumps(
                    {
                        "PhotonOptions": {"Token": token},
                        "ConnectionStrings": {"Redis": "redis:6379"},
                    }
                )
                + "\n"
            )
            # Only root can traverse the parent directory; the non-root container can
            # read the individual bind mounts without changing the existing .env.
            for name in ("photon-token", "state-settings.json"):
                (output / name).chmod(0o644)
            options["StateManagerUrl"] = "http://photon-rooms:5089"
    elif mode in ("keep", "restore"):
        old = (
            json.loads(previous.read_text(encoding="utf-8-sig"))
            if previous.is_file()
            else {}
        )
        for key in ("ServerUrl", "StateManagerUrl"):
            if mode == "restore":
                options.pop(key, None)
            value = old.get("PhotonOptions", {}).get(key)
            if value:
                options[key] = value
    elif mode == "disable":
        options.pop("ServerUrl", None)
        options.pop("StateManagerUrl", None)
    if not options:
        data.pop("PhotonOptions", None)
    settings.write_text(json.dumps(data, ensure_ascii=False, indent=4) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--mode", choices=["configure", "keep", "restore", "disable"], required=True
    )
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--previous", type=Path, required=True)
    parser.add_argument("--env-file", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--image", default="")
    parser.add_argument("--rooms", action="store_true")
    args = parser.parse_args()
    try:
        prepare(
            args.mode,
            args.settings,
            args.previous,
            args.env_file,
            args.output,
            args.image,
            args.rooms,
        )
    except Exception:
        # 网络异常和配置异常可能带地址，不把异常或 traceback 发到公开的 Actions 日志。
        print(
            "联机配置未生成；请通过 TAT 检查服务器配置和元数据服务。", file=sys.stderr
        )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
