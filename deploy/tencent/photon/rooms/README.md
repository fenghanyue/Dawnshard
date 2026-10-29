# Luxon 普通关卡联机适配

适配普通关卡的房间准备、加载、行动消息转发和通关接口。
2026-10-20 用户已确认普通关卡双人建房到打完正常，服务端两次结算均成功；所有物品数量和返回准备续战仍待进一步验收。
活动 Raid 单人转换和缺失地图修复保持原样。
这一版不支持大型 Raid、RandomMatching 特殊玩法和计时排行，准备接口会明确拒绝这些关卡。
活动中的普通 Boss 与大型 Raid 要按关卡编号/类型区分，新活动检查与验收见 [复用指南](../../EVENTS.md)。

## 固定来源与实现选择

- LuxonServer v1.5：`e4bab98d8d6b3aee9ecf4035309a78a59b584f93`。
- Luxon 协议子模块：`81458d75b65df0ace14651d61b5c922f56da3628`。
- tracy 子模块：`05cceee0df3b8d7c6fa87e9638af311dbabc63cb`。
- LZ4 v1.10.0：`ebb370ca83af193212df4dcbadcc5d87bc0de2f0`。

选择 C++ 原生插件接口，避免首个房间阶段同时引入 .NET FFI 的生命周期和数据转换问题。
这不是原 C# DLL：房间逻辑参照本仓库 `GameLogicPlugin`、`DtoHelpers`、`StateManagerPlugin`，逐项适配。
保留 Luxon 及编译依赖的许可证；不改动或向上游提交生成的代码。

## 行为

- 注册名称为 `GluonPlugin`。创建时返回七位房间号，并从 `PlayerId` 初始化原 Photon 类型的 `ViewerId`。
- 最多四人；拒绝重复账号、无房主房间和非房主修改房间属性。
- Luxon v1.5 的 `OnJoinGame` 在 actor properties 写入前执行，插件直接读取请求；拒绝加入时释放 Master 预留的席位。
- 读取普通 MessagePack / MessagePack-CSharp Lz4Block 格式的入室条件，保留战力、元素、武器和目标筛选信息。
- 后台单线程发送完整房间快照，不阻塞 UDP 主循环。失败时一秒后重试最新快照，不重放可能过时的进出增量。
- 已成功发送的快照每 15 秒续期，Redis 中的有效期为 60 秒；服务异常退出后过期清理。原 Photon webhook 有效期不变。
- 房主退出时发送 MessagePack 格式的 `RoomBroken(HostDisconnected)`，禁止再加入，并删除列表记录。
- 房主设置加载阶段 1 后，发布 `GoToIngameInfo`，隐藏房间并禁止中途加入；阶段 2 读取角色，阶段 3 发布 `Party`，阶段 4 发布 `CharacterData`。
- 只有所有手机发出 `Ready` 才广播 `StartQuest`，重复 Ready 不重复开战；正常行动消息保留 Photon 的接收范围和可靠/不可靠传输设置。
- PhotonStateManager 的私有 `/Luxon/Prepare` 使用原 C# 插件的 MessagePack 类型生成角色事件，普通双人各控制两名角色；电脑角色不足时按实际可用数量分配。
- `/Luxon/Clear` 调用现有 `dungeon_record/record_multi`，由游戏 API 计算实际奖励；同行玩家、房主标记、使用人数从服务端状态补入，结果仅发给对应玩家。
- 同一玩家结算中忽略重复请求，成功后只重发缓存结果；结算失败不自动重试，不伪造成功回复。HTTP 在两个后台工作线程执行，回包由 UDP 主线程处理，不跨线程访问房间。
- 处理超时/全灭失败回包；所有现存玩家结束后，房主 `GameSucceed` 可恢复准备并更新房间号。加载中有人退出则关闭本轮，避免用过期角色名单开战。
- 游戏 API 令牌从服务器现有配置取得，优先采用 `.env` 中生效的 `PhotonOptions__Token`；没有现有令牌才生成。独立服务共享令牌与游戏 API 令牌分开保存，均不输出到日志，不修改 `.env`。
- 日志只记操作和人数，不记共享令牌、请求内容或服务地址。

## 真风黑屏诊断（2026-10-29）

只为真风联机关卡 `210010101–210010104` 增加 `Co-op trace` 日志，不调整现有开战、转发或属性拒绝策略。
记录双方的房间内序号、加载阶段、事件编号、传输通道/方式、候选接收人数，以及属性更新的字段名和准许/拒绝原因。
字段名仅允许最多 48 个 ASCII 字母、数字或下划线，每次最多 12 个；其他键统一显示 `<other>`。
不输出属性值、事件内容、账号、角色配置或令牌。每房间最多 160 条明细，各事件/结果只记录前两次。
另记录开战广播提交、开战后约 5 秒/15 秒及离房时的计数和成员阶段；采样任务不跨越下一轮战斗。

`relay` 表示插件交给 Luxon 继续处理；`eligible` 是当时符合接收条件的连接数量。
`server-broadcast-submitted` 只表示广播函数已调用，不能证明手机收到、处理完成或战斗画面正常。
`cancel` 包括由服务器消费的控制事件及被拒事件；结合事件编号、既有开战/结算日志判断，不能一律当作错误。
明细达到上限后仍保留检查点，避免逐帧记录行动消息。

复现后通过 TAT 查询同一时段游戏和联机日志，重点核对开战后的事件流、候选接收人数及
`room-update-during-battle` 是否出现；当前尚未确认黑屏根因。

## 本地验证

先构建生产镜像及带模拟客户端的验证镜像，再运行独立网络中的 Redis 8 和生产服务：

```bash
docker build -f deploy/tencent/photon/Dockerfile.rooms --target final -t dawnshard-luxon:rooms .
docker build -f deploy/tencent/photon/Dockerfile.rooms --target verification -t dawnshard-luxon:checks .
bash deploy/tencent/photon/smoke-rooms.sh dawnshard-luxon:rooms dawnshard-luxon:checks
```

验证客户端使用实际 Photon 1.6 协议，依次执行 UDP 连接、加密、Master/Game 切换、建房、入室、重复账号/第五人拒绝、公开转凭编号、退出和房主关闭。
房间停留 65 秒以验证心跳确实延长 60 秒租期。容器同时检查非 root、只读文件系统、两个进程的健康状态。
随后由两名模拟玩家验证加载屏障、角色字段、可靠/不可靠行动转发、仅本人可见的结算回包、重复结算、返回准备和接口故障。
生产镜像运行真正的 Luxon 和 PhotonStateManager，游戏 API 使用独立的合成测试替身；Python 和测试数据只存在验证镜像。
这不能代替安卓客户端实际进图、战斗和奖励入账的验证。

仅开发 C++ 时也可使用本机工具链（C++23、CMake 3.25+、libcurl 开发包）：

```bash
bash deploy/tencent/photon/rooms/fetch-source.sh work/luxon-upstream
cmake -S deploy/tencent/photon/rooms -B work/rooms-build -DCMAKE_BUILD_TYPE=Release -DLUXON_SOURCE_DIR="$PWD/work/luxon-upstream"
cmake --build work/rooms-build -j 2
ctest --test-dir work/rooms-build --output-on-failure
```

`room_udp_test` 默认嵌入本地 Luxon，需另启 PhotonStateManager，并设置 `LUXON_STATE_URL` 为本机地址及 `LUXON_STATE_TOKEN_FILE` 为令牌文件。
完整容器验证脚本不依赖本机 .NET 或 C++ 工具链，不连接真实私服。
