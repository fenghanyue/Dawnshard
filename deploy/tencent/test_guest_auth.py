"""Exercise deployment failure boundaries without reaching production or printing credentials."""

from contextlib import redirect_stdout
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import urllib.error

SOURCE = Path(__file__).parent / "guest-auth/prepare.py"
SPEC = importlib.util.spec_from_file_location("guest_auth_prepare", SOURCE)
prepare = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(prepare)


class Runtime:
    def __init__(self, feature="1", existing=False, fail_health=False):
        self.feature = feature
        self.database = existing
        self.role = existing
        self.existing = existing
        self.services = {"guestauth", "guest-auth-edge"} if existing else set()
        self.fail_health = fail_health
        self.calls = []

    def run(self, args, *, root, input=None, stdout=subprocess.PIPE, timeout=60):
        self.calls.append(args)
        if args[:3] == ["docker", "image", "inspect"]:
            return self.feature.encode()
        if input:
            query = input.decode()
            if "FROM pg_database" in query:
                return b"1" if self.database else b""
            if "FROM pg_roles" in query:
                return b"1" if self.role else b""
            if "CREATE ROLE" in query:
                self.role = True
            if "CREATE DATABASE" in query:
                self.database = True
            return b""
        if any("pg_dump" in arg for arg in args):
            stdout.write(b"PGDMP-test-backup")
        if "--wait" in args and self.fail_health:
            raise RuntimeError("Synthetic health failure")
        if "up" in args:
            self.services.update(name for name in ["guestauth", "guest-auth-edge"] if name in args)
        if "stop" in args:
            self.services.difference_update(name for name in ["guestauth", "guest-auth-edge"] if name in args)
        if "ps" in args:
            return b"edge-container" if args[-1] in self.services else b""
        if args[:2] == ["docker", "inspect"]:
            return b"sha256:previous" if "{{.Image}}" in args else b"172.20.0.2"
        return b""


class GuestDeploymentTest(unittest.TestCase):
    def fixture(self, *, host=False, existing=False):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name) / "app"
        private = root / "guest-auth/private"
        private.mkdir(parents=True)
        (private / "edge.env").write_text("GUEST_AUTH_HOST=private-sentinel.example\n" if host else "")
        (private / "api.json").write_text('{"before":true}\n')
        stage = Path(temporary.name) / "stage"
        (stage / "guest-auth").mkdir(parents=True)
        (stage / "guest-auth/Caddyfile").write_text("configuration-placeholder")
        compose = stage / "docker-compose.yml"
        compose.write_text("compose-placeholder")
        if existing:
            (private / "runtime.json").write_text(json.dumps({"GuestAuth": {"ConnectionString": "Host=postgres;Database=dawnshard_guest_auth;Password=private-sentinel-secret;"}}))
            (root / "guest-auth/data").mkdir()
            (root / "guest-auth/data/signing-key.pem").write_text("private-sentinel-key")
        return root, compose

    def invoke(self, runtime, root, compose, *, healthy=True):
        response = mock.MagicMock()
        response.__enter__.return_value.status = 200
        with mock.patch.object(prepare, "run", side_effect=runtime.run), \
             mock.patch.object(prepare.os, "geteuid", return_value=0), \
             mock.patch.object(prepare.os, "chown"), \
             mock.patch.object(prepare.urllib.request, "urlopen", return_value=response) as probe, \
             redirect_stdout(io.StringIO()) as output:
            if healthy:
                prepare.prepare(root, compose, "image:test")
            else:
                probe.side_effect = urllib.error.URLError("unavailable")
                with mock.patch.object(prepare.time, "monotonic", side_effect=[0, 241]):
                    with self.assertRaises(RuntimeError):
                        prepare.prepare(root, compose, "image:test")
        return output.getvalue()

    def test_no_tls_host_keeps_public_login_and_starts_only_internal_auth(self):
        root, compose = self.fixture()
        runtime = Runtime()
        output = self.invoke(runtime, root, compose)
        settings = json.loads((root / "guest-auth/private/api.json").read_text())
        self.assertTrue(settings["GuestAuth"]["Enabled"])
        self.assertFalse(settings["GuestAuth"]["ClientEnabled"])
        self.assertTrue(settings["Dragalipatch"]["UseUnifiedLogin"])
        self.assertFalse(any("up" in args and "guest-auth-edge" in args for args in runtime.calls))
        self.assertNotIn("Password=", output)

    def test_validated_tls_enables_local_login_and_pins_the_actual_proxy(self):
        root, compose = self.fixture(host=True)
        self.invoke(Runtime(), root, compose)
        settings = json.loads((root / "guest-auth/private/api.json").read_text())
        self.assertFalse(settings["Dragalipatch"]["UseUnifiedLogin"])
        self.assertEqual(settings["GuestAuth"]["TrustedProxyAddresses"], ["172.20.0.2"])

    def test_failed_certificate_preserves_previous_patch_and_game_configuration(self):
        root, compose = self.fixture(host=True)
        before = (root / "guest-auth/private/api.json").read_bytes()
        output = self.invoke(Runtime(), root, compose, healthy=False)
        self.assertEqual((root / "guest-auth/private/api.json").read_bytes(), before)
        self.assertNotIn("private-sentinel", output)

    def test_failed_auth_health_never_starts_tls_or_overwrites_previous_configuration(self):
        root, compose = self.fixture(host=True)
        runtime = Runtime(fail_health=True)
        with self.assertRaises(RuntimeError):
            self.invoke(runtime, root, compose)
        self.assertEqual((root / "guest-auth/private/api.json").read_text(), '{"before":true}\n')
        self.assertFalse(any("up" in args and "guest-auth-edge" in args for args in runtime.calls))

    def test_redeployment_preserves_credentials_key_and_database_and_backs_them_up(self):
        root, compose = self.fixture(existing=True)
        config = root / "guest-auth/private/runtime.json"
        key = root / "guest-auth/data/signing-key.pem"
        before = config.read_bytes()
        output = self.invoke(Runtime(existing=True), root, compose)
        self.assertEqual(config.read_bytes(), before)
        self.assertEqual(key.read_text(), "private-sentinel-key")
        snapshots = list((root / "backups").iterdir())
        self.assertEqual(len(snapshots), 1)
        self.assertEqual((snapshots[0] / "private/runtime.json").read_bytes(), before)
        self.assertTrue((snapshots[0] / "guest-auth.dump").read_bytes().startswith(b"PGDMP"))
        self.assertEqual(snapshots[0].stat().st_mode & 0o777, 0o700)
        self.assertNotIn("private-sentinel", output)

    def test_legacy_image_does_not_change_existing_authentication_state(self):
        root, compose = self.fixture(existing=True)
        runtime = Runtime(feature="")
        self.invoke(runtime, root, compose)
        self.assertEqual((root / "guest-auth/private/api.json").read_text(), '{"before":true}\n')
        self.assertEqual(len(runtime.calls), 1)

    def test_rollback_restores_previous_images_and_keeps_identity_keys(self):
        root, compose = self.fixture(host=True, existing=True)
        runtime = Runtime(existing=True)
        with mock.patch.object(prepare, "run", side_effect=runtime.run), \
             mock.patch.object(prepare.os, "geteuid", return_value=0), \
             mock.patch.object(prepare.os, "chown"), \
             mock.patch.object(prepare.urllib.request, "urlopen") as probe:
            probe.return_value.__enter__.return_value.status = 200
            prepare.prepare(root, compose, "image:new")
            prepare.rollback(root, compose, "image:new")
        self.assertEqual(json.loads((root / "guest-auth/private/api.json").read_text()), {"before": True})
        override = json.loads((root / "guest-auth/private/rollback.compose.json").read_text())
        self.assertEqual(set(override["services"]), {"guestauth", "guest-auth-edge"})
        self.assertTrue(all(value["image"] == "sha256:previous" for value in override["services"].values()))
        self.assertEqual((root / "guest-auth/data/signing-key.pem").read_text(), "private-sentinel-key")

    def test_compressed_transport_preserves_literal_shell_input_output_and_exit_status(self):
        import base64
        import tat_run

        script = "cat > /dev/null\nprintf '%s\\n' 'quoted $value `literal`'\nexit 7\n"
        bootstrap = base64.b64decode(tat_run.encode_script(script))
        self.assertNotIn(b"quoted $value", bootstrap)
        result = subprocess.run(["bash"], input=bootstrap, capture_output=True)
        self.assertEqual(result.returncode, 7)
        self.assertEqual(result.stdout, b"quoted $value `literal`\n")
