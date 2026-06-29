# Config.Lua 与设置项

`Config.Lua` 是 mod 的元信息文件，**每个 mod 必须有，缺了必然加载失败**（大小写敏感：`Config.Lua`，`L` 大写）。同名目录 `Config/` 放 mod 内容资源，别混淆。

## 完整字段速查（来自 `ModManager.cs` 与实际 mod）

```lua
return {
  -- —— 元信息 ——
  Title           = "我的 Mod",                  -- 显示名
  Version         = "1.0.0",                    -- mod 版本
  GameVersion     = "1.0.44",                   -- 目标游戏版本，尽量对上当前游戏
  Author          = "<用户确认的作者名>",   -- 怎么定作者见下方「确定 Author（作者名）」
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

## 确定 Author（作者名）——不要自己编

> ⚠️ **不要把 Author 留成占位符、也不要自己编一个（如 `taiwu-mod-dev`、`AI`、`Author`）。** 那不是 mod 开发者想要的名字。`Author` 必须来自用户：**先自动探测 Steam 昵称作为候选，再让用户确认或输入**。

### 第一步：自动探测最近登录的 Steam 昵称

Steam 把本机登录过的账号存在 `config\loginusers.vdf`，每个账号的 `PersonaName` 就是昵称，`MostRecent="1"` 标记最近登录的那个。

> ⚠️ **中文乱码坑（重要）**：`loginusers.vdf` 是**无 BOM 的 UTF-8** 文件，但中文 Windows 上 PowerShell 5.1（系统自带 `powershell.exe`）的 `Get-Content` 默认按系统代码页（GB2312/936）解码，会把中文昵称读成乱码（正确昵称→乱码）。脚本本身若含中文字面量也会被同样误读。**必须三条全做到**：① 用 `[System.IO.File]::ReadAllText($vdf, [System.Text.Encoding]::UTF8)` 强制 UTF-8 读文件（不靠 BOM 探测）；② **脚本零中文字面量**（昵称只从文件来）；③ 设 `[Console]::OutputEncoding = UTF8` 保证输出也正确。下面是验证过的**纯 ASCII 脚本**：

```powershell
# Pure-ASCII script: nickname comes only from the UTF-8 vdf file, never mis-decoded.
$ErrorActionPreference = 'Stop'
try {
    $steamPath = (Get-ItemProperty 'HKCU:\Software\Valve\Steam').SteamPath
    if (-not $steamPath) { Write-Output 'STEAM_NICKNAME_NOT_FOUND'; exit }
    $vdf = Join-Path $steamPath 'config\loginusers.vdf'
    if (-not (Test-Path -LiteralPath $vdf)) { Write-Output 'STEAM_NICKNAME_NOT_FOUND'; exit }
} catch {
    Write-Output 'STEAM_NICKNAME_NOT_FOUND'; exit
}

$content = [System.IO.File]::ReadAllText($vdf, [System.Text.Encoding]::UTF8)
$users = [regex]::Matches($content, '(?ms)"(\d+)"\s*\{(.*?)\}')
if ($users.Count -eq 0) { Write-Output 'STEAM_NICKNAME_NOT_FOUND'; exit }

$candidates = foreach ($u in $users) {
    $body = $u.Groups[2].Value
    $persona = if ($body -match '"PersonaName"\s*"([^"]*)"') { $matches[1] } else { '' }
    $mostRec = if ($body -match '"MostRecent"\s*"(\d+)"')    { $matches[1] } else { '0' }
    $ts      = if ($body -match '"Timestamp"\s*"(\d+)"')     { [long]$matches[1] } else { 0 }
    [pscustomobject]@{ PersonaName = $persona; MostRecent = $mostRec; Timestamp = $ts }
}
# Prefer MostRecent=1; otherwise fall back to the largest Timestamp.
$chosen = $candidates | Where-Object { $_.MostRecent -eq '1' } | Sort-Object Timestamp -Descending | Select-Object -First 1
if (-not $chosen) { $chosen = $candidates | Sort-Object Timestamp -Descending | Select-Object -First 1 }
if ($chosen -and $chosen.PersonaName) {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Write-Output ("PERSONA:" + $chosen.PersonaName)
} else {
    Write-Output 'STEAM_NICKNAME_NOT_FOUND'
}
```

- **成功**（输出 `PERSONA:<昵称>`）→ 把这个昵称作为候选，进入「第二步：确认或输入」。
- **失败**（输出 `STEAM_NICKNAME_NOT_FOUND`：无 Steam、无 vdf、未登录过等）→ 跳过确认，直接进入「第二步：让用户输入」。

> 该脚本只读本地 vdf，不联网、不调任何 Steam API，安全。`MostRecent` 是 Steam 自己维护的字段，比 `Timestamp` 更可靠；多账号同时有 `MostRecent=1` 时按 `Timestamp` 取最新。
>
> 执行方式：存成 `.ps1` 用 `powershell -NoProfile -ExecutionPolicy Bypass -File <脚本.ps1>` 运行。**不要**把含中文的命令直接内联进 bash/heredoc 传给 PS 5.1——那样中文字面量会先被外层 shell 按其编码处理，再被 PS 按 GB2312 读，双重错码。脚本存成纯 ASCII 文件最稳。

### 第二步：用 AskUserQuestion 让用户定作者名

- **探测成功时**：把探测到的昵称作为**推荐项（第一个）**，另给"输入自定义"和"留空"选项，让用户确认：
  - 问题如「检测到你的 Steam 昵称是「<昵称>」，用作 mod 作者吗？」
  - 选项 1：`<探测到的昵称>`（推荐）
  - 选项 2：`输入自定义名字`（用户自填，如团队名/英文名）
  - 选项 3：`留空（发布时游戏自动用 Steam 昵称补）`
- **探测失败时**：直接问用户「mod 的作者名填什么？」，**不要给占位符默认值**，由用户输入。

确定后把结果填进 `Config.Lua` 的 `Author` 字段，以及 `[PluginConfig]` 的第二个参数（两处作者名应一致）。



- `Config.Lua` 大小写错（写成 `config.lua`）→ mod 不显示/不加载。Linux/Steam 大小写敏感。
- `Key` 在 C# 读和 Lua 写里不一致 → `GetSetting` 返回 false，用默认值。
- Slider 想存小数 → 不支持，改用整数百分比。
- Description 里的引号：用 `[=[]=]` 长字符串语法包住，避免内嵌 `"` 冲突。
- `Author` 留占位符或被自己编造（如 `taiwu-mod-dev`）→ 工坊页面显示错误作者名。**必须**按「确定 Author」流程从 Steam 昵称探测 + 用户确认得来。
