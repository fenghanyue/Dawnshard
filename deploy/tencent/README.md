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
| `../../.github/workflows/tencent-deploy.yaml` | 自动部署的流程 |

数据库密码、`DEVELOPER_TOKEN` 只在服务器的 `/opt/dawnshard/.env` 里，不进仓库。

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
