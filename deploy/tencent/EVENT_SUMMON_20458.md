# 20458「忠龙之愿王之魂葬」战货奖池预览

准备日期：2026-10-25（北京时间）。此奖池用于 20458 回忆试运行；部署与手机验收状态见 [20458 整期记录](LOYALTYS_REQUIEM_20458.md)及 [PR #46](https://github.com/fenghanyue/Dawnshard/pull/46)。

这份文件说明战货召唤奖品；回忆入口、掉落、任务和奖章流程已在同一 PR 接入试运行。
手机页面是否能同时使用这些入口，仍需实机验证；不把奖池配置通过视为页面已经可用。

## 已确认规则与本服适配

| 内容 | 本次配置 | 依据 |
|---|---|---|
| 活动编号 | 20458，2022 年 10 月复刻版 | 原版 EventData |
| 消耗 | 每抽 1 战货，使用本活动的战货道具 2045801 | 原版 BoxSummonData |
| 大奖 | 佩雷，龙编号 20050103 | 原版 BoxSummonData 的重置目标 |
| 大奖份数 | 第 1～4 箱各 1 只，第 5 箱及以后没有 | 沿用本服 20401 奖池，不是 20458 完整原表 |
| 通用奖品、各箱份数 | 沿用本服 20401／20429 模板 | 20455 原始接口样本仅支持相似的第一箱结构，不能证明 20458 全部相同 |
| 属性材料 | 5 个暗属性材料格改为水属性材料 | 依据 20458 原版奖章奖励中的水属性材料进行本服适配；抽奖份数沿用模板 |

尚未取得 20458 当年完整奖池，因此这是一份有来源说明的本服配置，不标成“原版完整还原”。
也没有沿用 2020 年 Wiki 的不同箱量作为 2022 年依据。

## 每箱规模

| 箱数 | 有奖品的格数 | 抽空需要次数 | 抽空需要战货 | 佩雷 | 龙辉晶 |
|---|---:|---:|---:|---:|---:|
| 第 1 箱 | 31 | 535 | 535 | 1 只 | 25 |
| 第 2 箱 | 31 | 664 | 664 | 1 只 | 25 |
| 第 3 箱 | 31 | 819 | 819 | 1 只 | 25 |
| 第 4 箱 | 31 | 931 | 931 | 1 只 | 25 |
| 第 5 箱及以后，每箱 | 28 | 1120 | 1120 | 无 | 无 |

前四箱各有 1 份 Twinkling Sand；第 5 箱起该奖品、佩雷及龙辉晶均为 0 份。
抽空前四箱合计消耗 2949 战货，获得佩雷 4 只、龙辉晶 100、Twinkling Sand 4 份以及表中的其他奖品。
提前换箱会放弃该箱未抽到的剩余奖品。

## 完整奖品表

“份数”表示该奖品格在一箱内可被抽中多少次；实际获得总量 = 每次数量 × 抽中份数。
中文名称为便于阅读的说明，未经原版中文 TextLabel 核对；按物品编号和英文名识别。
配置仍保留 31 条奖品格，第 5 箱以后其中 3 条的份数为 0。

| 奖品说明 | 英文名 | 每次数量 | 第1箱份数 | 第2箱份数 | 第3箱份数 | 第4箱份数 | 第5箱起份数 |
|---|---|---:|---:|---:|---:|---:|---:|
| 佩雷 | Pele | 1 | 1 | 1 | 1 | 1 | 0 |
| 武器制作砂 | Twinkling Sand | 1 | 1 | 1 | 1 | 1 | 0 |
| 龙辉晶 | Wyrmite | 25 | 1 | 1 | 1 | 1 | 0 |
| 魔力（大份） | Mana | 5000 | 5 | 5 | 5 | 5 | 5 |
| 魔力（小份） | Mana | 200 | 50 | 60 | 70 | 80 | 120 |
| 卢比 | Rupies | 10000 | 50 | 60 | 70 | 80 | 120 |
| 红色核心素材 | Crimson Core | 1 | 8 | 8 | 9 | 10 | 10 |
| 陨石 | Meteorite | 1 | 9 | 10 | 14 | 17 | 19 |
| 魔兽眼素材 | Fiend's Eye | 1 | 9 | 10 | 14 | 17 | 19 |
| 妖翼素材 | Bewitching Wings | 1 | 9 | 10 | 14 | 17 | 19 |
| 水龙高阶鳞片 | Waterwyrm's Glistscale | 1 | 1 | 1 | 1 | 1 | 1 |
| 水龙鳞片 | Waterwyrm's Scale | 1 | 5 | 5 | 5 | 5 | 5 |
| 水属性高阶宝珠 | Deluge Orb | 1 | 2 | 2 | 2 | 2 | 2 |
| 水属性中阶宝珠 | Stream Orb | 1 | 8 | 8 | 8 | 8 | 8 |
| 水属性基础宝珠 | Water Orb | 1 | 20 | 30 | 30 | 30 | 30 |
| 轻金属 | Light Metal | 1 | 20 | 24 | 27 | 30 | 34 |
| 花岗岩 | Granite | 1 | 20 | 24 | 27 | 30 | 34 |
| 铁矿石 | Iron Ore | 1 | 30 | 40 | 50 | 60 | 70 |
| 魔兽角素材 | Fiend's Horn | 1 | 20 | 24 | 27 | 30 | 34 |
| 魔兽爪素材 | Fiend's Claw | 1 | 30 | 40 | 50 | 60 | 70 |
| 古鸟羽毛素材 | Ancient Bird's Feather | 1 | 20 | 24 | 27 | 30 | 34 |
| 蝙蝠翼素材 | Bat's Wing | 1 | 30 | 40 | 50 | 60 | 70 |
| 虚空战随机材料 | Void Battle Item | 3 | 50 | 60 | 70 | 80 | 100 |
| 金级经验水晶 | Gold Crystal | 1 | 13 | 13 | 13 | 13 | 13 |
| 银级经验水晶 | Silver Crystal | 1 | 15 | 20 | 30 | 30 | 30 |
| 铜级经验水晶 | Bronze Crystal | 1 | 20 | 25 | 40 | 45 | 55 |
| 高级龙果 | Succulent Dragonfruit | 1 | 13 | 13 | 13 | 13 | 13 |
| 中级龙果 | Ripe Dragonfruit | 1 | 15 | 20 | 30 | 30 | 30 |
| 基础龙果 | Dragonfruit | 1 | 20 | 25 | 40 | 45 | 55 |
| 铜级磨刀石 | Bronze Whetstone | 1 | 20 | 30 | 40 | 50 | 60 |
| 基础圣水 | Holy Water | 1 | 20 | 30 | 40 | 50 | 60 |
| **合计抽数** | | | **535** | **664** | **819** | **931** | **1120** |

“虚空战随机材料”沿用现有 TwoStepId=6：每次随机给一种虚空材料 3 件；
表内份数是抽中次数，故第一箱 50 份对应随机材料合计 150 件。

## 换箱与保存规则

- 第 1～4 箱：按现有服务端逻辑，抽到该箱佩雷后即可提前换箱；抽空也可换箱。
- 第 5 箱及以后：现有服务端要求抽空才能换箱，每箱使用相同的 FinalCount 配置。
- 重登后沿用已保存的箱号、战货及各奖品抽取次数，不重新赠送大奖。

以上是现有 EventSummonLogic / EventSummonService 的代码规则；本次没有修改抽奖算法。
此处不表示手机已经可以进入 20458、进行抽奖或换箱。

## 修改位置与核对边界

- 配置：`DragaliaAPI/DragaliaAPI/Resources/eventSummon.json`，新增 EventId=20458；原有三个活动配置保持不变。
- 说明：`deploy/tencent/EVENT_SUMMON_20458.md`，记录来源、箱量和待验证事项。
- 奖池准备时做了配置和数据一致性检查；后续整期工作已加入本地接口测试，不执行线上抽奖、不改玩家存档、不部署。
- JSON Schema、重复编号、物品存在性、大奖与战货引用、各箱总量、预览表一致性检查均通过；原有三个活动奖池未变。
- 已按奖品内容核对原始 20455 第一箱响应：源模板的 30 项非大奖奖品及份数一致。样本与配置的奖品格编号不同，因此不按格号直接比较。
- `python3 -B deploy/tencent/check_config.py` 和 `git diff --check` 通过。
- 后续已找到 .NET SDK 并使用独立 PostgreSQL／Redis 测试实际游戏接口；抽箱发放佩雷、扣除 1 战货和换箱已通过本地集成测试。
- 手机实际验证尚未执行；接口测试不能证明回忆页面有召唤按钮。

## 资料来源

1. [原版 BoxSummonData](https://github.com/fenghanyue/Dawnshard/blob/70b9c245ce42b402f279c2782be536405e9f92e2/DragaliaAPI/DragaliaAPI.Shared/Resources/Event/BoxSummonData.json)：20458 的战货消耗、道具和佩雷重置目标。
2. [现有奖池模板](https://github.com/fenghanyue/Dawnshard/blob/70b9c245ce42b402f279c2782be536405e9f92e2/DragaliaAPI/DragaliaAPI/Resources/eventSummon.json)：20401 的佩雷奖池及其源模板 20429。
3. [20458 原版奖章奖励](https://github.com/fenghanyue/Dawnshard/blob/70b9c245ce42b402f279c2782be536405e9f92e2/DragaliaAPI/DragaliaAPI.Shared/Resources/Event/RaidEventReward.json)：水属性材料编号，作为本服属性适配依据。
4. [原始游戏接口样本（20455，第 1 箱）](https://github.com/SapiensAnatis/dragalia-api-docs/blob/main/docs/source/dragalia/event_summon_get_data.rst)：2022 年游戏响应，535 份、31 格；不是 20458 的样本。
5. [换箱逻辑](https://github.com/fenghanyue/Dawnshard/blob/70b9c245ce42b402f279c2782be536405e9f92e2/DragaliaAPI/DragaliaAPI/Features/Event/Summon/EventSummonLogic.cs) 与 [随机虚空材料生成器](https://github.com/fenghanyue/Dawnshard/blob/70b9c245ce42b402f279c2782be536405e9f92e2/DragaliaAPI/DragaliaAPI/Features/Event/Summon/VoidBattleItemGenerator.cs)。
