# 私服交接记录

写给接手这个私服的人或 AI：现状、做过的事、踩过的坑。怎么部署、怎么改活动和卡池见 [README.md](README.md)，
待办都在 GitHub 的 issue 里。文中的 #数字 指本仓库（fenghanyue/Dawnshard）的 issue 或 PR。

## 现状（2026-10-19）

- 给用户自己和一个朋友玩的私服，跑在腾讯云轻量应用服务器上（上海，实例号见 `.github/workflows/tencent-deploy.yaml`）。
- 只有安卓：手机装原版游戏和 Dragalipatch 补丁连服务器，资源从服务器上的资源缓存（8081 端口）下载。
- 服务器跑的是 `ghcr.io/fenghanyue/dragalia-api:sha-c2c184d`（#16 合并后的 main）。手机端资源版本由部署脚本生成。
- 开着活动 20401「忠龙之愿王之魂葬」和卡池 1020002（Lily、Leviathan），时间都是 2026-10-18 06:00 到 11-01 05:59（UTC）。
- 20401 的剧情关能打。#16 补了战货召唤的奖池，把讨伐战改成单人打，修了高级讨伐战开打和奖章领取的报错，
  还等用户在手机上试讨伐战（#2）。活动页有白框（游戏最后一版里这个活动的图片删了），用户说不管。

这一节过时了就顺手改。

## 和用户怎么配合

- 用中文，少用术语。用户说看不懂，就换更简单的说法重讲。
- 改代码、写文件之前，先说要改什么、为什么，等用户同意再动手。技术上有疑问直接提，同一个问题别换着说法反复问。
- 流程：在 `claude/` 开头的分支上改 → 开 PR（中文说明）→ 用合并提交合进 main，标题写成
  `Merge pull request #N: 一句用户看得懂的话` → 自动部署 → 把部署结果告诉用户 → 更新相关的 issue。
- 只改 `.md` 文件不会触发部署。
- 待办记在 issue 里，用中文写；做完一项勾一项，能关的关掉。
- 仓库是公开的：服务器 IP、域名、密码、密钥都不能写进仓库、issue、PR。腾讯云 API 密钥只在仓库的 Actions secrets 里
  （`TENCENTCLOUD_SECRET_ID`、`TENCENTCLOUD_SECRET_KEY`）。
- 安全加固暂缓（#8）。以后要做，不能影响用户用腾讯云 API 密钥操作服务器。
- 部署结果看 Actions 里 Deploy to Tencent Cloud 的运行记录，最后一步会打印服务器上 `deploy.sh` 的输出。
  要在服务器上看日志、查文件，固定用 [`tools/tencent/tat.py`](../../tools/tencent/tat.py)，用法见
  [README 的连接服务器一节](README.md#连接服务器查日志看文件)。API 密钥从环境变量读取；已有密钥就直接用，不要反复向用户索要。

## 服务器连接（接手必读）

- **唯一方式是腾讯云自动化助手 TAT，不用 SSH/scp，不找密码或私钥，不改 SSH 配置。**
- 固定工具：`python3 tools/tencent/tat.py -c 'uptime; docker ps'`；Python 环境准备见 README。
- 地域默认 `ap-shanghai`，轻量实例编号已写进工具默认值，`lhins-` 直接用于 TAT，不转换成 `ins-`。
- API 域名 `tat.tencentcloudapi.com`、版本 `2020-10-28`；凭证只读
  `TENCENTCLOUD_SECRET_ID` / `TENCENTCLOUD_SECRET_KEY`，不写入文件或输出值。
- 用 `--file` 发脚本，用 `--invocation` 接着查已有命令，避免本地中断后重复执行。不要为了接着查结果取消或重发部署。
- 沙箱联网权限和腾讯云权限是两回事：本地报连不上、超时、DNS 失败或 SDK 装不上时，停止并请用户开启联网权限，不绕路。
- 2026-10-19 实测：SDK 默认用自己的 certifi，单设 `REQUESTS_CA_BUNDLE` 无效，会报证书链不可信。
  工具在 `HttpProfile.certification` 显式传入环境/系统 CA，保留代理和证书校验。
- 先只读；删文件、重启容器、改数据库或网络配置前先说明并等用户同意。改数据库前备份，部署走 GitHub Actions。
- 2026-10-19 的登录认证 502 在排查时自行恢复，用户确认能进游戏；登录、读存档、首页接口都返回 `Success`。
  当时容器、服务器时间和认证服务的公开接口正常，没改代码、重启容器或改存档，尚不能确定故障的具体来源。

## 做过的事

| PR | 做了什么 |
|---|---|
| #10 | 接上腾讯云自动部署（这个目录和 `tencent-deploy.yaml`），开了开服第一期卡池 |
| #11 | 部署改成先拉镜像、拉到了再动服务器；从南京大学的镜像站按 digest 拉；时限放到 60 分钟 |
| #12 | 卡池的普通池按当年的上线日期过滤（#4）；MessagePack 升到 3.1.12 |
| #13 | 部署时按排好的活动改手机端游戏数据表，活动能在手机上显示了（#2）；资源缓存的配置收进仓库 |
| #14 | 讨伐战活动没配活动召唤的奖品表时，服务器给空宝箱，不再把手机踢回主界面 |
| #16 | 20401 的战货召唤补上奖池；讨伐战改成单人打；修了老活动的两个报错（高级讨伐战开打、奖章领取） |

不在仓库里的操作：

- 停了服务器上原来的资源预下载脚本，留下的文件还没删（#5）。
- 2026-10-18 拍了快照 `before-first-repo-deploy-20261018`（免费名额 2 个，用了 1 个）。
- 直接改数据库，给 4 个存档补了付费钻（表 `PlayerDiamondData` 的 `PaidDiamond`）。上限 9,999,999 写在
  `Features/Shared/Reward/Handlers/DiamantiumHandler.cs`，超过上限以后游戏发的付费钻会进不来。改存档前先备份。

## 踩过的坑

### 部署

- 服务器在国内，直连 ghcr.io 每秒只有几十 KB，还会断。现在先从 `ghcr.nju.edu.cn` 按 digest 拉
  （`ghcr.m.daocloud.io` 返回 403，用不了），不行再直连。
- 服务器的 `/root/.docker/config.json` 里留着一条过期的 ghcr.io 登录，带着它拉公开镜像也会 `denied`，
  所以 `deploy.sh` 直连 ghcr.io 前会先 `docker logout`。
- 腾讯云「自动化助手」（TAT）：
  - 发过去的命令（base64 之后）最多 64KB。`tat_run.py` 把 `deploy.sh` 和要带的文件打包塞进命令里，现在约 41KB，
    超过 60KB 就不发；往 `EXTRA_FILES` 里加大文件会超。
  - `DescribeInvocations` 查回来的 Description 是空的，等结果要按 invocation id 或创建时间找。
- Claude Code 的自动模式拦过几次：改服务器的 SSH 配置、在服务器上跑删文件的脚本、取消正在跑的部署
  （CancelInvocation）。被拦了不要绕，跟用户说清楚，让用户决定。
- 本地编译只有警告、GitHub 上却失败：`DragaliaAPI/Directory.Build.props` 只在 `GITHUB_ACTIONS=true` 时把警告当错误。
  #12 就是因为 MessagePack 3.1.7 有漏洞（NU1902），GitHub 上的测试全都编译失败。本地复现要加 `GITHUB_ACTIONS=true`。

### 服务器配置

- .NET 读配置时，写错的角色名、龙名会被悄悄丢掉，不报错，服务器照样健康。所以部署前一定跑 `check_config.py`。
- 配置里的数组按下标合并，不是整个替换：
  - `Resources/*.json` 比 appsettings 后加载、优先级高，所以卡池配置是把整个 `bannerConfig.json` 挂进容器替换掉。
  - 镜像自带的 `appsettings.json` 里 `EventList` 有一条 22903（2022 年的，早过期），我们的第 0 条会盖掉它；
    我们的列表清空了它就会留下来（已经过期，没影响）。
  - 测试环境的 `appsettings.Testing.json` 开了一串活动，临时测试再加活动要换个不冲突的下标（用过 50）。
- `appsettings.Production.json` 整个替换镜像里的同名文件，原来那份里写日志文件的设置也就没了，日志只在
  `docker compose logs dragaliaapi` 里。
- 游戏数据里 2021 年以前上线的角色和龙，上线日期都是占位的 `2021/01/01 06:00:00`，所以 `gen_release_dates.py`
  用第一次当卡池主打的日期代替。原作者会改 `SummonData.json` 里的日期来重开卡池，合并原作者的更新后**不要**重新生成
  `ReleaseHistory.g.cs`。

### 手机端游戏数据表（#2）

- 活动显不显示看手机下载的数据表（master）里的日期，服务器的 `EventOptions` 只管放不放行
  （`Features/Event/EventValidationFilter.cs`）。卡池不一样，是服务器发给手机的，改 `bannerConfig.json` 就行。
- 手机怎么拿到新数据表：服务器每次回复都带资源版本（`ResourceVersionOptions.Android`），和手机上的不一样，手机就去下
  `/dl/manifests/Android/<版本>/` 下的资源清单，再按清单下变了的文件。资源文件的路径是
  `/dl/assetbundles/Android//<前两位>/<文件名>`（中间两个斜杠），资源缓存把 `Android` 换成资源站上的 `universe`。
- 资源清单是加密的（Rijndael，块和密钥都是 256 位，CBC，后面附一段 SHA-256），密钥来自公开的 DragaliaModTools，
  实现在 `client-master/unity_bundle.py`。资源文件名是文件内容 SHA-256 的 base32 前 52 位。
- 5 个语言包（日、英美、英欧、简中、繁中）各有一份数据表，各改各的。用户用简中。
- 别用 UnityPy 整包保存：它会重排对象、重写类型信息，和原文件差很多。现在只把要改的字段原地换成一样长的值，
  只重新压缩改到的那几块，其余字节照搬。
- 每张表是 .NET 的 Dictionary 序列化出来的，只有前 `count` 个槽是真的行，后面是空槽。
- 资源站 cdn.minty.sbs 会拒绝 Python 默认的请求标识（`Python-urllib/…` 得到 403），换个 User-Agent 就好，
  不需要手机请求里带的 `reliable_token`。以前服务器上的预下载脚本大量 403 就是这个原因，不是缺 token。
- 手机每次打开讨伐战活动页都会请求 `event_summon/get_data`（活动召唤，也就是宝箱抽奖），数据表里活动召唤关着也照样请求。
  #13 时以为不会请求，结果一进活动就被踢回主界面，#14 才修好。58 个带活动召唤的活动里，`Resources/eventSummon.json`
  只配了 20429、20457 的奖品。
- 没排活动时用原版的资源版本 `y2XM6giU6zz56wCm`。直连原资源站的手机找不到我们生成的版本，所以必须走 8081。

### 讨伐战（#2）

- 讨伐战（关卡类型 2）要连联机服务器 Photon，私服没有：官方授权每月 125 + 95 美元，还只能装在 Windows 上
  （见 `PhotonPlugin/README.md`）。服务器在 `load/index` 里把 `PhotonOptions.ServerUrl` 发给手机当联机地址，我们没配，
  发的是 `appsettings.json` 里的占位地址。手机点讨伐战会一直转圈，服务器日志里一个请求都没有。
- 现在的办法：部署时把排上的活动里的讨伐战改成单人讨伐战（关卡类型 10，`client_master.py` 的 `SOLO_RAID_FIELDS`），
  服务器单人开打讨伐战时也回单人讨伐战（`DungeonStartController.UseSoloRaid`）。依据是后期活动里单人和联机讨伐战用
  同一张战斗地图（20429、20455 核对过），20401 的 204010401 用的就是 20458 单人讨伐战那张。手机认不认要在手机上试。
- 体力按服务器自己的数据表扣：20401 前三档讨伐战本来就有单人体力（3、6、9），照扣；204010401 原来没有，
  手机上填了 15，服务器不扣。要碎片的照扣碎片。
- 别的要联机的玩法还是进不去。

### 活动页的图片

- 游戏最后一版里，很多老活动页的图片删了（`images/outgame/eventlocalized/<活动编号>/`）：20401 只剩 1 张，
  20429 一张都没有，2022 年重开的 20450–20459 有 20 多张。缺图的地方显示白框，不影响玩。
- 重开过的活动（`EventData` 的 `_BaseEventId` 一样，比如 20401 → 20458）换成重开版就没有白框，但剧情要重打、
  道具不通用，讨伐战的档位也不一样。用户选了按老活动的原编号来，白框不管。

### 战货召唤（宝箱抽奖）

- 奖池在服务器的 `Resources/eventSummon.json`，原作者只配了 20429、20457。
- 后期讨伐战活动用的是同一套奖池：20429（2021 年）和 20455（2022 年，dragalia-api-docs 上 `event_summon/get_data`
  那页有当年的抓包）第一箱 31 种奖品一模一样，只有大奖那格不同。20401 照 20429 抄，大奖换成 `BoxSummonData` 里的
  重置道具（龙 20050103 佩雷）。第二箱以后的数量照 20429；2018 年当年的箱子是什么样没有记录。
- 每抽几个战货看 `BoxSummonData` 的 `_CostNum`（20401 是 2，后期是 1）。

### 原作者代码里修过的（老活动才碰得到）

- 2018–2019 年的 16 个讨伐战活动（20401–20407 等）没有「异界宝石」，高级以上的讨伐战一开打就报错
  （服务器算掉落时找不到它），而且报错前碎片已经扣了。`EventDropService` 改成没有就不掉。
- 老活动、老任务、老兑换的奖励里，纹章写的是「护符」（`EntityTypes.Amulet`，200 多处），服务器只认 `Wyrmprint`，
  领奖整次失败（20401 的奖章奖励就是这样）。编号是同一套，`AbilityCrestRewardHandler` 现在也认护符。

### 资源缓存（8081）

- nginx 的缓存键 `$uri$is_args$args` 别改，一改原来缓存的五万多个文件全部失效，要重新从资源站下。
- `default.conf` 是单个文件挂进容器的，更新时 `deploy.sh` 用 `cp` 原地覆盖（不换 inode），容器里才看得到新内容。
- compose 的项目名是 `asset-cache`（容器 `asset-cache-assetcache-1`），换个项目名再起一个会端口冲突。

### 本地开发（云端容器里）

- .NET SDK 按 `global.json` 自己装，设好 `DOTNET_ROOT`（不设的话 MissionDesigner 报 "You must install .NET"）和 `PATH`，
  再加 `HUSKY=0`。浅克隆时编译要加 `-p:NBGV_GitEngine=Disabled`。
- 编译会改 `DragaliaAPI/DragaliaAPI.Shared/Generated/` 下的 `LoggerMessage.g.cs`，提交前
  `git checkout -- DragaliaAPI/DragaliaAPI.Shared/Generated/`。
- 集成测试要 Docker（postgres:16、redis-stack）。容器里要自己在后台起 `dockerd`，容器重启后要重起。
  Docker Hub 拉镜像碰到 429，可以从 `public.ecr.aws/docker/library/` 拉了再改名。
- 本地一口气跑全部集成测试不稳（连接被拒、dockerd 被杀），挑相关的类跑（`--filter-class`），全量交给 GitHub。
- 想先确认一个活动能不能玩：写个临时集成测试，用 `CreateClient(builder => builder.ConfigureAppConfiguration(...))`
  往 `EventOptions:EventList:50:Id`、`:Start`、`:End` 写活动编号和时间，依次请求 `raid_event/get_event_data`、
  `raid_event/entry`、`event_summon/get_data`，再对每个关卡请求 `dungeon_start/start`、`dungeon_record/record`，看返回码。
  20401 就是这么查的。用完删掉，别提交。
- 提交前对改过的文件跑 `dotnet csharpier format`。

### 发现了但没修的

- 活动角色没有专属养成材料时（63 个带活动角色的活动里有 40 个，包括 20401 的赛丽艾拉），活动期间解锁魔力节点只扣魔力、
  不扣材料：`Features/Chara/CharaController.cs` 里 `IsCharaEventActive` 为真时只扣魔力和专属材料，而专属材料是空的。
  原作者的 bug，对玩家有利，没修。
- 开打时先扣道具（碎片等）再算掉落，算掉落报错的话道具已经扣了（`DungeonStartController.BuildResponse` 先保存再算）。
- 还有 5 个讨伐战活动（20427、20428、20443、20444、20462）没有战货、奖章、碎片这些道具，排它们之前先确认掉落代码不会崩。

## 待办

- #2：等用户在手机上试 20401 的讨伐战（单人）、战货召唤、奖章领取，都行就关。
- #3：按开服顺序排活动和卡池，用活动的原编号（用户定的，白框不管）。节奏等用户定；要写排期脚本、
  补活动召唤的奖品表（讨伐战活动照 20429 的抄，见上）、跳过服务器不支持的活动类型、补活动任务、补 3★/4★ 的准确上线日期。
- #5：资源缓存缺约 1/4 的文件，还有长期保存的几个坑。补文件时限速，做之前问用户。
- #6：定时备份。
- #7：朋友加入。资源地址必须填 8081 的资源缓存；建号后补付费钻。
- #8：安全加固（暂缓）。
- #9：跟上原作者的更新。改过的原作者文件列在 issue 里，合并时留意冲突。
