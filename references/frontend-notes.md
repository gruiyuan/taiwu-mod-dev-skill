# 前端 Mod 要点

前端 = Unity 侧，源码在反编译出的 `Assembly-CSharp`（本 skill 阶段二反编译的产物）。涉及界面、渲染、输入、相机、UI 动画的改动走这里。**纯数值/规则类 mod 不需要前端**——别加前端 dll，徒增复杂度和维护面。

## 前端入口

和后端完全一样的契约：继承 `TaiwuRemakePlugin` + `[PluginConfig]`：

```csharp
using TaiwuModdingLib.Core.Plugin;
using UnityEngine;

namespace MyMod.Frontend;

[PluginConfig("MyMod.Unique.Frontend", "<用户确认的作者名>", "1.0.0")]
public sealed class FrontendPlugin : TaiwuRemakePlugin
{
    public override void Initialize()
    {
        Debug.Log("[MyMod] frontend init");
        MyFrontendPatches.Install(ModIdStr);
    }

    public override void Dispose()
    {
        MyFrontendPatches.Uninstall();
    }
}
```

引用 `Assembly-CSharp.dll`、`TaiwuModdingLib.dll`、`0Harmony.dll`，外加用到的 `UnityEngine.*.dll`（都在游戏 `Managed/` 目录，见 project-setup.md）。**前端工程的 `TargetFramework` 用 `netstandard2.1`**（前端跑在 Unity Mono/IL2CPP 上，不是 .NET 8；后端才用 net8.0）。

## 前端 vs 后端的区别

| 维度 | 前端（Assembly-CSharp） | 后端（GameData） |
|---|---|---|
| 进程 | Unity 进程（玩家直接交互） | 独立后端进程 |
| 拿数据 | 经跨进程通信请求后端 | 直接 `DomainManager.*` |
| 日志 | `UnityEngine.Debug.Log` 可用 | 见 backend-harmony.md |
| patch 目标 | UI 组件、`Game.Views.*`、输入 | 域逻辑、`GameData.Domains.*` |
| 命名空间根 | `Game.Views`、`FrameWork`、`UICommon`、`UILogic` | `GameData.Domains`、`GameData.Common` |

**前后端是不同进程**：前端 mod dll 只会被前端进程加载，后端 mod dll 只会被后端进程加载。两边互看不到对方的运行时对象，靠游戏自带的 IPC 通信。**不要在一个 mod dll 里同时引用前端和后端类型然后指望它们一起工作**——会编译过但运行时类型对不上。

## 找前端 patch 目标

前端按功能分得很细，常用入口：

- **界面**：`Game.Views.*`（如 `Game.Views.Character`、`Game.Views.Inventory`、`Game.Views.World`）。每个界面通常有一个对应的 View/Window 类。
- **UI 基础设施**：`FrameWork.UISystem`、`FrameWork.UISystem.Components`、`UICommon`、`UILogic`。
- **Mod 系统本身**：`FrameWork.ModSystem`、`ModManager.cs`——想改 mod 列表/设置面板行为时 patch 这里。
- **输入/快捷键**：搜 `Input`、`KeyCode` 相关。

定位方法和后端一样：先想"这个界面是哪个类管的"，在 `Assembly-CSharp/Game.Views.*` 里 Grep 类名或界面关键字。

## 拿 mod 目录

前端用 `ModManager.GetModInfo(ModIdStr).DirectoryName` 拿到 mod 目录名，再拼出绝对路径读自己打包的资源（贴图、配置等）：

```csharp
string modDir = Path.Combine(
    ModManager.GetModRootFolder(),
    ModManager.GetModInfo(ModIdStr).DirectoryName);
string coverPath = Path.Combine(modDir, "Cover.png");
```

`ModManager.GetModRootFolder()` 返回 `<游戏>/Mod`。

## 前端日志

前端在 Unity 进程，`UnityEngine.Debug.Log/LogWarning/LogError` 直接可用，输出到：
- `%USERPROFILE%\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log`（`Player-prev.log` 是上次启动的）

调试前端 patch 优先看这个文件，Harmony 的报错也会写这里。

## 何时需要前端

只在以下情况才加前端 dll：
- 改界面布局、加按钮、改交互。
- 改渲染、相机、特效。
- 加自定义快捷键、输入处理。
- 改 mod 管理器本身的行为。

其余（改伤害、加物品属性、改 NPC 行为、事件文本）全在后端，别碰前端。前后端各加一个 dll 时，`Config.Lua` 里分别列 `FrontendPlugins` 和 `BackendPlugins`。

## 谨慎：改前端更脆

前端和 Unity 版本、UI 框架内部实现耦合深，游戏更新时前端 API 变动比后端频繁。前端 patch 要：
- 尽量 patch public 方法。
- 用 Postfix 改可观察行为，少用 Transpiler。
- 充分看日志验证。
