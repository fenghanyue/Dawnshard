"""Prepare private guest-auth state before switching the game image; never print secrets."""

import argparse
from datetime import datetime, timezone
import json
import ipaddress
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

ROLE = "dawnshard_guest_auth"
DB = "dawnshard_guest_auth"
APP_UID = 1654


def run(args, *, root, input=None, stdout=subprocess.PIPE, timeout=60):
    result = subprocess.run(args, cwd=root, input=input, stdout=stdout,
                            stderr=subprocess.PIPE, timeout=timeout)
    if result.returncode:
        raise RuntimeError("Guest authentication preparation command failed; inspect privately via TAT.")
    return result.stdout or b""


def private_directory(path):
    path.mkdir(parents=True, exist_ok=True, mode=0o700)
    path.chmod(0o700)
    os.chown(path, APP_UID, APP_UID)


def write_private(path, data):
    # Keep the inode of configuration files mounted into running containers.
    with path.open("w") as output:
        output.write(data)
    path.chmod(0o600)
    os.chown(path, APP_UID, APP_UID)


def render_caddyfile(template, host):
    """Public IP certificates require the ACME shortlived profile, unlike DNS names."""
    options = ""
    if host:
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", host):
            raise RuntimeError("Invalid guest authentication HTTPS host.")
        try:
            address = ipaddress.ip_address(host)
        except ValueError:
            if re.fullmatch(r"[0-9.]+", host):
                raise RuntimeError("Invalid guest authentication IP address.") from None
        else:
            if address.version != 4 or not address.is_global:
                raise RuntimeError("Guest authentication requires a public IPv4 address.")
            options = "profile shortlived\n            disable_tlsalpn_challenge"
    return template.replace("# GUEST_AUTH_IP_ACME_OPTIONS", options)


def rollback(root, compose_file, image):
    private = root / "guest-auth/private"
    state_file = private / "last-rollout.json"
    if not state_file.exists():
        return
    state = json.loads(state_file.read_text())
    if state["image"] != image:
        return
    compose = ["docker", "compose", "--project-directory", str(root), "-f", str(compose_file)]
    if state["caddyfile"] is not None:
        (root / "guest-auth/Caddyfile").write_text(state["caddyfile"])
    for name in ["guestauth", "guest-auth-edge"]:
        if name not in state["previous_images"]:
            run(compose + ["--profile", "guest-auth", "stop", name], root=root)
    if state["previous_images"]:
        override = private / "rollback.compose.json"
        write_private(override, json.dumps({"services": {name: {"image": old} for name, old in state["previous_images"].items()}}))
        run(compose + ["-f", str(override), "--profile", "guest-auth", "up", "-d", "--no-deps", *state["previous_images"]], root=root)
    settings = json.loads(state["api"] or "{}")
    if "guest-auth-edge" in state["previous_images"] and settings.get("GuestAuth", {}).get("ClientEnabled"):
        container = run(compose + ["ps", "-q", "guest-auth-edge"], root=root).decode().strip()
        address = run(["docker", "inspect", "--format", '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', container], root=root).decode().strip()
        settings["GuestAuth"]["TrustedProxyAddresses"] = [address]
    write_private(private / "api.json", json.dumps(settings) + "\n")
    print("Previous authentication containers and patch configuration restored; identity data and keys retained.")


def prepare(root, compose_file, image):
    feature = run(["docker", "image", "inspect", "--format",
                   '{{index .Config.Labels "org.dawnshard.guest-auth"}}', image], root=root).decode().strip()
    directory = root / "guest-auth"
    private = directory / "private"
    private.mkdir(parents=True, exist_ok=True, mode=0o700)
    # Compose may resolve env_file even for inactive profiles.
    edge_env = private / "edge.env"
    if not edge_env.exists():
        edge_env.write_text("")
    api_settings = private / "api.json"
    if feature != "1":
        if not api_settings.exists():
            api_settings.write_text("{}\n")
        return

    if os.geteuid() != 0:
        raise RuntimeError("Production authentication preparation must run through TAT as root.")
    private_directory(private)
    edge_env.chmod(0o600)

    compose = ["docker", "compose", "--project-directory", str(root), "-f", str(compose_file)]
    def sql(query):
        return run(compose + ["exec", "-T", "postgres", "sh", "-c",
                    'exec psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -At'],
                   root=root, input=query.encode()).decode().strip()

    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    backup = root / "backups" / ("guest-auth-" + stamp)
    backup.mkdir(parents=True, mode=0o700)
    for name in ["private", "data", "caddy"]:
        source = directory / name
        if source.exists():
            shutil.copytree(source, backup / name)
    previous_images = {}
    for name in ["guestauth", "guest-auth-edge"]:
        container = run(compose + ["ps", "-q", name], root=root).decode().strip()
        if container:
            previous_images[name] = run(["docker", "inspect", "--format", "{{.Image}}", container], root=root).decode().strip()
    caddyfile = directory / "Caddyfile"
    write_private(private / "last-rollout.json", json.dumps({
        "image": image, "previous_images": previous_images,
        "api": api_settings.read_text() if api_settings.exists() else "{}",
        "caddyfile": caddyfile.read_text() if caddyfile.exists() else None,
    }))
    database_exists = sql(f"SELECT 1 FROM pg_database WHERE datname='{DB}';") == "1"
    if database_exists:
        dump = backup / "guest-auth.dump"
        with dump.open("wb") as output:
            run(compose + ["exec", "-T", "postgres", "sh", "-c",
                'exec pg_dump -U "$POSTGRES_USER" -d dawnshard_guest_auth -Fc'], root=root, stdout=output)
        dump.chmod(0o600)
        with dump.open("rb") as source:
            if source.read(5) != b"PGDMP":
                raise RuntimeError("Guest authentication database backup is invalid.")

    private_directory(directory / "data")
    private_directory(directory / "caddy")
    runtime = private / "runtime.json"
    if not runtime.exists():
        if database_exists:
            raise RuntimeError("Restore the existing authentication configuration before deploying.")
        password = secrets.token_urlsafe(48)
        write_private(runtime, json.dumps({"GuestAuth": {
            "ConnectionString": f"Host=postgres;Database={DB};Username={ROLE};Password={password};Maximum Pool Size=10;Timeout=5;Command Timeout=5",
            "DataDirectory": "/data", "Issuer": "DawnshardGuest", "AllowNewGuests": False
        }}) + "\n")
    else:
        config = json.loads(runtime.read_text())["GuestAuth"]
        match = re.search(r"(?:^|;)Password=([A-Za-z0-9_-]+)(?:;|$)", config["ConnectionString"])
        if not match:
            raise RuntimeError("Existing guest authentication configuration is invalid.")
        password = match.group(1)

    if sql(f"SELECT 1 FROM pg_roles WHERE rolname='{ROLE}';") != "1":
        sql(f"CREATE ROLE {ROLE} LOGIN PASSWORD '{password}';")
    if not database_exists:
        sql(f"CREATE DATABASE {DB} OWNER {ROLE};")

    host_values = dict(line.split("=", 1) for line in edge_env.read_text().splitlines()
                       if line and not line.startswith("#") and "=" in line)
    host = host_values.get("GUEST_AUTH_HOST", "")
    source = compose_file.parent / "guest-auth" / "Caddyfile"
    # Preserve the inode of the read-only bind mount used by a running edge container.
    (directory / "Caddyfile").write_text(render_caddyfile(source.read_text(), host))
    run(compose + ["--profile", "guest-auth", "up", "-d", "--no-deps", "--wait", "--wait-timeout", "75", "guestauth"], root=root, timeout=90)

    if not database_exists:
        initial = backup / "initialized"
        initial.mkdir(mode=0o700)
        for name in ["private", "data"]:
            shutil.copytree(directory / name, initial / name)
        with (initial / "guest-auth.dump").open("wb") as output:
            run(compose + ["exec", "-T", "postgres", "sh", "-c",
                'exec pg_dump -U "$POSTGRES_USER" -d dawnshard_guest_auth -Fc'], root=root, stdout=output)
        (initial / "guest-auth.dump").chmod(0o600)

    trusted = []
    if host:
        run(compose + ["--profile", "guest-auth", "up", "-d", "--no-deps", "guest-auth-edge"], root=root)
        container = run(compose + ["ps", "-q", "guest-auth-edge"], root=root).decode().strip()
        address = run(["docker", "inspect", "--format", '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}', container], root=root).decode().strip()
        trusted.append(address)
        # Verify the certificate with the OS trust store before advertising a new patch configuration.
        deadline = time.monotonic() + 240
        while True:
            try:
                with urllib.request.urlopen("https://" + host + "/health", timeout=8) as response:
                    if response.status == 200:
                        break
            except (urllib.error.URLError, TimeoutError):
                pass
            if time.monotonic() >= deadline:
                raise RuntimeError("HTTPS certificate verification did not complete; existing game configuration was not switched.")
            time.sleep(3)

    patch_settings = {"UseUnifiedLogin": not bool(host)}
    production = root / "appsettings.Production.json"
    if production.exists():
        public_host = json.loads(production.read_text()).get("PhotonOptions", {}).get("ServerUrl", "").rsplit(":", 1)[0]
        try:
            address = ipaddress.ip_address(public_host)
            if address.version == 4 and address.is_global:
                patch_settings["CdnUrl"] = "http://" + str(address) + ":8081/"
        except ValueError:
            pass
    write_private(api_settings, json.dumps({
        "GuestAuth": {"Enabled": True, "ClientEnabled": bool(host), "TrustedProxyAddresses": trusted},
        "Dragalipatch": patch_settings,
    }) + "\n")
    print("Guest authentication prepared; HTTPS client entry " + ("enabled." if host else "not activated."))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app-dir", type=Path, required=True)
    parser.add_argument("--compose", type=Path, required=True)
    parser.add_argument("--image", required=True)
    parser.add_argument("--rollback", action="store_true")
    args = parser.parse_args()
    try:
        if args.rollback:
            rollback(args.app_dir, args.compose, args.image)
        else:
            prepare(args.app_dir, args.compose, args.image)
    except Exception:
        if not args.rollback:
            try:
                rollback(args.app_dir, args.compose, args.image)
            except Exception:
                print("Guest authentication rollback also failed; inspect privately via TAT.", file=sys.stderr)
        print("Guest authentication preparation failed; existing game was not switched. Inspect privately via TAT.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
