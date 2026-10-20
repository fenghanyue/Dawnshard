"""用假的 Docker 和健康接口跑部署脚本，验证线上失败分支，不连接真实服务器。"""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import tat_run

HERE = Path(__file__).resolve().parent


class DeployFlowTest(unittest.TestCase):
    def run_deploy(self, *, game_healthy=True, photon_healthy=True):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        app = root / "app"
        app.mkdir()
        (app / ".env").write_text(
            "POSTGRES_USER=test\nPOSTGRES_DB=test\nPHOTON_PUBLIC_HOST=private-sentinel.example\n"
        )
        old_settings = '{"PhotonOptions":{"ServerUrl":"previous.example:5055"}}'
        (app / "appsettings.Production.json").write_text(old_settings)
        (app / "docker-compose.yml").write_text("previous compose")
        (app / "bannerConfig.json").write_text("{}")
        assets = root / "assets"
        assets.mkdir()
        for name in ("default.conf", "docker-compose.yml"):
            (assets / name).write_bytes((HERE / "asset-cache" / name).read_bytes())
        venv = app / "client-master/venv"
        (venv / "bin").mkdir(parents=True)
        import hashlib

        requirements = b"".join(
            (HERE / "client-master" / name).read_bytes()
            for name in ("requirements-build.txt", "requirements.txt")
        )
        (venv / ".requirements").write_text(hashlib.sha256(requirements).hexdigest())
        binary = venv / "bin/python"
        binary.write_text("#!/bin/sh\nprintf 'abcdefghijklmnop\\n'\n")
        binary.chmod(0o755)
        fake_bin = root / "bin"
        fake_bin.mkdir()
        fake_docker = fake_bin / "docker"
        fake_docker.write_text(
            """#!/usr/bin/env python3
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
with open(os.environ['MOCK_CALLS'],'a') as f:f.write(json.dumps(args)+'\\n')
if args[:2]==['image','inspect']:
 print('ghcr.io/example/api@sha256:'+'a'*64+'\\nghcr.io/example/photon@sha256:'+'b'*64)
elif args[0]=='create':print('temp-container')
elif args[0]=='cp':Path(args[-1]).write_text('{}')
elif 'pg_dump' in args:print('fake database dump')
elif args[0]=='compose' and '--project-directory' in args and 'photon' in args[args.index('--project-directory')+1]:
 if 'up' in args and '--wait' in args and os.environ['MOCK_PHOTON_HEALTHY']=='0':
  print('private-sentinel.example',file=sys.stderr)
  sys.exit(1)
"""
        )
        fake_docker.chmod(0o755)
        curl = fake_bin / "curl"
        curl.write_text(
            "#!/bin/sh\nprintf '%s\\n' '{\"status\":\""
            + ("Healthy" if game_healthy else "Unhealthy")
            + "\"}'\n"
        )
        curl.chmod(0o755)
        sleep = fake_bin / "sleep"
        sleep.write_text("#!/bin/sh\nexit 0\n")
        sleep.chmod(0o755)
        env = os.environ.copy()
        env.update(
            {
                "PATH": str(fake_bin) + os.pathsep + env["PATH"],
                "APP_DIR": str(app),
                "ASSET_CACHE_DIR": str(assets),
                "MOCK_CALLS": str(root / "calls"),
                "MOCK_PHOTON_HEALTHY": "1" if photon_healthy else "0",
            }
        )
        script = tat_run.build_script(
            "ghcr.io/example/api:test",
            "sha256:" + "a" * 64,
            "",
            "",
            "ghcr.io/example/photon:test",
            "sha256:" + "b" * 64,
            photon_rooms=True,
        )
        result = subprocess.run(
            ["bash"], input=script, env=env, capture_output=True, text=True, timeout=15
        )
        calls = [json.loads(line) for line in (root / "calls").read_text().splitlines()]
        return result, app, calls, old_settings

    def test_game_health_failure_restores_settings_and_never_starts_photon(self):
        result, app, calls, old_settings = self.run_deploy(game_healthy=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(
            (app / "appsettings.Production.json").read_text(), old_settings
        )
        self.assertEqual((app / "docker-compose.yml").read_text(), "previous compose")
        self.assertFalse(any("--wait" in call for call in calls))
        self.assertNotIn("private-sentinel", result.stdout + result.stderr)
        self.assertFalse(any("logs" in call for call in calls))

    def test_photon_failure_restores_urls_and_stops_failed_first_container(self):
        result, app, calls, _ = self.run_deploy(photon_healthy=False)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(
            json.loads((app / "appsettings.Production.json").read_text())[
                "PhotonOptions"
            ]["ServerUrl"],
            "previous.example:5055",
        )
        self.assertNotIn(
            "StateManagerUrl",
            json.loads((app / "appsettings.Production.json").read_text())[
                "PhotonOptions"
            ],
        )
        game_starts = [
            i for i, call in enumerate(calls) if "up" in call and "dragaliaapi" in call
        ]
        photon_starts = [
            i for i, call in enumerate(calls) if "up" in call and "--wait" in call
        ]
        self.assertEqual(len(game_starts), 2)
        self.assertLess(game_starts[0], photon_starts[0])
        self.assertTrue(any("down" in call for call in calls))
        self.assertNotIn("private-sentinel", result.stdout + result.stderr)
        self.assertIn("private-sentinel", (app / "photon/deploy.log").read_text())

    def test_room_deployment_connects_private_list_service_after_game_is_healthy(self):
        result, app, calls, _ = self.run_deploy()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        options = json.loads((app / "appsettings.Production.json").read_text())[
            "PhotonOptions"
        ]
        self.assertEqual(options["StateManagerUrl"], "http://photon-rooms:5089")
        state = json.loads((app / "photon/state-settings.json").read_text())
        token = (app / "photon/photon-token").read_text().strip()
        self.assertEqual(state["PhotonOptions"]["Token"], token)
        self.assertEqual(len(token), 64)
        self.assertNotIn(
            token, result.stdout + result.stderr + (app / ".env").read_text()
        )
        self.assertEqual((app / "photon").stat().st_mode & 0o777, 0o700)
        game_start = next(
            i for i, call in enumerate(calls) if "up" in call and "dragaliaapi" in call
        )
        room_start = next(
            i for i, call in enumerate(calls) if "up" in call and "--wait" in call
        )
        self.assertLess(game_start, room_start)


if __name__ == "__main__":
    unittest.main()
