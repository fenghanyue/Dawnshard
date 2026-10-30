# 腾讯云游客认证

## 目标与范围

游客继续使用手机已有的设备凭据，不要求注册。新认证服务独立运行，游戏 API 通过内网查询它的公钥，验证自己的登录凭证；过渡期也验证原公共认证凭证。

首次迁移时，将手机保留的设备凭据交给原公共认证服务核验一次，并验证回复的签名、发行方、用途和有效期。
确认回复中的用户编号与签名凭证、手机提供的旧编号一致后，再登记到本地。游戏仍按同一个账号编号寻找原存档。
设备编号、昵称、玩家编号或未经验证的旧用户编号本身不能认领存档。

原服务的 `RandomExtensions.NextString` 为设备编号和账号编号使用同一套字符：大小写字母、数字及 `!$+-`，长度均为 16。迁移校验必须接受这套完整字符，不能只接受字母数字；来源为 NOTICE 固定版本的 `DragaliaBaasServer/Extensions/RandomExtensions.cs`。签名、发行方、用途、有效期和旧编号一致性检查仍全部执行。

已登记设备的后续登录只验证本地密码散列，原公共认证服务不可用时也能发证。原认证返回失败、超时、伪造或过期凭证时不登记、更不另建替代身份。
数据库事务和按设备加锁防止重试、并发请求重复登记。签名密钥持久保存，已有账号但密钥丢失时启动失败，要求恢复备份。

初次上线默认 `AllowNewGuests=false`。如果换地址后 SDK 没有带回原游客凭据，停止迁移，不发新身份，不自动进入新号。
两台现有手机完成迁移后，是否开放新游客、换机找回或网页账号功能另按实际需求处理。公共网页账号库和密码不在本服，本次提供游客 SDK 登录，不复制它们。

本服务的协议参考与授权见 [GuestAuth/NOTICE.md](../../GuestAuth/NOTICE.md)。服务是独立可执行文件，通过 HTTP 与游戏 API 通信；其源代码遵循 GPL-3.0，目录内保留原授权。

## 入口与容器

- 游戏、游客认证和 HTTPS 入口使用同一发布镜像的三个独立容器；认证和入口非 root、只读文件系统、移除额外权限。
- 认证容器不发布端口。PostgreSQL 中使用专用数据库、专用账号，不能读取游戏的玩家表。
- HTTPS 入口使用固定版本 Caddy 2.11.7。镜像构建移除二进制的低端口权限属性，容器监听 8080/8443，由 Docker 映射至公网 80/443。
- HTTPS 凭证由 Let's Encrypt 自动申请、续期；具体域名、地址只保存在服务器私有配置中，不写公开仓库或 CI 日志。
- 游戏 API 仅信任实际入口容器的转发地址，拒绝其他来源伪造 HTTPS 状态。长期设备凭据不接受经公网 HTTP 入口提交。
- HTTPS 的 `/dragalipatch/config` 才会公布本地认证模式。旧 HTTP 地址保留公共认证模式，旧安装包继续可用。
- 内网认证、公钥请求直接访问 Docker 服务；公共认证的首次验证仍使用正常的外部网络与证书校验。

持久数据位于 `/opt/dawnshard/guest-auth/`：

| 路径 | 内容 |
|---|---|
| `private/edge.env` | 已确认的 HTTPS 主机名，由运维准备，禁止进 Git |
| `private/runtime.json` | 专用数据库连接配置 |
| `private/api.json` | 认证启用状态、实际入口容器地址、补丁模式；可提供现有 IP:8081 资源地址 |
| `private/last-rollout.json` | 本次切换前的镜像及入口配置，用于部署失败回退 |
| `data/signing-key.pem` | 本服签名私钥，禁止输出 |
| `caddy/` | 证书、续期账号及私钥，禁止输出 |

数据库名、角色名均为 `dawnshard_guest_auth`。私有文件和数据目录仅供 root/容器用户读取。
准备脚本先备份已有认证数据库、私有配置、密钥和证书；首次初始化后另保留初始密钥及数据库备份。

## 发布和验证

仍由 GitHub Actions 的 **Deploy to Tencent Cloud** 发布。镜像同时携带游戏、游客服务和 Caddy 二进制，不要求服务器另从 Docker Hub 拉 Caddy。
准备阶段检查专用数据库和认证服务健康；存在 HTTPS 主机配置时启动入口，并用系统信任库实际验证 HTTPS `/health`。
证书验证成功后才生成本地补丁配置，再切换游戏镜像。任一步失败，保留或恢复此前的认证容器、补丁配置与游戏版本；游客记录和密钥不做破坏性回退。

新增部署文件后，原 TAT 发送体积接近限制。`tat_run.encode_script` 先压缩完整脚本，再在服务器解压到匿名内存文件交给 bash，关闭其 stdin，避免内部命令误读后续脚本。保持原 stdout、退出码和 60 KiB 检查，不把脚本密钥放进命令行参数或写入额外磁盘文件。

必要检查：

```bash
dotnet test --project GuestAuth.Tests/GuestAuth.Tests.csproj -c Release
dotnet test --project DragaliaAPI/DragaliaAPI.Integration.Test/DragaliaAPI.Integration.Test.csproj -c Release --filter-class 'DragaliaAPI.Integration.Test.Features.Login.*'
python3 -m unittest discover -s deploy/tencent -p 'test_*.py' -v
docker build -f DragaliaAPI/DragaliaAPI/Dockerfile -t guest-auth:checks .
python3 -m pip install msgpack==1.1.2
python3 deploy/tencent/guest-auth/smoke.py guest-auth:checks
```

SDK 单独检查使用真实 PostgreSQL、合成的原认证响应，覆盖签名、有效期、用途、旧编号、错误密码、并发、重启及迁移期间禁止新号。
镜像检查另启动隔离 PostgreSQL、Redis、认证、游戏与真实 TLS 入口；测试 CA 仅用于本地检查，生产使用受信任公开证书。验证真实 MessagePack 登录、同一玩家编号、无重复存档、认证重启和数据库隔离。
这些结果不能代替手机验证，也不代表已迁移真实游客。

## 手机切换

1. 先保存旧补丁 APK；它不包含游客凭据或已下载资源，不等于手机数据备份。
2. 保留现有游戏和数据，用旧游戏确认玩家编号。无需上传完整 APK。
3. 使用原官方 Dragalipatch 工具，Server Address 填已确认的 HTTPS 入口。CDN 继续使用原 IP:8081 资源地址；HTTPS 补丁配置提供该地址时可留空由工具读取。
4. 生成新包后检查是否可以覆盖更新；出现签名冲突、另装新应用或必须卸载时暂停，不卸载、不清数据。
5. 更新后仍选择游客。核对原玩家编号、角色、库存和资源是否沿用，再退出重进。若要求重下整包则暂停。
6. TAT 核对本地游客登记与原存档对应，确认确实完成本地认证；不能仅凭能进入游戏认定迁移完成，因为过渡期仍接受原公共凭证。

资源版本由活动、奖池与改表输入决定，认证配置不参与计算。本次保持资源版本和原资源缓存地址；手机缓存保留仍需实际覆盖更新验证。

## 回退

部署过程中认证准备失败会还原原认证容器和入口配置。游戏健康失败时也执行认证回退，再重建旧游戏版本。回退保留身份数据、签名密钥和证书，不恢复整库覆盖新进度。

如果手机已经更新并迁移，不要直接换成不支持本地认证的旧游戏镜像。先使用保留的旧 APK 覆盖更新、确认原公共认证和原存档可用，再决定服务端回退；或者保留支持两种凭证的兼容版本。资源缓存和游客数据不能通过卸载来处理。

## 当前验收边界

开发、CI、实际部署与手机结果分别记录在 [NOTES.md](NOTES.md)。手机未验证前，不记作游客迁移或资源保留已通过。
