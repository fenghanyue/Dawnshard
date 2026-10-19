"""离线验证 TAT 工具不会重复提交命令或泄露凭证。"""

import argparse
import base64
import contextlib
import io
import os
import unittest
from unittest.mock import Mock, patch

import tat


class TatTest(unittest.TestCase):
    def setUp(self):
        self.args = argparse.Namespace(
            region="ap-shanghai", instance="lhins-test", timeout=120, wait_seconds=1
        )

    def test_invalid_script_never_reaches_server(self):
        client = Mock()
        for script in (b" \n", b"x" * (tat.MAX_CONTENT_BYTES + 1)):
            with self.assertRaises(ValueError):
                tat.submit(client, self.args, script)
        client.call_json.assert_not_called()

    def test_resume_reads_result_without_resubmitting_or_exposing_secrets(self):
        output = "测试成功\nexample-secret-id example-secret-key".encode() + b"\xff"
        client = Mock()
        client.call_json.return_value = {
            "Response": {
                "InvocationTaskSet": [
                    {
                        "InstanceId": self.args.instance,
                        "TaskStatus": "SUCCESS",
                        "TaskResult": {
                            "ExitCode": 0,
                            "Output": base64.b64encode(output).decode(),
                            "Dropped": 42,
                        },
                    }
                ]
            }
        }
        captured = io.StringIO()
        with (
            patch.dict(os.environ, {
                "TENCENT_INSTANCE_ID": self.args.instance,
                "TENCENTCLOUD_SECRET_ID": "example-secret-id",
                "TENCENTCLOUD_SECRET_KEY": "example-secret-key",
            }),
            patch.object(tat, "create_client", return_value=client),
            contextlib.redirect_stdout(captured),
        ):
            self.assertEqual(tat.main(["--invocation", "inv-existing"]), 0)
        self.assertEqual(client.call_json.call_count, 1)
        self.assertEqual(client.call_json.call_args.args[0], "DescribeInvocationTasks")
        self.assertIn("测试成功", captured.getvalue())
        self.assertIn("42", captured.getvalue())
        self.assertNotIn("example-secret-id", captured.getvalue())
        self.assertNotIn("example-secret-key", captured.getvalue())

    def test_wait_timeout_preserves_invocation_without_cancel_or_resubmit(self):
        client = Mock()
        captured = io.StringIO()
        with patch.object(tat.time, "monotonic", side_effect=[0, 2]), contextlib.redirect_stdout(captured):
            self.assertEqual(tat.wait_for_result(client, "inv-existing", self.args), 2)
        client.call_json.assert_not_called()
        self.assertIn("--invocation inv-existing", captured.getvalue())

    def test_missing_credentials_fail_before_network_without_values(self):
        with patch.dict(os.environ, {"TENCENTCLOUD_SECRET_ID": "", "TENCENTCLOUD_SECRET_KEY": ""}):
            with self.assertRaisesRegex(ValueError, "TENCENTCLOUD_SECRET_ID"):
                tat.create_client("ap-shanghai")

    def test_sdk_receives_environment_ca_and_keeps_tls_verification(self):
        try:
            from tencentcloud.common.common_client import CommonClient
        except ImportError:
            self.skipTest("安装 tools/tencent/requirements.txt 后验证 SDK 的 CA 参数")
        with (
            patch.dict(os.environ, {
                "TENCENTCLOUD_SECRET_ID": "example-secret-id",
                "TENCENTCLOUD_SECRET_KEY": "example-secret-key",
                "REQUESTS_CA_BUNDLE": "/example/environment-ca.pem",
            }),
            patch("tencentcloud.common.common_client.CommonClient") as client,
        ):
            tat.create_client("ap-shanghai")
        profile = client.call_args.kwargs["profile"].httpProfile
        self.assertEqual(profile.certification, "/example/environment-ca.pem")


if __name__ == "__main__":
    unittest.main()
