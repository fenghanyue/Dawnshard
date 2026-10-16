# 任务：在这台 Windows 电脑上部署 Dawnshard 私服（双人自用）

## 背景

Dawnshard（https://github.com/SapiensAnatis/Dawnshard）是手游《失落的龙约 / Dragalia Lost》停服后的开源私服模拟器（.NET 10 / C# / PostgreSQL / Redis / .NET Aspire）。我们的目标：**两个人自用的小型私服**。整体分四个阶段：

- Phase 0：装基础环境（今天做）
- Phase 1：跑通主服务（今天做）
- Phase 2：手机连上，验证单人内容（今天做）
- Phase 3：Photon 联机（co-op）（**今天先不做**，等 Phase 2 验证通过后另行发起）

这台 Windows 电脑的角色是**服务器**（后期还会跑 Photon Server）。所有代码和运行数据都放在 **E:\DawnshardWorkspace**；Git / .NET SDK / Docker 这类系统级软件 Windows 要求装 C 盘，这是正常的，不用迂回。

## 第一步：安装基础环境（先检查，已存在就跳过）

1. 用 winget 安装（没有 winget 就先在 Microsoft Store 装"应用安装程序"）：
   - `winget install Git.Git`
   - `winget install Microsoft.DotNet.SDK.10`
   - `winget install Docker.DockerDesktop`
2. 版本要求：仓库 `global.json` 锁定 .NET SDK 10.0.100（rollForward=latestFeature），装好后用 `dotnet --list-sdks` 确认有 10.x
3. 启动 Docker Desktop 并等引擎就绪（`docker info` 成功）。若提示需要 WSL2 / 重启电脑：重启后继续。可用 Computer Use 处理 Docker Desktop 的首次弹窗
4. 国内网络慢时可用常用镜像/加速，但完成后要告诉我你改了什么

## 第二步：获取源码并配置

1. `git clone https://github.com/SapiensAnatis/Dawnshard.git E:\DawnshardWorkspace\Dawnshard`
2. **先读仓库根目录的 AGENTS.md**，了解仓库结构和约定
3. ⚠️ **不要执行 `dotnet build DragaliaAPI.slnx`（整库构建会失败）**：解决方案里的 PhotonPlugin 项目引用 `PhotonHivePlugin.dll`（Photon Server SDK 里的专有文件，还没下载），这是预期内的失败，不是环境问题
4. 编辑 `Aspire/Dawnshard.AppHost/appsettings.json`，把 `"EnableWebsite"` 改为 `false`（Website 需要 Node.js + pnpm，今天先不装）

## 第三步：启动主服务

1. 确认 Docker Desktop 在运行
2. `cd E:\DawnshardWorkspace\Dawnshard\Aspire\Dawnshard.AppHost && dotnet run --launch-profile http`
   - 首次运行要还原 NuGet 包并构建，约几分钟，正常
   - 如果报 Aspire workload 相关错误，执行 `dotnet workload install aspire` 后重试
3. 成功标志：浏览器自动打开 Aspire 仪表盘，`dragalia-api`、`postgres`、`redis` 三个资源全部 Running
4. 从仪表盘或控制台输出里记录 `dragalia-api` 的 http 端点完整 URL；用 `ipconfig` 记录本机局域网 IP
5. 自行验证：对该 http 端点发起一次请求确认服务有响应（404/403 也算服务活着，连接拒绝才是失败）

## 第四步：停下来，给手机连接指引（Phase 2）

到这一步先暂停自动操作，把下面信息整理给我：

1. 手机和电脑连同一个 Wi-Fi
2. 手机安装 Dragalipatch（https://github.com/LukeFZ/DragaliPatch/releases/latest），服务器地址填：`http://<局域网IP>:<API端口>`（**必须带 http:// 前缀**，端口用 dragalia-api 的 http 端点端口）
3. Windows 防火墙放行该端口（专用网络）；如果我用 Computer Use，可以帮我点掉防火墙弹窗
4. **等我手机上完成账号注册并进入游戏主界面**，Phase 2 才算完成。卡住了就把现象发给你诊断

诊断提示：账号注册默认走公共认证服务 `baas.lukefz.xyz`，如果手机端卡在登录/注册，先用这台电脑 curl 测试它的可达性；确认不可达的话，备选方案是本地自托管 DragaliaBaas（https://github.com/DragaliaLostRevival/DragaliaBaasServer），**先报告再动手**。

## 暂时不要做（Phase 3 联机，仅作背景了解）

Photon 联机部分等 Phase 2 通过后我再发起新任务。你只需要知道：

- 联机依赖 Photon Server v4（仅 Windows 版），自托管免费 license 支持 100 CCU，两人够用；SDK 和 license 要登录 dashboard.photonengine.com/selfhosted 手动下载（涉及我的账号，需要我配合）
- 仓库说明在 `PhotonPlugin/README.md`；SDK 解压后必须放在仓库根目录 `PhotonServer\`（`src-server\Plugins\lib\net461\PhotonHivePlugin.dll` 和 `deploy\bin_Win64\` 是关键路径）

## 工作方式要求

- 一步一步来，每完成一步汇报结果和证据（命令输出/截图）再继续
- 遇到报错：贴完整错误信息，先自己排查修复；修不了就停下问我，不要瞎猜着改
- 不要 `git commit` / `git push`；不要修改业务代码；配置改动仅限本机部署必需项，且逐条告诉我
- 全程用中文回复
