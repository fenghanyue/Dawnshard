# 客户端活动资源生成

按服务器的活动安排，改手机端游戏数据表（master）里的活动日期，生成新的资源清单和资源版本号。

游戏里显示哪些活动，看的是手机从资源站下载的数据表里写的日期（还是当年的日期）；服务器的
EventOptions 只管放不放行。所以要把数据表里的日期改成服务器排的时间，再让手机重新下载：

1. 下载原版的资源清单和 5 个语言包的数据表，按写死的校验值核对，存在 --cache-dir 里，下次不用再下；
2. 把 appsettings.Production.json 的 EventOptions.EventList 里排的活动，日期改成服务器的时间
   （规则见 EventSchedule），活动里的讨伐战和菜单入口改成单人（见 SOLO_RAID_FIELDS、SOLO_RAID_MENU_FIELDS）。只换成一样长的值
   （日期一样长，数字本来就是定长的）；20458 回忆试运行另增任务行和本期图片别名，
   扩容仅更新指定对象及文件偏移，其余对象原始字节保留；
3. 改过的数据表按内容重新取文件名，更新资源清单里这个文件的名字和大小，重新加密清单；
4. 写到 --out-dir：dl/manifests/Android/<新版本号>/ 下是清单，dl/assetbundles/Android/xx/ 下是数据表。
   资源缓存（../asset-cache/default.conf）先从这里找文件，找不到再去资源站。

新版本号由输入算出来：活动安排、活动召唤配置、这个脚本和依赖都没变，版本号就不变，生成过的直接跳过。
没排活动且没有要关闭的原版活动时，直接用原版的版本号。标准输出只有最后一行的版本号，其他信息都在标准错误里。

用法（部署时 deploy.sh 会自动跑）：

```bash
    python3 client_master.py --events appsettings.Production.json --event-summons eventSummon.json \\
        --cache-dir /opt/dawnshard/client-master/cache --out-dir /opt/asset-cache/overrides
```

20458 回忆小卡底部的固定活动字样使用局部无文字补片修正，角色状态仍由客户端绘制。
补片来自图像修复结果，仅覆盖 x=7..96、y=102..124；原活动标题、佩雷及透明通道保留。

## UnityFS 与加密清单

读写游戏的资源包（Unity 的 UnityFS 格式）和加密的资源清单，给 client_master.py 用。

默认只做等长原地替换；回忆试运行可显式扩容指定对象，同时更新对象/文件目录，
其余对象和纹理原始字节保留；20458 的上述局部补片除外。未变的压缩块照抄。资源清单用 Rijndael（256 位分组）加密，密钥是原作者的改表工具
SapiensAnatis/DragaliaModTools 里公开的那一把。


### 指定对象扩容

只扩容指定对象；复制其余原始字节，更新对象偏移、长度和单文件目录。

限定本项目的单一 serialized file、little-endian v21 格式；可附带原始纹理流文件。
不重写类型树或其他对象，纹理流文件只更新目录偏移。

`patch_assets()` 按名称修改 MonoBehaviour；编辑函数返回变更数。默认等长写回，只有 `resizable` 显式列出的对象允许扩容。
