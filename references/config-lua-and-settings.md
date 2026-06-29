# Config.Lua 与设置项

`Config.Lua` 是 mod 的元信息文件，**每个 mod 必须有，缺了必然加载失败**（大小写敏感：`Config.Lua`，`L` 大写）。同名目录 `Config/` 放 mod 内容资源，别混淆。

## 完整字段速查（来自 `ModManager.cs` 与实际 mod）

```lua
return {
  -- —— 元信息 ——
  Title           = "我的 Mod",                  -- 显示名
  Version         = "1.0.0",                    -- mod 版本
  GameVersion     = "1.0.44",                   -- 目标游戏版本，尽量对上当前游戏
  Author          = "作者名",
  Description     = [=[说明，支持 Steam BBCode：[h1][h2][b][list][*][code][url]]=],
  Cover           = "Cover.png",                -- 可选，封面图文件名（相对 mod 根）
  WorkshopCover   = "Cover.png",                -- 可选，创意工坊封面
  DetailImageList = { "DetailImage1.png" },     -- 可选，工坊详情图列表
  TagList         = { "Extensions" },           -- 可选，工坊标签
  Dependencies    = { 3747731025 },             -- 可选，依赖的其它 mod 的 FileId

  -- —— 插件入口（dll 相对 Plugins/ 的路径；用 ../ 可跳出）——
  BackendPlugins  = { "MyMod.Backend.dll" },
  FrontendPlugins = { "Frontend/MyMod.Frontend.dll" },  -- 没有就不写

  -- —— 设置项 ——
  DefaultSettings = {
    -- 每个元素是一个设置条目，见下方各类型
  },
  NeedRestartWhenSettingChanged = true,   -- 改设置后是否要求重启才生效
}
```

### 工坊发布相关字段
- `Source = 1`、`FileId = <数字>`：用于创意工坊上传/更新标识。本地开发不用填，发布到工坊时由游戏写入。
- `Source = 0` 或缺省：本地 mod。

## 设置项类型（来自 `FrameWork.ModSystem`）

所有类型共享基础字段：`SettingType`、`Key`、`DisplayName`、`Description`、可选 `GroupName`。每种类型的值字段不同。

### Toggle（开关，bool）
```lua
{
  SettingType  = "Toggle",
  Key          = "EnableFeature",      -- C# 里用这个 key 读
  DisplayName  = "启用某功能",
  Description  = "勾选后生效。",
  DefaultValue = true,
}
```

### InputField（文本输入，string）
```lua
{
  SettingType  = "InputField",
  Key          = "CustomName",
  DisplayName  = "自定义名称",
  Description  = "留空使用默认。",
  DefaultValue = "",
}
```

### Slider（滑块，int；注意只能是整数）
```lua
{
  SettingType  = "Slider",
  Key          = "DamageMultiplier",    -- 实际是百分比整数
  DisplayName  = "伤害倍率",
  Description  = "50 到 300 之间的整数。",
  MinValue     = 50,
  MaxValue     = 300,
  StepSize     = 10,                    -- 可选，默认 1
  DefaultValue = 100,
}
```
> Slider 只支持 int。要"小数倍率"就用整数百分比，C# 里除以 100.0。

### Dropdown（下拉框，存选中项的 int 索引）
```lua
{
  SettingType  = "Dropdown",
  Key          = "Difficulty",
  DisplayName  = "难度",
  Description  = "选择难度等级。",
  Options      = { "简单", "普通", "困难" },  -- 顺序即索引：简单=0, 普通=1...
  DefaultValue = 1,
}
```

### ToggleGroup（单选按钮组，类似 Dropdown）
同样用 Options + DefaultValue(int 索引)。

## Settings.Lua

- **不用手写**。`Settings.Lua` 保存玩家**当前**设置值，玩家在游戏内改设置后由游戏自动生成。
- 没有这个文件时，游戏用 `DefaultSettings` 里的默认值。
- 想重置设置：删掉 mod 目录下的 `Settings.Lua` 即可回到默认。

## 在 C# 里读设置

值类型对应 `DefaultValue` 的类型。用 `ModManager.GetSetting`（4 个重载：int/float/bool/string）：

```csharp
public override void OnModSettingUpdate()
{
    // 用临时变量接，返回值表示 key 是否存在
    bool enable = true;
    ModManager.GetSetting(ModIdStr, "EnableFeature", ref enable);
    _enabled = enable;

    int mult = 100;
    ModManager.GetSetting(ModIdStr, "DamageMultiplier", ref mult);

    // Dropdown 存的是索引(int)
    int diff = 1;
    ModManager.GetSetting(ModIdStr, "Difficulty", ref diff);

    string name = "";
    ModManager.GetSetting(ModIdStr, "CustomName", ref name);
}
```

后端进程里也能用 `DomainManager.Mod.GetSetting(ModIdStr, key, ref val)`（等价）。

## 设置生效的两种时机

由 `NeedRestartWhenSettingChanged` 决定：

- **`true`**（多数规则类 mod）：改设置后游戏提示重启，重启时重新 `Initialize`。在 `Initialize` 里读一次即可。
- **`false`**：改设置立即生效，游戏会调用插件的 `OnModSettingUpdate()`。在 `OnModSettingUpdate` 里重新读设置并更新你的运行时状态。适合纯显示、即时开关的场景。

> `ModIdStr` 是 `TaiwuRemakePlugin` 基类字段，读设置和给 Harmony 起名都用它，不要硬编码 mod 目录名。

## GameVersion 与版本兼容性（重要）

`GameVersion` 不只是展示信息——**游戏用它做兼容性检查**，决定 mod 列表里是否显示"过期"红色警告。来源：`ModManager.IsModOutdated()` / `GameApp.ParsedGameVersion`。

### 数据流

```
Config.Lua 的 GameVersion 字符串 (如 "1.0.44")
   → ModManager.ParseGameVersion() 解析成 System.Version (Major.Minor.Build)
游戏自身版本 GameApp.ParsedGameVersion (运行时确定)
```

`ParseGameVersion` 容错：支持 `"1.0.44"`、`"V1.0.44"`（去 V）、`"1.0.44-test"`（截断 `-` 之后）。所以填纯数字 `"1.0.44"` 最稳。

### 判定规则（返回 true = 显示"过期"警告，但**不阻止加载**）

| 条件 | 结果 |
|---|---|
| `GameVersion` 缺失/为 null | 过期 |
| `GameVersion < 0.0.79`（代码里 `CutVersion`） | 过期 |
| **Major 或 Minor 与游戏不等** | 过期 |
| mod 的 Build > 游戏当前 Build（且无 LegacyPlugins） | 过期 |
| 其余情况 | 兼容 |

> "过期"只是 UI 警告（`outdatedWarning`），**不会阻止 mod 加载运行**。真正不兼容的崩溃发生在 Harmony patch 签名对不上时。

### 实践建议

- **把 `GameVersion` 填成当前游戏版本**即可通过兼容性检查。
- 兼容判定较宽松：Major.Minor 匹配、mod 的 Build ≤ 游戏即可。所以填当前版本最稳，游戏小版本更新后通常仍显示兼容。

### 怎么查当前游戏版本

从启动场景 `level0` 提取（**不需要启动游戏**）。游戏版本号存在 `<游戏根>\The Scroll Of Taiwu_Data\level0` 文件里。

**PowerShell**（推荐）：

```powershell
$level0 = "$GameDir\The Scroll Of Taiwu_Data\level0"
$bytes  = [System.IO.File]::ReadAllBytes($level0)
$text   = [System.Text.Encoding]::ASCII.GetString($bytes)
# 版本号(x.y.z) 紧跟构建时间戳(20xxxxxx)，二者相邻，唯一识别游戏版本
if ($text -match '(\d+\.\d+\.\d+).{0,8}20\d{6}') { $matches[1] }
```

**grep**（Git Bash 等）：

```bash
grep -aoE "[0-9]+\.[0-9]+\.[0-9]+.{0,8}20[0-9]{6}" "<游戏根>/The Scroll Of Taiwu_Data/level0" \
  | grep -oE "^[0-9]+\.[0-9]+\.[0-9]+" | head -1
```

> 正则用"版本号 + 紧跟 `20xxxxxxxx` 时间戳"做锚点，不依赖具体版本号格式，且能避开同文件里的 Unity 引擎版本号。

**获取失败时**：请用户启动一次游戏（到主菜单即可，不必进存档），然后直接从运行日志读取：

```bash
# GameApp 启动时打印 "Game version = x.y.z"
$LogPath = "$env:USERPROFILE\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log"
grep "Game version = " "$LogPath" 2>/dev/null | tail -1
```

## 常见坑

- `Config.Lua` 大小写错（写成 `config.lua`）→ mod 不显示/不加载。Linux/Steam 大小写敏感。
- `Key` 在 C# 读和 Lua 写里不一致 → `GetSetting` 返回 false，用默认值。
- Slider 想存小数 → 不支持，改用整数百分比。
- Description 里的引号：用 `[=[]=]` 长字符串语法包住，避免内嵌 `"` 冲突。
