# 腾讯云私服部署

这个目录让 fork 和腾讯云上的服务器（轻量应用服务器，上海）联动：

```
推代码到 main ──► GitHub Actions 编译镜像 ──► 推到 ghcr.io/fenghanyue/dragalia-api
                                          └─► 腾讯云「自动化助手」在服务器上执行 deploy.sh
```

服务器在国内，直连 ghcr.io 下载每秒只有几十 KB，还会断线。所以 `deploy.sh` 先从南京大学的镜像站
`ghcr.nju.edu.cn` 按 digest 拉镜像（实测每秒近 10 MB；按 digest 拉，docker 会校验内容，镜像站没法偷换），
镜像站不行再直连 ghcr.io。

| 文件 | 作用 |
|---|---|
| `docker-compose.yml` | 服务器上的容器配置（游戏服务器 + 数据库 + redis） |
| `appsettings.Production.json` | 私服配置，比如开哪些活动（`EventOptions`） |
| `bannerConfig.json` | 开哪些卡池，替换镜像里自带的 `Resources/bannerConfig.json` |
| `deploy.sh` | 在服务器上执行：拉镜像 → 生成手机端的游戏数据表 → 备份数据库 → 更新资源缓存 → 换配置 → 重建游戏服务器 → 健康检查，没通过就回滚。前两步不动正在用的东西 |
| `tat_run.py` | 调腾讯云接口，把 `deploy.sh` 发到服务器执行并等结果 |
| `check_config.py` | 部署前检查配置：活动、卡池编号存不存在，角色和龙的名字有没有写错 |
| `gen_release_dates.py` | 从游戏数据生成角色、龙的上线日期表（`ReleaseHistory.g.cs`），卡池按上线日期过滤普通池时用 |
| `client-master/` | 按排好的活动改手机端游戏数据表里的日期，生成新的资源版本（见下面「活动在手机上显示」） |
| `asset-cache/` | 资源缓存（8081 端口的 nginx）的配置，部署时写到服务器的 `/opt/asset-cache` |
| `photon/` | Luxon v1.5 联机试连镜像、配置模板和独立容器的部署脚本 |
| `../../.github/workflows/tencent-deploy.yaml` | 自动部署的流程 |

数据库密码、`DEVELOPER_TOKEN` 只在服务器的 `/opt/dawnshard/.env` 里，不进仓库。

## 连接服务器（查日志、看文件）

固定入口是 [`tools/tencent/tat.py`](../../tools/tencent/tat.py)。它用腾讯云「自动化助手」（TAT）的
`RunCommand` 发命令，再用 `DescribeInvocationTasks` 取回结果；服务器上的自动化助手以 root 执行。
只需要运行环境中的 `TENCENTCLOUD_SECRET_ID`、`TENCENTCLOUD_SECRET_KEY`，不用 SSH、密码或私钥。
地域和轻量实例编号已设好默认值；需要换服务器时可以设置 `TENCENT_REGION`、`TENCENT_INSTANCE_ID`，
也可用 `--region`、`--instance` 指定。轻量实例的 `lhins-` 编号可以直接用于 TAT。

在仓库根目录安装依赖（新环境安装一次；密钥在运行环境的密钥设置中配置，不要写进命令、文件或仓库）：

```bash
python3 -m venv /tmp/dawnshard-tat
/tmp/dawnshard-tat/bin/python -m pip install -r tools/tencent/requirements.txt
```

先用只读命令检查连接：

```bash
/tmp/dawnshard-tat/bin/python tools/tencent/tat.py -c 'uptime; docker ps'
/tmp/dawnshard-tat/bin/python tools/tencent/tat.py -c 'docker compose -f /opt/dawnshard/docker-compose.yml logs --since 30m --tail 100 dragaliaapi'
```

执行本地写好的脚本，或在等待中断后继续查询原来的执行编号：

```bash
/tmp/dawnshard-tat/bin/python tools/tencent/tat.py --file /tmp/check-game.sh
/tmp/dawnshard-tat/bin/python tools/tencent/tat.py --invocation inv-xxxxxxxx
```

工具会先打印执行编号；默认远端执行时限 120 秒、本地等待 60 秒，可用 `--timeout` 和 `--wait-seconds` 调整。
本地等待超时或被中断不会取消远端命令；**用原编号继续查询，不要重复发送可能改数据的命令**。
退出码 0 表示远端命令成功，1 表示失败，2 表示尚未等到最终结果，130 表示本地等待被中断。
输出被腾讯云截断时会提示；查日志先限制时间和行数，避免输出整个存档、环境变量或 `.env`。

连接问题按报错处理：

| 报错 | 处理 |
|---|---|
| SDK 装不上、连接被拒绝、超时或 DNS 失败 | 检查当前运行环境的联网权限，允许 `pypi.org`、`files.pythonhosted.org`、`tat.tencentcloudapi.com`。这是本地连接问题，先停下告知用户，不要改密钥或尝试 SSH |
| `CERTIFICATE_VERIFY_FAILED` | 工具会把环境的 CA 证书传给 SDK（依次读取 `REQUESTS_CA_BUNDLE`、`CURL_CA_BUNDLE`、`SSL_CERT_FILE`，否则用系统 CA）。检查证书路径，不要关闭校验，也不要清掉环境代理 |
| `AuthFailure` / `SignatureFailure` | 检查环境变量是否缺失、填反、过期或带空白；只检查长度，不打印值 |
| `UnauthorizedOperation` | 在腾讯云访问管理（CAM）中给密钥添加 `QcloudTATFullAccess` |
| `InstanceIdNotExist` | 检查地域为 `ap-shanghai`、实例编号为配置的 `lhins-` 编号 |
| `AgentNotRunning` | 在腾讯云控制台检查自动化助手状态；若需要重启服务器，先取得用户同意 |

先做只读检查。删文件、重启容器、改数据库或防火墙配置之前，说明原因并等用户同意；改数据库先备份。
部署通过 GitHub Actions 的 **Deploy to Tencent Cloud**，不要用这个日常运维工具手动部署。
工具位于 `tools/`，只改工具和说明文档不会触发游戏服务器自动部署。
修改工具后可运行 `python3 -m unittest discover -s tools/tencent -v` 做离线检查，再用上面的只读命令验证连接。

## 联机：Luxon 试连与房间准备（#20）

第一步已由手机验证：连接、加密、认证、服务端创建并加入房间均成功，随后因缺少房间插件而超时。
原来的固定发布版保留在 `photon/Dockerfile`，便于复现这一结果。

第二步只实现普通联机关卡的房间准备：七位房间号、玩家信息、四人上限、公开/凭编号查房、进出房和房主退出通知。
`photon/Dockerfile.rooms` 固定编译 [LuxonServer v1.5](https://github.com/niansa/LuxonServer/releases/tag/v1.5)
（`e4bab98d8d6b3aee9ecf4035309a78a59b584f93`）及其指定的协议子模块，以原生 C++ 注册客户端要求的 `GluonPlugin`。
同一个独立容器运行 Luxon 和 PhotonStateManager，任一进程退出就重启整个房间服务。
**这一步没有开战、结算和自动匹配逻辑，必须先由手机确认能稳定停留在准备界面。** 详情见 [房间插件说明](photon/rooms/README.md)。

正常部署会单独构建 `dragalia-photon` 镜像，先按 digest 从南京大学镜像站拉，失败再按 digest 从 ghcr.io 拉。
联机镜像构建失败、下载失败或无法取得地址时，游戏照常部署，保留之前的联机地址。
游戏健康检查通过以后才启动独立 compose 项目 `dawnshard-photon`，目录为 `/opt/dawnshard/photon`。
联机启动失败会回退联机容器，同时恢复原联机地址和房间列表地址，再重建游戏容器使配置生效。
新游戏镜像保持不变；恢复配置后游戏仍不健康才执行原有的游戏版本回滚。

公网地址只在服务器上确定：优先读取现有 `.env` 里的 `PHOTON_PUBLIC_HOST`，没有则查询腾讯云元数据服务。
`PHOTON_PUBLIC_HOST` 可以是 IPv4 或域名，不带协议和端口；无需为了首次部署编辑 `.env`。
地址会写入服务器上生成的 `photon/config.yml` 和 `appsettings.Production.json` 的 `PhotonOptions.ServerUrl`，
与设置 `PhotonOptions__ServerUrl` 的作用相同。不要同时在 `.env` 中另设 `PhotonOptions__ServerUrl` 覆盖它。
房间列表地址为 Docker 内部的 `http://photon-rooms:5089`，不发布这个 HTTP 端口；管理页只监听容器里的 `127.0.0.1:5088`。
联机容器加入现有 `dawnshard_default` 网络，只发布 UDP 5055/5056。
首次配置会在私有的 `/opt/dawnshard/photon` 目录生成共享令牌，后续部署复用；不修改现有 `.env`。
地址、令牌、配置文件和联机日志不进仓库、issue、PR，也不输出到 Actions。

第一次手机验证前，用户在轻量服务器控制台的防火墙添加允许规则：UDP 5055、5056，来源为全部 IPv4。
不需要开 5058、27000–27002 或管理页的端口。
彻底退出游戏再打开，进入支持联机的普通关卡，选择联机、创建房间，等半分钟，记录手机现象和测试时间。
现在活动讨伐战仍按单人入口处理，不用于这一步验证。

通过 TAT 查看同一时间段的日志（不要贴到公开页面）：

```bash
python3 tools/tencent/tat.py -c 'docker compose --project-directory /opt/dawnshard/photon -f /opt/dawnshard/photon/docker-compose.yml logs --since 30m --tail 150 photon'
python3 tools/tencent/tat.py -c 'tail -n 80 /opt/dawnshard/photon/deploy.log'
```

手机验证：彻底退出重开，进入普通联机关卡建房；应显示七位房间号，并在准备界面停留至少一分钟。
再让朋友凭编号进入，检查双方人物显示、队员退出、房主退出。暂时不要按开始，不把房间成功当成能打关。

只回滚游戏镜像：照旧填写 Actions 的 `image`，联机容器和地址保持原样。
停用试连：在 **Deploy to Tencent Cloud** 中填写当前游戏镜像，勾选 `disable_photon`；
游戏健康后会恢复默认联机地址并停止独立联机容器，不编辑 `.env`。先告知用户此次会重建游戏容器并取得同意。
若完整撤回功能，先停用，再开 PR 撤回改动，请用户关掉两个 UDP 端口。
单次停用后，后续普通部署会重新启用试连。

PR 检查会验证失败回退、日志不泄露地址、回滚保留地址、TAT 包大小，并在 Redis 8 上使用实际镜像完成加密 UDP 建房、进出、权限和 65 秒房间续期检查。
本地检查：

```bash
bash -n deploy/tencent/deploy.sh deploy/tencent/photon/manage.sh deploy/tencent/photon/smoke.sh
python3 -m unittest discover -s deploy/tencent -p 'test_*.py' -v
python3 -m unittest discover -s tools/tencent -v
python3 deploy/tencent/tat_run.py --image ghcr.io/example/dragalia-api:test --print-script > /dev/null
docker build -t dawnshard-luxon:trial deploy/tencent/photon
bash deploy/tencent/photon/smoke.sh dawnshard-luxon:trial
```

## 第一次要做的设置

1. GitHub 仓库 → Settings → Secrets and variables → Actions → New repository secret，建两个：
   - `TENCENTCLOUD_SECRET_ID`
   - `TENCENTCLOUD_SECRET_KEY`
2. 打开仓库的 Actions 页，点 "I understand my workflows, go ahead and enable them"。
3. 原作者的 "Publish DragaliaAPI"、"Publish PhotonStateManager"、"Publish Website" 会因为没权限失败，
   可以在 Actions 页点进去 → 右上角 "..." → Disable workflow。它们第一次被触发（改了对应的代码）之后
   才会出现在 Actions 页的列表里。

## 日常怎么用

- **改活动安排**：编辑 `appsettings.Production.json` 里的 `EventOptions.EventList`，每个活动写
  `Id`（活动编号）、`Start`、`End`（UTC 时间）。合并到 main 后自动部署，手机端的数据表也会跟着改，
  见下面「活动在手机上显示」。同一个活动只能排一次。
- **改卡池**：编辑 `bannerConfig.json`，每个卡池写 `Id`、`Start`、`End`、`PickupCharas`（主打角色）、`PickupDragons`（主打龙）。
  原版卡池的编号、日期和主打对象在 `DragaliaAPI/DragaliaAPI.Shared/Resources/Summon/SummonData.json`，
  角色和龙的名字见 `DragaliaAPI/DragaliaAPI.Shared/Definitions/Enums/` 里的 `Charas`、`DragonId`。
- **普通池按当年的上线日期过滤**：`bannerConfig.json` 里的 `"FilterPoolByReleaseDate": true`。
  每个卡池的普通池只放这个卡池当年开始时已经上线的角色和龙，主打的不受影响；召唤券、Plus 召唤券和新手教程的重抽，
  按已经开过的卡池里当年日期最新的那一期来过滤。上线日期表 `DragaliaAPI/DragaliaAPI/Features/Summoning/ReleaseHistory.g.cs`
  由 `gen_release_dates.py` 从游戏数据生成：游戏数据里 2021 年以前的上线日期是假的，这些角色用第一次当卡池主打的日期；
  从没当过主打的当作开服就有，其中有些 3★/4★ 其实是后来出的。
- **改完先检查**：`python3 deploy/tencent/check_config.py`。服务器读配置时，写错的角色名、龙名会被悄悄丢掉、不报错，
  所以部署前一定会先跑这个检查，有错就不部署。
- **加新的配置文件**：放进这个目录，再在 `tat_run.py` 的 `CONFIG_FILES` 和 `docker-compose.yml` 的 `volumes` 里各加一行。
- **改代码**：改完合并到 main，自动编译、部署。
- **手动部署 / 回滚**：Actions → Deploy to Tencent Cloud → Run workflow。
  留空会编译 main 当前的代码再部署；填以前的镜像（比如 `ghcr.io/fenghanyue/dragalia-api:sha-1234567`）就直接换回那个版本。
- **从自己电脑部署**（需要设置好上面两个环境变量）：

  ```bash
  pip install tencentcloud-sdk-python-common tencentcloud-sdk-python-tat
  python3 deploy/tencent/tat_run.py --image ghcr.io/sapiensanatis/dragalia-api:4.0.204 \
      --digest sha256:bf410451d2bfe397074c54d70f8911e826bd3d9aef0257f0211e826e957b8649
  ```

  digest 用 `docker buildx imagetools inspect <镜像> --format '{{json .Manifest}}'` 查。不给 `--digest`
  就不走镜像站，直连 ghcr.io 慢慢下。

## 活动在手机上显示

游戏里显示哪些活动，看的是手机从资源站下载的游戏数据表（master）里写的日期，当年的日期早就过了。
所以每次部署时，`client-master/client_master.py` 会按 `EventOptions.EventList` 把数据表里这些活动的日期改成服务器排的时间：

- 开始、结束用服务器的时间；预告提前几天照当年。结束以后就不显示了：当年结束后还能进去看一周，
  但服务器到点就不放行，点进去会报错。
- 开始几天后才解锁的关卡、活动商店、首页横幅等的日期，按开始时间的差值平移，平移后晚于结束的截到结束，
  所以活动最好按当年的天数排。
- 活动角色「只能用专属材料养成」的时间，改成和活动一样。
- 活动召唤（宝箱抽奖）只打开 `DragaliaAPI/DragaliaAPI/Resources/eventSummon.json` 里配了奖品的活动，
  没配的打开了一点就报错，保持关闭。讨伐战活动的奖池可以照 20429 的抄，大奖换成这个活动 `BoxSummonData` 里的
  重置道具（来历见 [NOTES.md](NOTES.md)）。
- 讨伐战改成单人讨伐战：讨伐战要连联机服务器（Photon），私服没有，点进去会一直转圈。改成单人以后自己带 3 个电脑队友打，
  要碎片的照扣碎片；原来没有单人体力的，手机上显示 15，服务器不扣。
  活动菜单里的这些关卡也移到现有的单人首领战列表，关卡编号、解锁条件和奖励沿用原来的；只改关卡类型不改入口仍可能转圈。

只把日期、数字原地换成一样长的新值，数据表别的地方一个字节都不动；5 个语言包（日、英美、英欧、简中、繁中）都改。
改好的数据表和重新加密的资源清单放在服务器的 `/opt/asset-cache/overrides`，资源缓存先从那里找，找不到再去资源站。
新的资源版本号由活动安排和脚本算出来，部署时写进服务器的 `ResourceVersionOptions.Android`（不要手动改）。
版本号变了，手机进游戏时会先下一个十来 MB 的更新；活动安排没变就沿用原来的版本。服务器上保留最近 5 个版本。

- 手机必须经资源缓存（8081 端口）下载资源。直连原资源站的手机找不到我们的资源版本，进不去游戏。
- 原版文件（资源站上 `y2XM6giU6zz56wCm` 这个版本）第一次用到时下载，按写死的校验值核对，存在服务器的
  `/opt/dawnshard/client-master/cache`；改表用的 Python 库装在 `/opt/dawnshard/client-master/venv`。
- 部署前 GitHub 会先用资源站的原版文件把整个流程跑一遍（Check client data），出错就不部署。
- 手机更新后进不去游戏：撤回这次改的活动安排，重新部署，服务器会换回上一个资源版本。
- 升级改表用的库：改 `client-master/requirements*.txt` 里的版本和校验值，只留 Python 3.12、Linux x86_64 的安装包和源码包。
  服务器下次部署时会重装。

在自己电脑上试（要 Python 3.12）：

```bash
python3.12 -m venv /tmp/cm
/tmp/cm/bin/pip install --require-hashes --no-deps -r deploy/tencent/client-master/requirements-build.txt
/tmp/cm/bin/pip install --require-hashes --no-deps --no-build-isolation -r deploy/tencent/client-master/requirements.txt
/tmp/cm/bin/python deploy/tencent/client-master/client_master.py \
    --events deploy/tencent/appsettings.Production.json \
    --event-summons DragaliaAPI/DragaliaAPI/Resources/eventSummon.json \
    --source-url https://cdn.minty.sbs/dl --source-platform universe \
    --cache-dir /tmp/cm-cache --out-dir /tmp/cm-out
```

## 备份和恢复存档

每次部署前，`deploy.sh` 都会把数据库导出到服务器的 `/opt/dawnshard/backups/db-时间.dump`，保留最近 10 份。

恢复某一份（会覆盖当前存档，先停游戏服务器）：

```bash
cd /opt/dawnshard
docker compose stop dragaliaapi
docker compose exec -T postgres pg_restore -U <POSTGRES_USER> -d <POSTGRES_DB> --clean --if-exists < backups/db-时间.dump
docker compose start dragaliaapi
```

`<POSTGRES_USER>`、`<POSTGRES_DB>` 见服务器上的 `.env`。改数据库结构的代码回滚镜像不一定能用，这时就要靠这份备份。
