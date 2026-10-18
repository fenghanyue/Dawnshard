# 腾讯云私服部署

这个目录让 fork 和腾讯云上的服务器（轻量应用服务器，上海）联动：

```
推代码到 main ──► GitHub Actions 编译镜像 ──► 推到 ghcr.io/fenghanyue/dragalia-api
                                          └─► 腾讯云「自动化助手」在服务器上执行 deploy.sh
```

| 文件 | 作用 |
|---|---|
| `docker-compose.yml` | 服务器上的容器配置（游戏服务器 + 数据库 + redis） |
| `appsettings.Production.json` | 私服配置，比如开哪些活动（`EventOptions`） |
| `deploy.sh` | 在服务器上执行：备份数据库 → 换配置 → 拉镜像 → 重建游戏服务器 → 健康检查，没通过就回滚 |
| `tat_run.py` | 调腾讯云接口，把 `deploy.sh` 发到服务器执行并等结果 |
| `../../.github/workflows/tencent-deploy.yaml` | 自动部署的流程 |

数据库密码、`DEVELOPER_TOKEN` 只在服务器的 `/opt/dawnshard/.env` 里，不进仓库。
资源缓存（8081 端口的 nginx）在服务器的 `/opt/asset-cache`，不归这里管。

## 第一次要做的设置

1. GitHub 仓库 → Settings → Secrets and variables → Actions → New repository secret，建两个：
   - `TENCENTCLOUD_SECRET_ID`
   - `TENCENTCLOUD_SECRET_KEY`
2. 打开仓库的 Actions 页，点 "I understand my workflows, go ahead and enable them"。
3. 原作者的 "Publish DragaliaAPI"、"Publish PhotonStateManager"、"Publish Website" 会因为没权限失败，
   可以在 Actions 页点进去 → 右上角 "..." → Disable workflow。

## 日常怎么用

- **改活动安排**：编辑 `appsettings.Production.json` 里的 `EventOptions.EventList`，每个活动写
  `Id`（活动编号）、`Start`、`End`（UTC 时间）。合并到 main 后自动部署。
- **改代码**：改完合并到 main，自动编译、部署。
- **手动部署 / 回滚**：Actions → Deploy to Tencent Cloud → Run workflow。
  留空会编译 main 当前的代码再部署；填以前的镜像（比如 `ghcr.io/fenghanyue/dragalia-api:sha-1234567`）就直接换回那个版本。
- **从自己电脑部署**（需要设置好上面两个环境变量）：

  ```bash
  pip install tencentcloud-sdk-python-common tencentcloud-sdk-python-tat
  python3 deploy/tencent/tat_run.py --image ghcr.io/sapiensanatis/dragalia-api:4.0.204
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
