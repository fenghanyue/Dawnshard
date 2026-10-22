# 开活动与联机复用指南

以后开活动先按本文检查，再用 [README.md](README.md) 部署；当前线上版本和历史问题看 [NOTES.md](NOTES.md)。
中文活动顺序、原版日期和私服开放历史见 [活动记录](EVENT_HISTORY.md)，目前已整理前 10 期。
本指南来自 #10–#30 的提交和 20401 的实际排查，不把一个活动通过视为所有活动通过。

## 已经验证了什么

截至 2026-10-20，20401 的显示、剧情关、战货召唤和奖章领取已由用户确认。
普通“成长之路”的双人建房到打完也已确认正常：16:11:07 开战，16:12:15 两次 `record_multi` 均返回 `Success`，各自结果送回。
可见奖励日志为 `Added`，每日奖励与配置一致；没有战前库存，尚未逐项核对所有物品差额。
活动关双人通关、大型 Raid 联机、手机返回准备后续战不能沿用这份通过结论。

## 每次开活动的顺序

### 1. 确定活动编号和玩法范围

使用活动原编号还是重开版，会改变存档进度、活动道具和关卡，不能只因重开版图片齐全就换编号。
本私服目前沿用原编号，老活动页的白框可接受。

先查以下文件，列出该活动的关卡，而不是按中文名称猜类型：

| 要确认的内容 | 文件或代码入口 |
|---|---|
| 活动编号、原日期、类型、重开关系 | `DragaliaAPI/DragaliaAPI.Shared/Resources/Event/EventData.json`，看 `_Id`、`_EventKindType`、`_BaseEventId` |
| 关卡编号、地图、单人/联机消耗 | `DragaliaAPI/DragaliaAPI.Shared/Resources/QuestData.json`，按 `_Gid` 筛选 |
| 菜单归类、入口、解锁条件 | 手机 master 的 `QuestEventMenu`；字段由 `client-master/client_master.py` 读取 |
| 敌人、掉落、任务奖励 | `DragaliaAPI/DragaliaAPI/Features/Dungeon/QuestEnemyService.cs`、`EventDropService.cs`，以及 Shared 的 `Resources/QuestDrops/`、`Resources/QuestRewards/` |
| 活动货币及枚举 | Shared 的 `Resources/Event/*EventItem.json`、`Definitions/Enums/EventItemTypes/` |
| 联机能否开战 | `PhotonStateManager/DragaliaAPI.Photon.StateManager/Controllers/LuxonBattleController.cs`，以及 `PhotonPlugin/DragaliaAPI.Photon.Plugin/Plugins/GameLogic/QuestHelper.Data.cs` |

从仓库根目录运行这段只读查询，把 `20401` 换成目标活动编号：

```bash
python3 - 20401 <<'PY'
import json
import sys
from pathlib import Path

root = Path('DragaliaAPI/DragaliaAPI.Shared/Resources')
event_id = int(sys.argv[1])
events = json.loads((root / 'Event/EventData.json').read_text(encoding='utf-8-sig'))
event = next((row for row in events if row['_Id'] == event_id), None)
if event is None:
    raise SystemExit('没有这个活动编号')
print({key: event.get(key) for key in ('_Id', '_EventKindType', '_BaseEventId')})
quests = json.loads((root / 'QuestData.json').read_text(encoding='utf-8-sig'))
for row in quests:
    if row['_Gid'] == event_id:
        print({key: row.get(key) for key in (
            '_Id', '_DungeonType', '_QuestPlayModeType',
            '_PayStaminaSingle', '_PayStaminaMulti', '_Scene01', '_Scene02'
        )})
PY
```

这查的是服务器源数据。部署生成的手机 master 还可能应用单人 Raid 转换等改动，验收时两边都要查。

### 2. 判断哪些能力可复用

| 关卡或活动 | 当前处理方式 |
|---|---|
| 支持普通联机的战斗关，包括活动里的普通 Boss | 复用现有 Luxon 加载、角色分配、行动转发和 `record_multi`；新活动仍需双手机验证 |
| Raid 活动里的普通 Boss | 活动类型是 Raid，不代表每个关卡都是 Raid；例如独眼食人王 `204010201/202/203` 是普通关卡，不在联机拒绝名单中 |
| 大型 Raid / RaidSixteen（每人带整队角色） | 当前联机准备接口拒绝；现有角色分配不能直接当作 Raid 支持 |
| 计时排行、RandomMatching 特殊玩法 | 当前联机准备接口拒绝；不要把这里的 RandomMatching 与普通房间的查房/快速加入混为一谈 |
| `Collect`、`ExHunter`、`Simple`、`BattleRoyal` | `EventDropService` 的材料生成分支直接抛未实现异常，先补能力或暂不排期 |
| `Build`、`Raid`、`Combat`、`Clb01`、`Earn`、`ExRush` | 有掉落生成代码，但不表示整个活动完整支持；尤其 ExRush 明确有推测规则，需检查入口、货币、任务和结算 |

其他类型也要读实际分支；“有枚举”“有活动数据”“没有被联机拒绝”都不能代替完整验证。
旧 issue 中的类型黑名单可能过时，应以当前代码为准。
原版长期开放的大逃杀 `22301` 在 `client_master.py` 的 `CLOSED_EVENTS` 中，当前不能排期。

排期脚本会把已排活动中 `_DungeonType=2` 的关卡改成单人 Raid，并把对应菜单移到单人首领入口。
它没有覆盖所有特殊讨伐类型。以后要启用真正 Raid 联机，必须同时补插件能力并调整这项转换，不能仅取消转换或开放按钮。

### 3. 只修改这次需要的配置

| 文件 | 何时改 |
|---|---|
| [`appsettings.Production.json`](appsettings.Production.json) 的 `EventOptions.EventList` | 每次排活动；写 `Id`、`Start`、`End`，使用带 `Z` 的 UTC 时间，同一活动只能出现一次 |
| [`bannerConfig.json`](bannerConfig.json) | 同期要换卡池时；与活动排期分开核对，不强制一起改 |
| `DragaliaAPI/DragaliaAPI/Resources/eventSummon.json` | 活动有战货召唤而当前缺少奖池时 |
| 活动任务定义 | 活动限定任务需要新增时；已有操作说明在 `.claude/skills/event-endeavours/SKILL.md`，实际编写时再阅读应用 |
| `client-master/client_master.py` 或游戏 API | 只有出现现有流程未覆盖的日期、菜单、地图或玩法问题时才改；不要为每个活动复制一套部署/联机代码 |

北京时间比 UTC 快 8 小时，例如北京时间 14:00 对应配置里的 `06:00:00Z`；日更边界也是北京时间 14:00。
尽量保留原活动持续天数：后开放的关卡和商店按原开始日的差值平移，超出新结束时间会被截断。
同一编号的多次未来复刻不能同时写进列表，现有客户端表只容纳该编号的一段日期。

战货召唤必须核对 `BoxSummonData` 的消耗、大奖/重置道具、每箱数量和换箱条件。
20401 借用了 20429 的后期奖池、替换大奖；这不是恢复 2018 年原始奖池的证据，其他活动也不能原样照抄。
没配奖池的活动虽然能返回空宝箱、不再把人踢回首页，但这只证明活动页能打开，不能算召唤已可用。

### 4. 先离线检查，再部署

配置检查：

```bash
python3 deploy/tencent/check_config.py
```

它只检查配置中的编号、时间和卡池名字等，不检查玩法实现、资源是否存在、活动掉落是否正确。
活动开放必须按当前 UTC 时刻判断 `Start <= now < End`，日更时间只用于每日任务周期，不能拿“上次 14 点”判断活动开关。
#39 曾修复午夜开放却等到下次日更才允许进入的问题：入口过滤、任务初始化和登录时的活动选择都应复用
`EventRunInformation.IsActive`。非日更时刻排期必须验证开始/结束边界、首次入场及登录每日任务；
配置日期、文件哈希和健康检查通过，不能代替实际判定及手机入场通过。

活动编号重复由 `client_master.load_runs` 检查。

改排期或手机表规则时，按 [README 的本地改表步骤](README.md#活动在手机上显示) 准备锁定依赖的 Python 3.12 环境，
使用该环境的 Python 运行（下例沿用 README 的 `/tmp/cm`）：

```bash
/tmp/cm/bin/python -m unittest discover -s deploy/tencent/client-master -v
```

之后仍要完整生成五个语言包，并读回新清单和数据表验证，不能只检查版本号或生成文件存在。
GitHub 的 **Check client data** 会用原版资源实际生成，出错时不进入正常部署。

如果改游戏 API，应在本地测试库针对目标活动测试入场、各代表关卡开战/结算和领奖：
普通 Boss、最高难度、EX/特殊关卡分别选例；至少覆盖一个首次通关和一个重复通关。
参考 `DragaliaAPI/DragaliaAPI.Integration.Test/Features/Event/` 和 `Features/Dungeon/`，
测试排期使用独立测试配置，不能向真实玩家接口发假通关记录。
修改 C# 后按仓库要求运行 CSharpier 和相关测试；改联机协议时再跑 [实际镜像协议检查](photon/rooms/README.md#本地验证)。
协议检查里的游戏 API 是合成替身，不能证明真实掉落、活动积分或手机体验正确。

在 `claude/` 分支提交中文 PR，相关检查通过、用户同意合并后走 **Deploy to Tencent Cloud**。
只改文档不会部署；仅排期变动会触发正常构建与部署，不需要重新搭建一套 Luxon。
核对的是腾讯云部署工作流和对应服务器状态，不要把其他上游发布工作流失败当成本次部署失败。

### 5. 核对手机实际拿到的内容

活动开放涉及三个部分：服务器配置放行、手机 master 显示入口、资源站提供对应版本。
`EventOptions` 不会直接改变手机上的菜单；只改服务端排期却没更新资源，可能完全看不到活动。

部署已处理以下步骤，后续应复用：

- 生成五语言的日期、菜单和关闭入口改动；对序列化数据做等长原地修改，不用 UnityPy 整包重写。
- 更新资源文件名、清单与加密内容，把生成版本写入 `ResourceVersionOptions.Android`；不要手填版本号。
- 通过资源缓存 8081 提供新文件，手机彻底退出重开并完成更新。
- 战货召唤的开关以**本次游戏镜像内**的 `eventSummon.json` 为准，避免手机与实际服务端奖池不一致。

用 TAT 只读核对部署镜像、服务健康、实际生效的排期和资源版本，再实际下载清单及 master 读回目标行。
手机资源地址必须走缓存；直连原资源站取不到本服新版本。
`CLOSED_EVENTS` 非空时即使没有排其他活动，也仍需生成版本；不能退回原版而恢复被关闭的入口。
单个活动页面的白框与战斗地图缺失是两件事：前者可能只影响图片，后者可能让进图一直 loading。

### 6. 按完整过程验收

1. 手机显示活动，能进入活动页、领取活动角色（如有）、打开剧情及任务。
2. 代表关卡能进图、能打完；分别验证单人和计划开放的双人路径。
3. 双人时核对两边人物、行动、通关画面，以及房主/队员退出；四人上限不等于必须四位真人。
4. 双方结算都成功，能看到正确的奖励。活动积分、奖章、材料、战货、每日奖励分别核对，首次与重复奖励分开算。
5. 用实际获得的活动货币做一次兑换、奖章领取和召唤/换箱（适用时），再验证返回准备后下一局。

每一步写明活动编号、关卡编号、测试时间和具体结果。未测试、代码推断、接口通过、手机通过分开记录。
服务器日志用 UTC，用户通常给北京时间；先换算再对齐同一轮记录。

## 数量如何核对

`record_multi` 会计算奖励、执行存档保存，再返回结果。两个 `Success` 能证明两次结算完成，
却不能单独证明掉落表正确或手机上每一种数量都对。`GrantRewards` 的批量材料没有逐项日志，不能从日志缺项推断漏发。

严格核对时，开打前先用只读查询记录双方相关库存和活动货币，打一局后保存结算画面，再查询一次：

```text
通关后数量 − 通关前数量
  = 本局入账奖励 + 同期领取的其他奖励 − 同期消耗
```

测试期间尽量不要同时升级、兑换或领取其他奖励。首通、每日奖励、能力加成、随机掉落、礼物箱发放、
上限/溢出处理都要算进去；双方掉落不要求完全相同。
`ProcessFirstMeetingRewards` 目前仍是简化实现，按同行人数发礼物，没有真正的初遇去重和总额校验；
核对时应注明这一差异，不能把现有行为写成已还原官方初遇规则。
背包前后数量和原始日志留在私有记录中，公开 issue 只写核对结果和规则，不贴账号、令牌、IP、域名或完整请求。
不能为了“验证奖励”重放真实结算请求，也不要修改玩家库存凑出预期结果。

## 出问题时从哪里查

| 现象 | 先查什么 | 本次留下的经验 |
|---|---|---|
| 活动不显示，或更新后入口没变化 | 实际资源版本、缓存地址、五语言 master 日期及入口 | 服务端放行不等于手机显示；关闭入口也要改手机表 |
| 点活动页退回首页 | 同一时段 `event_summon/get_data` 的 ResultCode、入场记录和奖池 | 客户端会自动请求召唤数据，即使玩家没有点召唤 |
| 点关卡后还没进编队就转圈 | `QuestEventMenu` 与 `QuestData` 是否一致，是否发出开战请求 | #16 只改关卡类型不够，#18 补改了菜单 |
| 开战接口成功但进图卡住 | `Scene/AreaName`、资源清单、敌人数据、与同活动重开版的差异 | #19 只跳过已删除的独眼食人王前置图；不要给所有活动套用相同地图替换 |
| 高难度开打报错或道具已扣 | 缺少的活动道具定义、生成掉落的异常 | 老活动可能没有后期货币；开战先保存再算掉落，反复重试可能继续扣道具 |
| 领奖失败 | 奖励 EntityType 与对应处理器 | 旧 `Amulet` 奖励与 `Wyrmprint` 使用同一编号，#16 已补兼容 |
| 双人建房失败 | UDP 是否有连接、加密/认证、插件、房间列表服务 | 防火墙放行与插件就绪是不同阶段，不先归咎于玩家网络 |
| 建房成功但卡加载/通讯中断 | `/Luxon/Prepare` 状态、关卡拒绝名单、角色事件与全员 Ready | 错误码 12 不能单凭数字诊断；看这次到达了哪一阶段 |
| 打完无结果 | `/Luxon/Clear`、`record_multi` 的 ResultCode、逐人结果送回 | HTTP 200 不一定是游戏成功；一次成功也不代表两位都完成 |

普通联机的正常日志顺序是：

```text
Co-op loading started: 2 player(s)
Co-op role data requested
HTTP POST /Luxon/Prepare responded 200
Co-op party allocation sent
Co-op character data sent
Co-op battle started: all players loaded
Co-op quest record requested          # 每位玩家各一次
Endpoint .../dungeon_record/record_multi responded with result code Success
HTTP POST /Luxon/Clear responded 200
Co-op quest result delivered          # 每位玩家各一份
```

并发日志顺序可能交错，要按同一房间和各次请求关联，不能只数全服务器的日志行数。
房间列表的空闲记录 15 秒续期、60 秒过期，开战后会隐藏/移除；列表为空不等于玩家已经断线。
`Client disconnected` 也不能证明报错，用户之前多次是主动退出。

## 运维与回退也沿用现有流程

查服务器只用 [`tools/tencent/tat.py`](../../tools/tencent/tat.py)，密钥读环境变量；不使用 SSH。
先只读，部署、重启、改网络或改存档按 [AGENTS.md](../../AGENTS.md) 和交接记录取得授权；改存档前备份。
日志在服务器端筛选并脱敏后再返回，避免把私服地址、身份及令牌写进公开记录。

- 镜像和手机资源准备好以后才替换游戏服务，沿用 `deploy.sh` 的备份与健康检查。
- 游戏与联机分别检查；游戏内网列表地址、UDP 地址和两种令牌的**一致性**可返回布尔结果，无需输出值。
- 回滚游戏镜像不会自动撤回仓库里的新排期、改表规则，也不会回滚联机镜像；按故障来源选择回退范围，记录两项镜像与资源版本。
- 活动日期/手机资源有问题时，通过 PR 撤回相应配置或规则并重新部署，不能只换旧游戏镜像。
- 停用联机走 Actions 的 `disable_photon`；后续普通部署会重新启用。完整撤回先停用，再撤代码/关端口。
- 保留资源缓存键、单文件挂载的原地覆盖方式和现有 compose 项目名；不要通过清缓存来碰运气。

## 每个活动复制一份验收记录

把下面内容放进活动 issue，填具体结果；完成哪一步勾哪一步。`不适用` 的项目注明理由，不算通过。

```markdown
### 活动开放记录

- 活动：名称 / 原编号 / 重开关系
- 配置时间：UTC 起止 / 北京时间起止
- 模式：单人 / 普通双人 / 特殊玩法暂不开放
- 代表关卡：普通 Boss / 高难度 / EX 等编号
- PR、提交、部署运行：
- 游戏镜像 / 联机镜像 / 安卓资源版本：

- [ ] 类型、活动货币、敌人/地图、掉落及任务奖励数据已核对
- [ ] 奖池大奖、消耗和后续箱数量已核对；无召唤时注明
- [ ] 配置检查、相关接口测试、五语言资源生成与读回通过
- [ ] 用户批准后部署成功，TAT 只读核对通过
- [ ] 手机显示、入场、剧情及活动角色正常
- [ ] 单人代表关卡通关，记录时间与关卡号
- [ ] 普通双人双方进图、行动与通关正常，记录时间与关卡号
- [ ] 双方结算 ResultCode 和各自回包均成功
- [ ] 背包/活动货币前后差额与结算一致，私有记录留存
- [ ] 活动兑换、奖章、召唤/换箱已实测
- [ ] 返回准备、再次开战和退出行为已实测

未通过项：现象 / 到达阶段 / 下一步；不要写成“全部可玩”。
```

## 提交索引：下次从这里找实现

| PR / 合并提交 | 可复用的经验和实现 |
|---|---|
| [#10](https://github.com/fenghanyue/Dawnshard/pull/10) `29456959`、[#11](https://github.com/fenghanyue/Dawnshard/pull/11) `1d87d58b` | 配置驱动排期、自动部署、先拉镜像再动服务、按 digest 拉取 |
| [#12](https://github.com/fenghanyue/Dawnshard/pull/12) `1d15b8af` | 普通卡池按原卡池日期过滤；早期 3★/4★ 上线日期仍有近似值，不能宣称完全还原 |
| [#13](https://github.com/fenghanyue/Dawnshard/pull/13) `6d3d2509` | 五语言 master、日期平移、内容寻址、清单加密和资源覆盖 |
| [#14](https://github.com/fenghanyue/Dawnshard/pull/14) `1a52ef54`、[#16](https://github.com/fenghanyue/Dawnshard/pull/16) `c2c184dc` | 空奖池保护、20401 奖池、旧货币/护符兼容、单人 Raid 转换 |
| [#18](https://github.com/fenghanyue/Dawnshard/pull/18) `f44d6b16`、[#19](https://github.com/fenghanyue/Dawnshard/pull/19) `2f3a0425` | 菜单与关卡类型一起改；区分入口卡住和缺失地图 |
| [#17](https://github.com/fenghanyue/Dawnshard/pull/17) `f181d583`、[#21](https://github.com/fenghanyue/Dawnshard/pull/21) `06d6d5e1` | TAT 固定入口、Luxon 固定版本与独立部署/回退 |
| [#25](https://github.com/fenghanyue/Dawnshard/pull/25) `8d6a837a`、[#27](https://github.com/fenghanyue/Dawnshard/pull/27) `16143e0f` | 房间快照与续期、普通关卡加载/转发、复用 C# 协议类型和实际结算 API、防止重复提交 |
| [#29](https://github.com/fenghanyue/Dawnshard/pull/29) `0c9bceaf` | 原版长期开放的入口要显式关闭，空排期也不能让入口重新出现 |

每个新活动补充自己的验收结果和必要修复；不要把 20401 的特例、模拟测试或上一场活动的通过状态复制成新活动的结论。
