<div align="center">

# 太吾绘卷 Mod 开发 .skill

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Agent Skills](https://img.shields.io/badge/Agent%20Skills-Standard-green)](https://github.com/vercel-labs/skills)
[![skills.sh](https://img.shields.io/badge/skills.sh-Compatible-blue)](https://skills.sh)
[![Multi-Runtime](https://img.shields.io/badge/Runtime-Claude%20Code%20%C2%B7%20Codex%20%C2%B7%20Cursor%20%C2%B7%20ZCode-blueviolet)](#安装)

<br>

**为《太吾绘卷：天幕心帷》（The Scroll of Taiwu，Steam App 838350）制作独立 C# Mod 的全流程 skill。**

<br>

从环境检查（.NET SDK、ilspycmd、游戏定位）、按需反编译游戏程序集、版本一致性校验，
到编写插件入口、用 HarmonyLib 打补丁、定义 Config.Lua 设置项、前后端 RPC 通信，
再到编译部署、看日志调试、发布到创意工坊及后续版本维护——**完整链路，一个 skill 搞定**。

还内置**游戏机制知识库**：把游戏内《太吾百晓册》（官方百科）转成 markdown，让 AI 先理解游戏机制/数值再结合反编译源码定位实现，patch 写得更准。


[安装](#安装) · [这个 skill 能做什么](#这个-skill-能做什么) · [工作流](#工作流) · [设计要点](#设计要点)

</div>

---

## 安装

支持三种方式，任选其一。

### 方式一：把本仓库链接贴给 AI Agent（最省事）

直接把下面这行发给你的 AI Agent（Claude Code / Codex / Cursor / ZCode 等支持 Agent Skills 标准的客户端）：

```
请安装这个 skill：https://github.com/gruiyuan/taiwu-mod-dev-skill
```

Agent 会自动识别并安装，之后你直接说「帮我做个太吾的 mod」即可触发。

### 方式二：命令安装（推荐）

使用 [vercel-labs/skills](https://github.com/vercel-labs/skills) 的 CLI 一键安装。**在你的太吾 mod 工程目录下执行**——本 skill 是太吾专用、不适合放全局，默认就装到当前项目：

```bash
npx skills add gruiyuan/taiwu-mod-dev-skill
```

> 该命令默认装到**当前项目的** agent skills 目录（项目级，会随仓库提交共享）。**不要加 `-g`/`--global`**——那是装到全局用户目录，会让所有项目都加载这个太吾专用 skill，没有必要。
> 也兼容 `npx skill add gruiyuan/taiwu-mod-dev-skill` 写法。

### 方式三：手动安装

把本仓库的 `SKILL.md` 和 `references/` 目录复制到你的 Agent skills 目录即可：

```bash
git clone https://github.com/gruiyuan/taiwu-mod-dev-skill.git
```

然后根据你用的 Agent，放到对应位置（任选其一）：

然后在你的太吾 mod 工程根目录下，按所用的 Agent 放到对应项目级目录（本 skill 不建议放全局）：

| Agent | 项目级 skills 目录 |
|---|---|
| Claude Code | `<项目>/.claude/skills/taiwu-mod-dev/` |
| Codex | `<项目>/.codex/skills/taiwu-mod-dev/` |
| Cursor | `<项目>/.cursor/skills/taiwu-mod-dev/` |
| OpenCode | `<项目>/.opencode/skills/taiwu-mod-dev/` |
| ZCode | `<项目>/.agents/skills/taiwu-mod-dev/` |

> 本 skill 是太吾专用、不适合放全局（会让所有项目都加载）。各 Agent 都支持项目级安装，每个用自己的点目录（`.claude` / `.codex` / `.cursor` / `.opencode` / ZCode 用 `.agents`）。`<项目>` 指你的太吾 mod 工程根目录。
>
> Cursor 注意：`.cursor/skills-cursor/` 是 Cursor 内置只读目录，用户 skill 放 `.cursor/skills/`（无 `-cursor` 后缀）。

目录结构应为：

```
taiwu-mod-dev/
├── SKILL.md
├── scripts/
│   ├── dotnet-build-kb/            # 生成《太吾百晓册》机制知识库的 .NET 工程（dotnet run）
│   └── config-extractor/           # 从游戏 dll 离线提取全部配置数值的 .NET 工程（Mono.Cecil）
└── references/
    ├── backend-harmony.md
    ├── config-lua-and-settings.md
    ├── frontend-backend-rpc.md
    ├── frontend-notes.md
    ├── game-config.md             # 游戏配置数值（config-extractor）的构建与使用
    ├── game-knowledge-base.md     # 游戏机制知识库（百晓册）的构建与使用
    ├── project-setup.md
    └── publishing.md
```

---

## 这个 skill 能做什么

当你说出下面这些，skill 会自动接管：

- 「我想做个让太吾免疫中毒的 mod」
- 「帮我加个物品 / 改战斗伤害」
- 「我的 mod 加载不了 / Harmony patch 不生效」
- 「怎么反编译太吾看看某个方法的签名」
- 「怎么更新已发布的 mod」
- 「游戏更新了我的 mod 还能用吗」
- 「帮我做个前后端通信的 mod」
- 「这个游戏机制是怎么设计的？某项数值是多少？」

## 工作流

skill 把 mod 开发组织成四个阶段，每次会话按需推进：

1. **前置检查** — .NET 8+ 环境、ilspycmd 版本匹配、从注册表定位游戏安装目录。
2. **反编译就绪** — 按 Steam buildid 做版本一致性校验，按需反编译前端 `Assembly-CSharp.dll` / 后端 `GameData.dll` 到工作区。（可选/推荐：同步生成《太吾百晓册》机制知识库。）
3. **开发** — 插件入口（`TaiwuRemakePlugin`）、Harmony patch、Config.Lua 设置项、前后端 RPC，编译部署看日志。
4. **发布与维护** — 完善 Config.Lua（交互收集作者/版本等信息）、自检、游戏内上传创意工坊，以及版本更新、适配游戏新版本、回滚。

## 设计要点

- **源码读、DLL 引用分开** — 反编译产物只读；编译引用游戏目录的真实 DLL，且 `<Private>false</Private>` 防类型身份冲突。
- **前后端目录别混** — 前端 `Managed\Assembly-CSharp.dll`、后端 `Backend\GameData.dll`；后端工程 `net8.0`、前端工程 `netstandard2.1`。
- **版本不靠猜** — 游戏版本号从启动场景 `level0` 离线提取（无需启动游戏）；Steam `buildid` 判断反编译源码是否过期。
- **优先 public、优先改配置** — 能改 lua 配置/事件脚本就别上 Harmony，能 patch public 就别碰 internal。

## 适用版本

面向当前《太吾绘卷：天幕心帷》（后端 .NET 8、Unity Mono 前端）。游戏大版本更新后，按 skill 阶段二的版本一致性校验重新反编译对齐即可。

## License

[MIT](LICENSE)
