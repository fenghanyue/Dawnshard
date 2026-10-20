# Luxon 房间准备适配

本阶段目标是普通联机关卡的准备界面，未实现战斗、结算、自动匹配和房间续战。
客户端设置 `GoToIngameState > 0` 会被拒绝，战斗事件不转发。活动讨伐战单人转换和缺失地图修复保持原样。

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
- 日志只记操作和人数，不记共享令牌、请求内容或服务地址。

## 本地验证

先构建生产镜像及带模拟客户端的验证镜像，再运行独立网络中的 Redis 8 和生产服务：

```bash
docker build -f deploy/tencent/photon/Dockerfile.rooms --target final -t dawnshard-luxon:rooms .
docker build -f deploy/tencent/photon/Dockerfile.rooms --target verification -t dawnshard-luxon:checks .
bash deploy/tencent/photon/smoke-rooms.sh dawnshard-luxon:rooms dawnshard-luxon:checks
```

验证客户端使用实际 Photon 1.6 协议，依次执行 UDP 连接、加密、Master/Game 切换、建房、入室、重复账号/第五人拒绝、公开转凭编号、退出和房主关闭。
房间停留 65 秒以验证心跳确实延长 60 秒租期。容器同时检查非 root、只读文件系统、两个进程的健康状态。
这不能代替安卓客户端对准备界面和人物信息的验证。

仅开发 C++ 时也可使用本机工具链（C++23、CMake 3.25+、libcurl 开发包）：

```bash
bash deploy/tencent/photon/rooms/fetch-source.sh work/luxon-upstream
cmake -S deploy/tencent/photon/rooms -B work/rooms-build -DCMAKE_BUILD_TYPE=Release -DLUXON_SOURCE_DIR="$PWD/work/luxon-upstream"
cmake --build work/rooms-build -j 2
ctest --test-dir work/rooms-build --output-on-failure
```

`room_udp_test` 默认嵌入本地 Luxon，需另启 PhotonStateManager，并设置 `LUXON_STATE_URL` 为本机地址及 `LUXON_STATE_TOKEN_FILE` 为令牌文件。
完整容器验证脚本不依赖本机 .NET 或 C++ 工具链，不连接真实私服。
