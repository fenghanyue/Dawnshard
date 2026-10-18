"""通过腾讯云「自动化助手」(TAT) 在服务器上执行 deploy.sh，并等待结果。

用法：
    python3 deploy/tencent/tat_run.py --image ghcr.io/fenghanyue/dragalia-api:sha-xxxxxxx

腾讯云密钥从环境变量 TENCENTCLOUD_SECRET_ID / TENCENTCLOUD_SECRET_KEY 读取。
拉非公开镜像时，把凭证放在环境变量 GHCR_USER / GHCR_TOKEN 里。
加 --print-script 只打印要发到服务器上的脚本（凭证会打码），不调用接口。

依赖：pip install tencentcloud-sdk-python-common tencentcloud-sdk-python-tat
"""

import argparse
import base64
import os
import shlex
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent

# TAT 的状态里，这些表示还没执行完
PENDING_STATES = {"PENDING", "DELIVERING", "DELIVER_DELAYED", "RUNNING", "CANCELLING"}

# TAT 对命令内容（base64 之后）的长度上限是 64KB，留一点余量
MAX_CONTENT_BYTES = 60 * 1024


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode()


def build_script(image: str, ghcr_user: str, ghcr_token: str) -> str:
    variables = {
        "IMAGE": image,
        "COMPOSE_B64": b64((HERE / "docker-compose.yml").read_bytes()),
        "CONFIG_B64": b64((HERE / "appsettings.Production.json").read_bytes()),
    }
    if ghcr_token:
        variables["GHCR_USER"] = ghcr_user or "github"
        variables["GHCR_TOKEN"] = ghcr_token

    header = "".join(f"export {k}={shlex.quote(v)}\n" for k, v in variables.items())
    return "#!/bin/bash\n" + header + (HERE / "deploy.sh").read_text()


def run(args: argparse.Namespace, script: str) -> int:
    from tencentcloud.common import credential
    from tencentcloud.common.common_client import CommonClient
    from tencentcloud.common.profile.client_profile import ClientProfile
    from tencentcloud.common.profile.http_profile import HttpProfile

    cred = credential.Credential(
        os.environ["TENCENTCLOUD_SECRET_ID"], os.environ["TENCENTCLOUD_SECRET_KEY"]
    )
    profile = ClientProfile(httpProfile=HttpProfile(endpoint="tat.tencentcloudapi.com"))
    client = CommonClient("tat", "2020-10-28", cred, args.region, profile=profile)

    content = b64(script.encode())
    if len(content) > MAX_CONTENT_BYTES:
        print(f"脚本太长（{len(content)} 字节），超过 TAT 的上限", file=sys.stderr)
        return 1

    invocation = client.call_json(
        "RunCommand",
        {
            "Content": content,
            "InstanceIds": [args.instance],
            "CommandType": "SHELL",
            "WorkingDirectory": "/root",
            "Timeout": args.timeout,
            "Username": "root",
            "SaveCommand": False,
            "CommandName": "dawnshard-deploy",
            "Description": f"deploy {args.image}",
        },
    )["Response"]["InvocationId"]
    print(f"已发到服务器，InvocationId={invocation}，等待执行完……", flush=True)

    deadline = time.time() + args.timeout + 120
    while time.time() < deadline:
        time.sleep(5)
        tasks = client.call_json(
            "DescribeInvocationTasks",
            {
                "Filters": [{"Name": "invocation-id", "Values": [invocation]}],
                "HideOutput": False,
            },
        )["Response"]["InvocationTaskSet"]
        if not tasks or tasks[0]["TaskStatus"] in PENDING_STATES:
            continue

        task = tasks[0]
        result = task.get("TaskResult") or {}
        output = base64.b64decode(result.get("Output") or "").decode("utf-8", "replace")
        print(output)
        if result.get("Dropped"):
            print(f"（输出太长，后面 {result['Dropped']} 字节被腾讯云截掉了）")
        print(f"状态：{task['TaskStatus']}，退出码：{result.get('ExitCode')}")
        return 0 if task["TaskStatus"] == "SUCCESS" and result.get("ExitCode") == 0 else 1

    print("等太久了，去腾讯云控制台的「自动化助手 → 执行记录」里看看", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--image", required=True, help="要部署的游戏服务器镜像")
    parser.add_argument("--region", default=os.environ.get("TENCENT_REGION", "ap-shanghai"))
    parser.add_argument(
        "--instance", default=os.environ.get("TENCENT_INSTANCE_ID", "lhins-cjiput56")
    )
    parser.add_argument("--timeout", type=int, default=1200, help="服务器上最多执行多少秒")
    parser.add_argument("--print-script", action="store_true", help="只打印脚本，不执行")
    args = parser.parse_args()

    ghcr_user = os.environ.get("GHCR_USER", "")
    ghcr_token = os.environ.get("GHCR_TOKEN", "")

    if args.print_script:
        script = build_script(args.image, ghcr_user, ghcr_token)
        if ghcr_token:
            script = script.replace(shlex.quote(ghcr_token), "'***'")
        print(script, end="")
        return 0

    return run(args, build_script(args.image, ghcr_user, ghcr_token))


if __name__ == "__main__":
    sys.exit(main())
