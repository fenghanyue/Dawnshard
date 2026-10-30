"""Exercise the built image through a real TLS proxy using isolated synthetic guests."""

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import urllib.parse
import uuid

import msgpack
from prepare import render_caddyfile


def verify_caddy_configuration(image, root):
    """Check both production modes with the Caddy binary in the actual release image."""
    for host, is_ip in [("auth.example.invalid", False), ("8.8.8.8", True)]:
        path = root / "Caddyfile.check"
        path.write_text(render_caddyfile(Path(__file__).with_name("Caddyfile").read_text(), host))
        path.chmod(0o644)
        result = subprocess.run([
            "docker", "run", "--rm", "--network", "none", "-e", "GUEST_AUTH_HOST=" + host,
            "-v", str(path) + ":/etc/caddy/Caddyfile:ro", "--entrypoint", "/usr/bin/caddy", image,
            "adapt", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile",
        ], capture_output=True, check=True, timeout=30)
        config = json.loads(result.stdout)
        policies = config["apps"]["tls"]["automation"]["policies"]
        policy = next(p for p in policies if host in p["subjects"])
        issuer = policy["issuers"][0]
        assert issuer["module"] == "acme"
        assert issuer["ca"] == "https://acme-v02.api.letsencrypt.org/directory"
        assert (issuer.get("profile") == "shortlived") is is_ip
        assert policy["renewal_window_ratio"] == 0.5
        server = config["apps"]["http"]["servers"]["srv0"]
        assert server["tls_connection_policies"][0]["default_sni"] == host
        if is_ip:
            assert issuer["challenges"]["tls-alpn"]["disabled"] is True
    print("Verified: DNS and public-IP production Caddy profiles, automatic renewal, and no-SNI certificate selection.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--work-dir", type=Path, default=Path(os.environ.get("RUNNER_TEMP", "/workspace/work")))
    args = parser.parse_args()
    args.work_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="guest-runtime-", dir=args.work_dir) as temporary:
        root = Path(temporary)
        verify_caddy_configuration(args.image, root)
        uid = os.getuid() or 1654
        gid = os.getgid() or 1654
        for directory in ["auth-data", "edge-data"]:
            (root / directory).mkdir()
            if os.geteuid() == 0:
                os.chown(root / directory, uid, gid)
        (root / "auth.json").write_text(json.dumps({"GuestAuth": {"ConnectionString": "Host=postgres;Database=dawnshard_guest_auth;Username=dawnshard_guest_auth;Password=synthetic-auth-password", "DataDirectory": "/data", "AllowNewGuests": True}}))
        (root / "appsettings.json").write_text("{}")
        (root / "api.json").write_text("{}")
        (root / "init.sql").write_text("CREATE ROLE dawnshard_guest_auth LOGIN PASSWORD 'synthetic-auth-password';\nCREATE DATABASE dawnshard_guest_auth OWNER dawnshard_guest_auth;\n")
        caddy = Path(__file__).with_name("Caddyfile").read_text()
        caddy = caddy.replace("    admin off", "    admin off\n    skip_install_trust")
        caddy = re.sub(r"^    tls \{.*?^    \}", "    tls internal", caddy, flags=re.MULTILINE | re.DOTALL)
        (root / "Caddyfile").write_text(caddy)
        for path in root.iterdir():
            if path.is_file():
                path.chmod(0o644)  # Synthetic configuration must be readable by container users.
        configuration = {
            "name": "guest-runtime-" + uuid.uuid4().hex[:10],
            "services": {
                "postgres": {"image": "postgres:17", "environment": {"POSTGRES_USER": "testing", "POSTGRES_PASSWORD": "synthetic-game-password", "POSTGRES_DB": "game"}, "volumes": [str(root / "init.sql") + ":/docker-entrypoint-initdb.d/auth.sql:ro"], "healthcheck": {"test": ["CMD-SHELL", "pg_isready -U testing -d game"], "interval": "1s", "timeout": "3s", "retries": 45}},
                "redis": {"image": "redis:8"},
                "guestauth": {"image": args.image, "user": f"{uid}:{gid}", "entrypoint": ["dotnet", "/guestauth/GuestAuth.dll"], "environment": {"ASPNETCORE_URLS": "http://+:8080", "GuestAuthRuntimeFile": "/run/auth.json"}, "volumes": [str(root / "auth.json") + ":/run/auth.json:ro", str(root / "auth-data") + ":/data"], "read_only": True, "tmpfs": ["/tmp"], "cap_drop": ["ALL"]},
                "edge": {"image": args.image, "user": f"{uid}:{gid}", "entrypoint": ["/usr/bin/caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"], "environment": {"GUEST_AUTH_HOST": "auth.example.invalid", "XDG_CONFIG_HOME": "/config", "XDG_DATA_HOME": "/data", "NO_PROXY": "dragaliaapi,guestauth,localhost,127.0.0.1,::1", "no_proxy": "dragaliaapi,guestauth,localhost,127.0.0.1,::1"}, "volumes": [str(root / "Caddyfile") + ":/etc/caddy/Caddyfile:ro", str(root / "edge-data") + ":/data"], "read_only": True, "tmpfs": ["/tmp", "/config"], "cap_drop": ["ALL"], "ports": ["127.0.0.1::8443"]},
                "dragaliaapi": {"image": args.image, "environment": {"ConnectionStrings__Postgres": "Host=postgres;Database=game;Username=testing;Password=synthetic-game-password", "ConnectionStrings__Redis": "redis", "HangfireOptions__Enabled": "false"}, "volumes": [str(root / "appsettings.json") + ":/app/appsettings.Production.json:ro", str(root / "api.json") + ":/app/guestauth-api.runtime.json:ro"]},
            },
        }
        compose_file = root / "compose.json"
        compose_file.write_text(json.dumps(configuration))
        command = ["docker", "compose", "-f", str(compose_file)]
        def run(argv, *, input=None, allowed_failure=False):
            result = subprocess.run(argv, input=input, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120)
            if result.returncode and not allowed_failure:
                raise RuntimeError("Container verification command failed: " + result.stderr.decode(errors="replace")[:1500])
            return result
        try:
            run(command + ["up", "-d", "--wait", "postgres", "redis"])
            run(command + ["up", "-d", "guestauth", "edge"])
            edge = run(command + ["ps", "-q", "edge"]).stdout.decode().strip()
            address = run(["docker", "inspect", "--format", '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', edge]).stdout.decode().strip()
            (root / "api.json").write_text(json.dumps({"GuestAuth": {"Enabled": True, "ClientEnabled": True, "TrustedProxyAddresses": [address]}, "Dragalipatch": {"UseUnifiedLogin": False}}))
            run(command + ["up", "-d", "dragaliaapi"])
            binding = run(command + ["port", "edge", "8443"]).stdout.decode().strip()
            port = binding.rsplit(":", 1)[1]
            origin = "https://auth.example.invalid:" + port
            ca = root / "edge-data/caddy/pki/authorities/local/root.crt"
            def request(path, value=None, headers=None, allowed_failure=False):
                argv = ["curl", "--silent", "--show-error", "--fail-with-body", "--max-time", "20", "--noproxy", "auth.example.invalid", "--resolve", "auth.example.invalid:" + port + ":127.0.0.1", "--cacert", str(ca)]
                if value is not None:
                    content_type = "application/octet-stream" if isinstance(value, bytes) else "application/json"
                    argv += ["-X", "POST", "-H", "Content-Type: " + content_type, "--data-binary", "@-"]
                # Only synthetic, isolated test credentials are used by this verifier.
                for name, content in (headers or {}).items():
                    argv += ["-H", name + ": " + content]
                argv += [origin + path]
                payload = value if isinstance(value, bytes) else json.dumps(value).encode() if value is not None else None
                return run(argv, input=payload, allowed_failure=allowed_failure)
            deadline = time.monotonic() + args.timeout
            last_error = "CA certificate was not created"
            while True:
                try:
                    if ca.exists() and json.loads(request("/health").stdout)["status"] == "Healthy":
                        break
                except (RuntimeError, json.JSONDecodeError, KeyError) as error:
                    last_error = str(error)
                if time.monotonic() > deadline:
                    raise RuntimeError("TLS proxy and game health did not become ready: " + last_error)
                time.sleep(1)
            assert json.loads(request("/dragalipatch/config").stdout)["useUnifiedLogin"] is False
            first = json.loads(request("/core/v1/gateway/sdk/login", {}).stdout)
            credential = first["createdDeviceAccount"]
            subject = first["user"]["id"]
            assert first["capability"]["accountHost"] == "auth.example.invalid:" + port
            def authenticate(token):
                response = request("/2.19.0_20220714193707/tool/auth", msgpack.packb({}), {"ID-TOKEN": token, "Platform": "2", "Res-Ver": "y2XM6giU6zz56wCm", "Accept": "application/x-msgpack"})
                body = msgpack.unpackb(response.stdout, raw=False)
                assert body["data_headers"]["result_code"] == 1
                return body["data"]["viewer_id"]
            viewer = authenticate(first["idToken"])
            auth_settings = json.loads((root / "auth.json").read_text())
            auth_settings["GuestAuth"]["AllowNewGuests"] = False
            (root / "auth.json").write_text(json.dumps(auth_settings))
            run(command + ["restart", "guestauth"])
            deadline = time.monotonic() + 30
            while True:
                try:
                    second = json.loads(request("/core/v1/gateway/sdk/login", {"deviceAccount": credential, "previousUserId": subject}).stdout)
                    break
                except RuntimeError:
                    if time.monotonic() > deadline:
                        raise
                    time.sleep(1)
            assert second["user"]["id"] == subject
            assert authenticate(second["idToken"]) == viewer
            rejected = request("/core/v1/gateway/sdk/login", {}, allowed_failure=True)
            assert rejected.returncode != 0
            assert json.loads(rejected.stdout)["error"] == "A preserved guest identity is required."
            players = run(command + ["exec", "-T", "postgres", "psql", "-U", "testing", "-d", "game", "-Atc", 'SELECT COUNT(*) FROM "Players";']).stdout.strip()
            assert players == b"1"
            forbidden = run(command + ["exec", "-T", "-e", "PGPASSWORD=synthetic-auth-password", "postgres", "psql", "-h", "127.0.0.1", "-U", "dawnshard_guest_auth", "-d", "game", "-c", 'SELECT COUNT(*) FROM "Players";'], allowed_failure=True)
            assert forbidden.returncode != 0
            print("Verified: real TLS gateway, guest SDK login, private key discovery, game authentication, persistent identity after restart, unchanged player ID, no duplicate save, and database separation.")
        except Exception:
            diagnostic = run(command + ["logs", "--no-color", "--tail", "35", "edge", "guestauth", "dragaliaapi"], allowed_failure=True)
            log = args.work_dir / "guest-runtime-failure.log"
            log.write_bytes(diagnostic.stdout + diagnostic.stderr)
            log.chmod(0o600)
            raise
        finally:
            run(command + ["down", "--volumes", "--remove-orphans"], allowed_failure=True)


if __name__ == "__main__":
    main()
