"""联机试连部署的离线检查：地址不泄露、失败不切换、回滚保留配置。"""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shlex
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import Mock, patch

import tat_run

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location(
    "photon_prepare", HERE / "photon/prepare.py"
)
prepare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(prepare)


class PhotonConfigTest(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.settings = self.root / "settings.json"
        self.settings.write_text('{"EventOptions":{"EventList":[{"Id":20401}]}}')
        self.previous = self.root / "previous.json"
        self.env_file = self.root / ".env"
        self.env_file.write_text("POSTGRES_PASSWORD=secret-sentinel\n")
        self.output = self.root / "runtime"

    def run_prepare(self, mode="configure", image="ghcr.io/example/photon:test"):
        prepare.prepare(
            mode, self.settings, self.previous, self.env_file, self.output, image
        )
        return json.loads(self.settings.read_text())

    def test_configured_hostname_is_used_without_network_or_changing_env(self):
        self.env_file.write_text(
            "PHOTON_PUBLIC_HOST='private-sentinel.example' # comment\n"
        )
        before = self.env_file.read_bytes()
        with patch.object(prepare.urllib.request, "build_opener") as network:
            result = self.run_prepare()
        network.assert_not_called()
        self.assertEqual(
            result["PhotonOptions"]["ServerUrl"], "private-sentinel.example:5055"
        )
        self.assertEqual(result["EventOptions"]["EventList"][0]["Id"], 20401)
        self.assertIn(
            '"private-sentinel.example:5056"', (self.output / "config.yml").read_text()
        )
        self.assertNotIn(
            "__PHOTON_IMAGE__", (self.output / "docker-compose.yml").read_text()
        )
        self.assertEqual(self.env_file.read_bytes(), before)

    def test_metadata_requires_public_ipv4(self):
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        opener = Mock()
        opener.open.return_value = response
        with patch.object(prepare.urllib.request, "build_opener", return_value=opener):
            response.read.return_value = b"8.8.8.8\n"
            self.assertEqual(
                self.run_prepare()["PhotonOptions"]["ServerUrl"], "8.8.8.8:5055"
            )
            before = self.settings.read_bytes()
            response.read.return_value = b"127.0.0.1"
            with self.assertRaises(ValueError):
                self.run_prepare()
        self.assertEqual(self.settings.read_bytes(), before)
        opener.open.assert_called_with(prepare.METADATA_URL, timeout=5)

    def test_unavailable_metadata_does_not_change_api_settings(self):
        before = self.settings.read_bytes()
        opener = Mock()
        opener.open.side_effect = OSError("private-sentinel.example unavailable")
        with patch.object(prepare.urllib.request, "build_opener", return_value=opener):
            with self.assertRaises(OSError):
                self.run_prepare()
        self.assertEqual(self.settings.read_bytes(), before)

    def test_invalid_host_cli_does_not_print_private_address(self):
        self.env_file.write_text(
            "PHOTON_PUBLIC_HOST=https://private-sentinel.example:5055\n"
        )
        argv = [
            "prepare.py",
            "--mode",
            "configure",
            "--settings",
            str(self.settings),
            "--previous",
            str(self.previous),
            "--env-file",
            str(self.env_file),
            "--output",
            str(self.output),
            "--image",
            "ghcr.io/example/photon:test",
        ]
        captured = io.StringIO()
        with patch("sys.argv", argv), contextlib.redirect_stderr(captured):
            self.assertEqual(prepare.main(), 1)
        self.assertNotIn("private-sentinel", captured.getvalue())
        self.assertNotIn("Traceback", captured.getvalue())

    def test_manual_game_rollback_keeps_server_url_without_copying_other_old_settings(
        self,
    ):
        self.previous.write_text(
            json.dumps(
                {
                    "PhotonOptions": {
                        "ServerUrl": "old.example:5055",
                        "Token": "secret",
                    },
                    "Old": True,
                }
            )
        )
        result = self.run_prepare("keep")
        self.assertEqual(result["PhotonOptions"], {"ServerUrl": "old.example:5055"})
        self.assertNotIn("Old", result)
        self.assertFalse(self.output.exists())

    def test_disable_removes_override_without_changing_env(self):
        self.settings.write_text(
            '{"PhotonOptions":{"ServerUrl":"old.example:5055","Token":"secret"}}'
        )
        before = self.env_file.read_bytes()
        self.assertEqual(
            self.run_prepare("disable")["PhotonOptions"], {"Token": "secret"}
        )
        self.assertEqual(self.env_file.read_bytes(), before)


class PhotonDeploymentTest(unittest.TestCase):
    def test_bundle_fits_tat_limit_and_contains_runtime_templates_without_env(self):
        script = tat_run.build_script(
            "ghcr.io/example/api:test",
            "sha256:" + "a" * 64,
            "example",
            "token" * 64,
            "ghcr.io/example/photon:test",
            "sha256:" + "b" * 64,
        )
        self.assertLess(len(tat_run.b64(script.encode())), tat_run.MAX_CONTENT_BYTES)
        with tarfile.open(
            fileobj=io.BytesIO(tat_run.build_bundle()), mode="r:gz"
        ) as bundle:
            names = bundle.getnames()
        self.assertIn("photon/config.template.yml", names)
        self.assertIn("photon/manage.sh", names)
        self.assertNotIn(".env", names)
        self.assertNotIn("photon/config.yml", names)

    def test_cli_rejects_partial_photon_arguments_before_network(self):
        for options in (
            ["--photon-image", "ghcr.io/example/photon:test"],
            ["--photon-digest", "sha256:" + "a" * 64],
            [
                "--disable-photon",
                "--photon-image",
                "ghcr.io/example/photon:test",
                "--photon-digest",
                "sha256:" + "a" * 64,
            ],
        ):
            with patch(
                "sys.argv", ["tat_run.py", "--image", "example:test", *options]
            ), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit):
                    tat_run.main()

    def test_print_script_redacts_registry_token(self):
        captured = io.StringIO()
        with patch(
            "sys.argv", ["tat_run.py", "--image", "example:test", "--print-script"]
        ), patch.dict(
            os.environ, {"GHCR_TOKEN": "private-token-sentinel"}
        ), contextlib.redirect_stdout(
            captured
        ):
            self.assertEqual(tat_run.main(), 0)
        self.assertNotIn("private-token-sentinel", captured.getvalue())
        self.assertIn("export PHOTON_IMAGE=", captured.getvalue())

    def test_failed_photon_update_restores_existing_service_without_public_logs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            live = root / "photon"
            live.mkdir()
            staged = root / "staged/photon-runtime"
            staged.mkdir(parents=True)
            for name in ("config.yml", "docker-compose.yml"):
                (live / name).write_text("old-private-sentinel")
                (staged / name).write_text("new-private-sentinel")
            calls = root / "calls"
            script = f"""
set -euo pipefail
APP_DIR={shlex.quote(str(root))}
new_dir={shlex.quote(str(root / "staged"))}
source {shlex.quote(str(HERE / "photon/manage.sh"))}
photon_ready=1
photon_compose() {{
    echo "$*" >> {shlex.quote(str(calls))}
    echo 'private-sentinel.example'
    [[ "$*" != *--wait* ]]
}}
start_photon || true
echo 'GAME_STILL_HEALTHY'
"""
            result = subprocess.run(
                ["bash", "-c", script], capture_output=True, text=True, check=True
            )
            self.assertIn("GAME_STILL_HEALTHY", result.stdout)
            self.assertNotIn("private-sentinel", result.stdout + result.stderr)
            self.assertEqual((live / "config.yml").read_text(), "old-private-sentinel")
            self.assertIn("--force-recreate --pull never\n", calls.read_text())


if __name__ == "__main__":
    unittest.main()
