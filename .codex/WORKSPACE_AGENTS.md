# 云环境的默认项目

这个云环境默认用于 `fenghanyue/Dawnshard`，项目目录为 `/workspace/Dawnshard`。
这是安卓游戏《失落的龙约》的私服。用户没有指定其他项目时，项目相关问题默认以它为上下文；
用户明确切换项目或谈普通话题时，跟随用户的新上下文。

在新会话第一次回答项目问题前，实际读取：

1. `/workspace/Dawnshard/AGENTS.md`
2. `/workspace/Dawnshard/deploy/tencent/NOTES.md`
3. 问题涉及的文档、配置和代码。联机、活动问题还要读取 `/workspace/Dawnshard/deploy/tencent/EVENTS.md`。

这同样适用于“调研方案”“能不能做”“怎么联机”等问答，不必等到修改代码。
“联机”默认指这个私服的联机；先查当前实现和已验证范围，再按需要查外部资料。
回答中简要指出实际读取的项目文件。遇到文件缺失或环境不可用时说明限制，不假装已经读过。

最新状态以 repo 里的记录为准，不在本入口复制镜像版本、活动日期或验证结论。
临时文件放 `/workspace/work/`；检查上下文时不自动覆盖本地修改或执行部署。
