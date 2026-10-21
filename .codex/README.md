# 新会话的默认项目入口

本云环境的会话起始目录曾为 `/workspace`，repo 位于 `/workspace/Dawnshard`。
repo 中的记录已经提交，不代表每个新会话都已自动读取；环境安装脚本执行过 `cd`，也不等于后续会话的工作目录会跟着改变。

## 会话入口配置

使用支持项目选择或工作目录设置的客户端时，将项目设为 `/workspace/Dawnshard`。
repo 的 [AGENTS.md](../AGENTS.md) 要求问答、调研和改代码都先读取交接记录及相关实现。

若当前聊天入口仅附加云环境，不能确认它会自动读取云端 `AGENTS.md`，
应在该入口提供的项目持久指令或自定义指令中加入以下内容；具体配置位置取决于客户端：

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

模板随 repo 保存；`/workspace/AGENTS.md` 的链接属于云环境本身，重建环境时需要重新创建。
需要重建后自动恢复时，把上述命令加入环境的安装流程；不要把当前容器中的安装脚本副本视为已经修改了平台配置。

这提供了文件入口，是否自动注入模型上下文仍取决于客户端。
可在全新会话里问“这个项目目前联机做到哪一步？”，核对它是否实际读取上述文件，
并根据 repo 说明普通联机、活动关和大型 Raid 各自的验证范围。
