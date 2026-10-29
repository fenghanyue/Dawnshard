# 新会话的默认项目入口

本云环境的会话起始目录曾为 `/workspace`，repo 位于 `/workspace/Dawnshard`。
repo 中的记录已经提交，不代表每个新会话都已自动读取；环境安装脚本执行过 `cd`，也不等于后续会话的工作目录会跟着改变。

## 会话入口配置

使用支持项目选择或工作目录设置的客户端时，将项目设为 `/workspace/Dawnshard`。
repo 的 [AGENTS.md](../AGENTS.md) 要求问答、调研和改代码都先读取交接记录及相关实现。

对当前网页或桌面入口，建议每个 repo 建一个 ChatGPT 项目。在项目右上角的 `…` 菜单进入
`Project settings`，将下面的内容加入项目指令。项目指令随该项目里的新对话使用，避免把某个 repo 的上下文写进所有聊天的全局指令。
之后从这个项目里面新建对话，再在 `Work in > Cloud` 选择对应云环境；项目指令不会自动提供对云端文件的访问。

```text
这个云环境的默认项目是 /workspace/Dawnshard（fenghanyue/Dawnshard，安卓《失落的龙约》私服）。
每个新会话第一次回答本项目相关问题前，先实际读取 /workspace/Dawnshard/AGENTS.md 和
/workspace/Dawnshard/deploy/tencent/NOTES.md，再读取问题涉及的代码与文档；联机、活动问题还要读
/workspace/Dawnshard/deploy/tencent/EVENTS.md。问答和调研也适用。
用户未另指定项目时，以本 repo 为项目上下文；用户明确谈其他话题时跟随用户。
```

## 从 `/workspace` 进入的文件入口

[WORKSPACE_AGENTS.md](WORKSPACE_AGENTS.md) 是工作区入口模板，只记录项目位置和读取规则。
在 repo 已放置到上述路径后，可执行以下命令安装入口；若工作区已有指引，保留并手动合并需要的内容：

```bash
if [ ! -e /workspace/AGENTS.md ] && [ ! -L /workspace/AGENTS.md ]; then
    ln -s Dawnshard/.codex/WORKSPACE_AGENTS.md /workspace/AGENTS.md
fi
```

模板随 repo 保存；`/workspace/AGENTS.md` 的链接属于当前任务的工作区。
新的云任务从已发布环境的准备状态创建独立工作区，当前任务里新增的文件不会自动写回已发布配置。
要在后续任务恢复入口，进入 `Settings > Codex Cloud > Environments`，从目标环境的 `…` 菜单选择
`Edit`，让环境配置流程把上述命令加入安装流程并测试，保存后选择 `Republish`，再新建任务验证。
仅修改当前容器中的安装脚本副本，不等于已经完成了这次发布。

以后换 repo 时，为其单独建立项目和云环境，替换项目名称、repo 路径和状态文档路径；
将项目用途、必读文档和问答前检查规则放在该 repo 的 `AGENTS.md`，把依赖准备与入口安装保存在已发布环境配置。

这提供了文件入口，是否自动注入模型上下文仍取决于客户端。
可在全新会话里问“这个项目目前联机做到哪一步？”，核对它是否实际读取上述文件，
并根据 repo 说明普通联机、活动关和大型 Raid 各自的验证范围。

官方配置说明：[项目及项目指令](https://learn.chatgpt.com/docs/projects?surface=app)、
[云环境编辑、发布和任务隔离](https://learn.chatgpt.com/docs/environments/cloud-environments)。
