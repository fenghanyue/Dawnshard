"""通过腾讯云自动化助手执行服务器命令；也可用执行编号继续查询结果。"""

import argparse
import base64
import os
import shlex
import ssl
import sys
import time
from pathlib import Path

PENDING_STATES = {"PENDING", "DELIVERING", "DELIVER_DELAYED", "RUNNING", "CANCELLING"}
MAX_CONTENT_BYTES = 60 * 1024
SECRET_NAMES = ("TENCENTCLOUD_SECRET_ID", "TENCENTCLOUD_SECRET_KEY")


def redact(message: str) -> str:
    for name in SECRET_NAMES:
        value = os.environ.get(name)
        if value:
            message = message.replace(value, "[密钥已隐藏]")
    return message


def create_client(region: str):
    for name in SECRET_NAMES:
        value = os.environ.get(name)
        if not value:
            raise ValueError(f"未设置环境变量 {name}；请在运行环境的密钥设置中配置，不要写进文件。")
        if any(char.isspace() for char in value):
            raise ValueError(f"环境变量 {name} 含空格或换行，请检查配置（不要打印密钥）。")

    try:
        from tencentcloud.common import credential
        from tencentcloud.common.common_client import CommonClient
        from tencentcloud.common.profile.client_profile import ClientProfile
        from tencentcloud.common.profile.http_profile import HttpProfile
    except ImportError:
        raise RuntimeError(
            "未安装腾讯云 SDK；请运行 python3 -m pip install -r tools/tencent/requirements.txt"
        ) from None

    # SDK 显式传入 certifi，单设 REQUESTS_CA_BUNDLE 不生效；在这里传入环境的 CA。
    certification = (
        os.environ.get("REQUESTS_CA_BUNDLE")
        or os.environ.get("CURL_CA_BUNDLE")
        or os.environ.get("SSL_CERT_FILE")
        or ssl.get_default_verify_paths().cafile
    )
    profile = ClientProfile(
        httpProfile=HttpProfile(
            endpoint="tat.tencentcloudapi.com", reqTimeout=30, certification=certification
        )
    )
    cred = credential.Credential(*(os.environ[name] for name in SECRET_NAMES))
    return CommonClient("tat", "2020-10-28", cred, region, profile=profile)


def explain_error(error: Exception) -> str:
    code = getattr(error, "code", "")
    message = str(error)
    if code in {"ClientNetworkError", "ClientNetworkSSLError"}:
        if "CERTIFICATE_VERIFY_FAILED" in message or "certificate verify failed" in message:
            return "证书校验失败：请检查运行环境的 CA 证书，设置 SSL_CERT_FILE 或 REQUESTS_CA_BUNDLE；不要关闭证书校验。"
        return "当前运行环境无法连接腾讯云 TAT：请开启联网权限并允许 tat.tencentcloudapi.com，保留环境代理设置。不要改密钥或尝试 SSH。"
    if code.startswith("AuthFailure") or code == "SignatureFailure":
        return "腾讯云认证失败：请检查两个密钥环境变量是否填反、过期或含空白；只检查长度，不要打印值。"
    if code.startswith("UnauthorizedOperation"):
        return "密钥没有执行此操作的权限：请在腾讯云访问管理（CAM）中授予 QcloudTATFullAccess。"
    if "InstanceIdNotExist" in code:
        return "找不到实例：请检查地域是否为 ap-shanghai，以及轻量实例的 lhins- 编号；不用转换成 ins- 编号。"
    if "AgentNotRunning" in code or "AgentNotRunning" in message:
        return "服务器的自动化助手客户端离线，请在腾讯云控制台检查其状态；需要重启服务器时先取得用户同意。"
    return redact(message)


def submit(client, args: argparse.Namespace, script: bytes) -> str:
    content = base64.b64encode(script).decode()
    if not script.strip():
        raise ValueError("命令不能为空。")
    if len(content) > MAX_CONTENT_BYTES:
        raise ValueError(f"脚本编码后 {len(content)} 字节，超过工具的 60KB 上限；请缩短脚本。")
    return client.call_json(
        "RunCommand",
        {
            "Content": content,
            "InstanceIds": [args.instance],
            "CommandType": "SHELL",
            "WorkingDirectory": "/root",
            "Username": "root",
            "Timeout": args.timeout,
            "SaveCommand": False,
            "CommandName": "dawnshard-ops",
            "Description": "Run maintenance command from repository tool",
        },
    )["Response"]["InvocationId"]


def wait_for_result(client, invocation: str, args: argparse.Namespace) -> int:
    deadline = time.monotonic() + args.wait_seconds
    while time.monotonic() < deadline:
        tasks = client.call_json(
            "DescribeInvocationTasks",
            {
                "Filters": [{"Name": "invocation-id", "Values": [invocation]}],
                "HideOutput": False,
            },
        )["Response"]["InvocationTaskSet"]
        task = next((task for task in tasks if task.get("InstanceId") == args.instance), None)
        if task and task["TaskStatus"] not in PENDING_STATES:
            result = task.get("TaskResult") or {}
            output = base64.b64decode(result.get("Output") or "").decode("utf-8", "replace")
            print(redact(output), end="" if output.endswith("\n") else "\n")
            if result.get("Dropped"):
                print(f"（输出太长，腾讯云已截掉 {result['Dropped']} 字节；请缩小日志范围重新查询。）")
            print(f"状态：{task['TaskStatus']}，退出码：{result.get('ExitCode')}")
            if task["TaskStatus"] != "SUCCESS":
                for name in ("ErrorInfo", "ExecErrorInfo"):
                    if result.get(name):
                        print(redact(str(result[name])), file=sys.stderr)
            return 0 if task["TaskStatus"] == "SUCCESS" and result.get("ExitCode") == 0 else 1
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(3, remaining))

    print("本次等待已结束，服务器上的命令不会被取消。继续查询同一编号，避免重复执行：")
    print(shlex.join([
        sys.executable, str(Path(__file__).resolve()), "--invocation", invocation,
        "--region", args.region, "--instance", args.instance,
    ]))
    return 2


def positive_integer(value: str) -> int:
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("必须是大于 0 的整数")
    return number


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--command", "-c", help="服务器上执行的命令；需要引号")
    source.add_argument("--file", type=Path, help="要在服务器上执行的本地脚本")
    source.add_argument("--invocation", help="已有执行编号，仅查询结果，不重新执行命令")
    parser.add_argument("--region", default=os.environ.get("TENCENT_REGION", "ap-shanghai"))
    parser.add_argument("--instance", default=os.environ.get("TENCENT_INSTANCE_ID", "lhins-cjiput56"))
    parser.add_argument("--timeout", type=positive_integer, default=120, help="服务器执行时限，默认 120 秒")
    parser.add_argument("--wait-seconds", type=positive_integer, default=60, help="本地等待时限，默认 60 秒")
    args = parser.parse_args(argv)

    try:
        script = None
        if not args.invocation:
            script = args.file.read_bytes() if args.file else args.command.encode()
        client = create_client(args.region)
        invocation = args.invocation or submit(client, args, script)
        print(f"InvocationId={invocation}", flush=True)
        return wait_for_result(client, invocation, args)
    except KeyboardInterrupt:
        print("\n已停止本地等待；远端命令可能仍在执行，请用上面的 InvocationId 继续查询。", file=sys.stderr)
        return 130
    except Exception as error:
        print(f"错误：{redact(explain_error(error))}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
