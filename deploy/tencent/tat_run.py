"""通过腾讯云「自动化助手」(TAT) 在服务器上执行 deploy.sh，并等待结果。

用法：
    python3 deploy/tencent/tat_run.py --image ghcr.io/fenghanyue/dragalia-api:sha-xxxxxxx --digest sha256:...

部署前会先用 check_config.py 检查配置，有错就不部署。
给了 --digest，服务器会先从国内镜像站按 digest 拉镜像（快，内容会校验），不行再直连 ghcr.io。
镜像的 digest 可以这样查：docker buildx imagetools inspect <镜像> --format '{{json .Manifest}}'
腾讯云密钥从环境变量 TENCENTCLOUD_SECRET_ID / TENCENTCLOUD_SECRET_KEY 读取。
拉非公开镜像时，把凭证放在环境变量 GHCR_USER / GHCR_TOKEN 里。
加 --print-script 只打印要发到服务器上的脚本（凭证会打码），不调用接口。
联机试连加 --photon-image 和 --photon-digest；留空不更新联机，--disable-photon 则停用试连。

依赖：pip install tencentcloud-sdk-python-common tencentcloud-sdk-python-tat
"""

import argparse
import base64
import gzip
import importlib.util
import io
import os
import re
import shlex
import sys
import tarfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent

# 部署时发到服务器 /opt/dawnshard 的文件。加了新的配置文件，要在这里和 docker-compose.yml 里各加一处。
CONFIG_FILES = [
    "docker-compose.yml",
    "appsettings.Production.json",
    "bannerConfig.json",
]
# 资源缓存的配置（发到 /opt/asset-cache），和改手机端游戏数据表的脚本（在服务器上运行）
EXTRA_FILES = [
    "asset-cache/default.conf",
    "asset-cache/docker-compose.yml",
    "client-master/client_master.py",
    "client-master/unity_bundle.py",
    "client-master/requirements.txt",
    "client-master/requirements-build.txt",
    "photon/config.template.yml",
    "photon/docker-compose.yml",
    "photon/docker-compose.rooms.yml",
    "photon/prepare.py",
    "photon/manage.sh",
    "guest-auth/prepare.py",
    "guest-auth/Caddyfile",
]

# TAT 的状态里，这些表示还没执行完
PENDING_STATES = {"PENDING", "DELIVERING", "DELIVER_DELAYED", "RUNNING", "CANCELLING"}

# TAT 对命令内容（base64 之后）的长度上限是 64KB，留一点余量
MAX_CONTENT_BYTES = 60 * 1024


def b64(data: bytes) -> str:
    return base64.b64encode(data).decode()


def encode_script(script: str) -> str:
    """Keep TAT's existing size limit while transporting the larger deployment bundle."""
    payload = b64(gzip.compress(script.encode(), mtime=0))
    bootstrap = (
        "#!/bin/bash\npython3 - <<'DAWNSHARD_DEPLOY'\n"
        "import base64,gzip,os,subprocess,sys\n"
        f"script=gzip.decompress(base64.b64decode('{payload}'))\n"
        "fd=os.memfd_create('dawnshard-deploy',0)\n"
        "with os.fdopen(fd,'wb',closefd=False) as file: file.write(script)\n"
        "os.lseek(fd,0,0)\n"
        "sys.exit(subprocess.run(['/bin/bash','/proc/self/fd/'+str(fd)],pass_fds=(fd,),stdin=subprocess.DEVNULL).returncode)\n"
        "DAWNSHARD_DEPLOY\n"
    )
    return b64(bootstrap.encode())


def build_bundle() -> bytes:
    def as_root(info: tarfile.TarInfo) -> tarfile.TarInfo:
        info.uid = info.gid = 0
        info.uname = info.gname = "root"
        info.mode = 0o644
        return info

    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name in CONFIG_FILES + EXTRA_FILES:
            tar.add(HERE / name, arcname=name, filter=as_root)
    return buffer.getvalue()


def build_script(
    image: str,
    digest: str,
    ghcr_user: str,
    ghcr_token: str,
    photon_image: str = "",
    photon_digest: str = "",
    disable_photon: bool = False,
    photon_rooms: bool = False,
) -> str:
    variables = {
        "IMAGE": image,
        "BUNDLE_B64": b64(build_bundle()),
        "PHOTON_IMAGE": photon_image,
        "PHOTON_DIGEST": photon_digest,
        "DISABLE_PHOTON": "1" if disable_photon else "0",
        "PHOTON_ROOMS": "1" if photon_rooms else "0",
    }
    if digest:
        variables["IMAGE_DIGEST"] = digest
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

    content = encode_script(script)
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
        return (
            0 if task["TaskStatus"] == "SUCCESS" and result.get("ExitCode") == 0 else 1
        )

    print("等太久了，去腾讯云控制台的「自动化助手 → 执行记录」里看看", file=sys.stderr)
    return 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--image", required=True, help="要部署的游戏服务器镜像")
    parser.add_argument(
        "--photon-rooms",
        action="store_true",
        help="使用包含房间准备插件和列表服务的联机镜像",
    )
    parser.add_argument(
        "--digest", default="", help="镜像的 digest（sha256:...），给了才会走国内镜像站"
    )
    parser.add_argument(
        "--photon-image", default="", help="可选，联机试连镜像；留空保留服务器原配置"
    )
    parser.add_argument(
        "--photon-digest",
        default="",
        help="联机试连镜像的 digest，必须和 --photon-image 一起给",
    )
    parser.add_argument(
        "--disable-photon",
        action="store_true",
        help="游戏健康后停用联机试连，恢复默认联机地址",
    )
    parser.add_argument(
        "--region", default=os.environ.get("TENCENT_REGION", "ap-shanghai")
    )
    parser.add_argument(
        "--instance", default=os.environ.get("TENCENT_INSTANCE_ID", "lhins-cjiput56")
    )
    # 镜像站不行、退回直连 ghcr.io 时，整个镜像要下半个小时左右
    parser.add_argument(
        "--timeout", type=int, default=3600, help="服务器上最多执行多少秒"
    )
    parser.add_argument(
        "--print-script", action="store_true", help="只打印脚本，不执行"
    )
    args = parser.parse_args()
    if args.digest and not re.fullmatch(r"sha256:[0-9a-f]{64}", args.digest):
        print(f"--digest 格式不对：{args.digest}", file=sys.stderr)
        return 1
    if bool(args.photon_image) != bool(args.photon_digest):
        parser.error("--photon-image 和 --photon-digest 必须一起给")
    if args.photon_digest and not re.fullmatch(
        r"sha256:[0-9a-f]{64}", args.photon_digest
    ):
        parser.error("--photon-digest 格式不对")
    if args.photon_image and not re.fullmatch(
        r"ghcr\.io/[a-z0-9._/-]+:[a-zA-Z0-9._-]+", args.photon_image
    ):
        parser.error("--photon-image 必须是 ghcr.io 上带 tag 的镜像")
    if args.disable_photon and args.photon_image:
        parser.error("--disable-photon 不能和 --photon-image 一起给")
    if args.photon_rooms and not args.photon_image:
        parser.error("--photon-rooms 必须和 --photon-image 一起给")

    ghcr_user = os.environ.get("GHCR_USER", "")
    ghcr_token = os.environ.get("GHCR_TOKEN", "")

    spec = importlib.util.spec_from_file_location(
        "check_config", HERE / "check_config.py"
    )
    check_config = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(check_config)
    errors = check_config.check()
    for error in errors:
        print(f"配置有误：{error}", file=sys.stderr)
    if errors:
        return 1

    script = build_script(
        args.image,
        args.digest,
        ghcr_user,
        ghcr_token,
        args.photon_image,
        args.photon_digest,
        args.disable_photon,
        args.photon_rooms,
    )
    if len(encode_script(script)) > MAX_CONTENT_BYTES:
        print("脚本太长，超过 TAT 的 60KB 限制", file=sys.stderr)
        return 1
    if args.print_script:
        if ghcr_token:
            script = script.replace(shlex.quote(ghcr_token), "'***'")
        print(script, end="")
        return 0

    return run(args, script)


if __name__ == "__main__":
    sys.exit(main())
