# Mod 发布指引

把开发好的 mod 发布到 Steam 创意工坊。太吾的发布**全在游戏内 UI 完成**（mod 管理器的"上传/更新"），不需要手动调用 Steamworks API——但要发布前先把 `Config.Lua` 写规范，让工坊页面信息和兼容性检查都对。

## 关键认知：发布由游戏内 UI 完成

发布流程是游戏 `ModUploadEditPanel` 实现的（`Game.Views.Mod.Upload`）。开发者要做的是：
1. 把 mod 目录按规范准备好（`Config.Lua` + `Plugins\*.dll` + 封面图）。
2. **在游戏内** mod 管理器选中该 mod → 点"上传/更新" → 填更新日志 → 游戏调 Steamworks 上传。

所以我们（作为开发辅助）的职责是：**发布前把 Config.Lua 写好、做自检、提示用户去游戏内完成最后一步**。不要尝试用脚本直接上传——游戏 UI 会处理 `Settings.Lua` 的临时排除、Source 标记切换等内部状态，手动绕开容易出问题。

## 发布前：先完善 Config.Lua（这一步要和用户交互）

发布前 Config.Lua 不能停留在开发期的占位状态。**用 AskUserQuestion 和用户确认以下必要信息**，然后帮他填好：

### 必须收集的字段

| 字段 | 说明 | 交互方式 |
|---|---|---|
| `Title` | mod 名称（工坊显示名）。**不能为空、不能与本地其他 mod 重名**（游戏校验，重名会拒绝保存）。 | 询问用户 |
| `Author` | 作者名。**不要自己编、不要留占位符**（如 `taiwu-mod-dev` 不是开发者想要的名字）。先自动探测最近登录的 Steam 昵称（读 `$(HKCU:\Software\Valve\Steam SteamPath)\config\loginusers.vdf` 的 `PersonaName`，优先 `MostRecent=1`），探测到→AskUserQuestion 让用户确认/自定义/留空；探测失败→直接让用户输入。完整脚本见 config-lua-and-settings.md「确定 Author（作者名）」。 | 探测 + AskUserQuestion |
| `Version` | mod 版本（如 `1.0.0`）。游戏会经 `VersionStringToUlong` 规范化，`1.0` 和 `1.0.0.0` 等价。**更新时版本号必须变**（否则 Steam 认为没更新）。 | 询问用户 |
| `GameVersion` | 目标游戏版本，影响兼容性检查（见 config-lua-and-settings.md）。 | 从 `level0` 离线提取（不需要启动游戏），方法见 config-lua-and-settings.md「怎么查当前游戏版本」；失败则请用户启动一次游戏后从 Player.log 读 |

### 推荐收集的字段

| 字段 | 说明 |
|---|---|
| `Description` | 工坊详情描述。支持 Steam BBCode：`[h1][h2][b][i][list][*][code][url][img]`。多行用 `\n`，或 Lua 长字符串 `[[]]`/`[=[]=]`。 |
| `TagList` | 工坊标签（帮助分类/被搜索）。官方标签见下表。 |
| `Cover` / `WorkshopCover` | 封面图文件名（PNG/JPG），放在 mod 根目录。工坊封面建议 16:9。 |
| `DetailImageList` | 详情图列表，工坊页面展示。 |

### 官方工坊标签（TagList 取值）

来自 `SteamManager.AllTagList`，常见的有（英文 key）：

- `Optimizations` — 优化类
- `New Content` — 新内容
- `Game Balance` — 平衡性
- `Bug Fixes` — bug 修复
- `Compatible Mods` — 兼容/前置 mod
- `Interface` — 界面
- `Translation` — 翻译

> TagList 的值是这些英文 key（不是中文显示名）。多标签用 `{ "Optimizations", "Interface" }`。发布后玩家在工坊按这些标签筛选。

### 这些字段发布时由游戏自动管理，**不要手填**

| 字段 | 处理 |
|---|---|
| `FileId` | 游戏首次上传成功后自动写入（创意工坊 ID）。**手填无效**，上传会覆盖。 |
| `Source` | `0`=本地 mod，`1`=工坊 mod。发布时游戏会临时改成 `1` 再上传，上传后恢复 `0`。**手填会破坏游戏的状态管理**。 |
| `UpdateLogList` | 在游戏内上传对话框填更新日志，游戏自动写入。 |

## 发布前自检清单

帮用户逐项确认后，再让他去游戏内发布：

1. **Config.Lua 完整**：Title/Author/Version/GameVersion 都填了，无占位符。
2. **Version 比上次发布的大**（更新时）。每次更新必须递增版本号。
3. **GameVersion 匹配当前游戏**：从 `level0` 离线提取（方法见 config-lua-and-settings.md「怎么查当前游戏版本」），填入 Config.Lua。
4. **dll 已编译并部署**到 mod 的 `Plugins\`（见 project-setup.md 的部署步骤）。
5. **封面图存在**（如有 Cover 字段）：文件在 mod 根目录，PNG/JPG。
6. **dll 不带多余的游戏 dll**：所有游戏引用 `<Private>false</Private>`，`Plugins\` 里只有你自己的 dll + 你确实需要的第三方依赖。多带一个游戏 dll 会导致玩家端类型冲突崩溃（见 project-setup.md）。
7. **Settings.Lua 删除**（可选）：发布前删掉 mod 目录下的 `Settings.Lua`，让玩家用你的 `DefaultSettings`。游戏上传时会自动临时排除它，但手动删更干净。
8. **mod 目录干净**：没有 `.cs`、`.csproj`、`refs\`、`bin\`、`obj\` 等开发文件（这些不该发布）。

> 这套自检对应用户的"发布前把 Config 写好"诉求。第 6、8 条尤其重要——带进游戏 dll 或开发文件是最常见的发布事故。

## 发布步骤（游戏内操作，告知用户）

1. 启动太吾 → 主菜单 → **Mod 管理器**（创意工坊/模组）。
2. 找到你的 mod（本地 mod 列表里），点进上传编辑面板。
3. 确认名称/描述/标签/封面（对应前面填的 Config 字段会自动读入）。
4. **首次发布**点"上传"，**更新**点"更新"——会弹更新日志框，填写本次更新内容。
5. 等待上传进度条完成。成功后会提示 FileId（创意工坊 ID）。

> 首次上传成功后，`Config.Lua` 里会自动多出 `FileId` 字段——**不要手改它**，那是创意工坊的标识，下次更新靠它定位。
> Steam 创意工坊要求游戏从 Steam 启动、Steam 在线、账号有发布权限。

## 关于 Config.Lua 大小写

注意：游戏读取的文件名是 **`Config.lua`**（小写 l），不是 `Config.Lua`。`ModUploadEditPanel.Import` 里查的是 `Config.lua`（第909行）。Windows 大小写不敏感所以通常不影响，但**发布前确保文件名是 `Config.lua`** 最稳妥，避免 Linux/工坊服务器大小写敏感问题。

> （社区习惯常写成 `Config.Lua`，本 skill 其他文档沿用此写法指代该文件，实际文件名建议用 `Config.lua`。）

## 版本号格式

`ModManager.VersionStringToUlong` 把点分版本号转成 ulong（如 `1.2.3` → 内部数值）。编码规则（来自反编译，准确）：
- 最多 4 段：`Major.Minor.Build.Revision`，如 `1.0.0.0`。
- 不足自动补 0：`1.0` 等价于 `1.0.0.0`。
- 每段 16 bit，取值范围 **0–65535**；**超出 65535 的段会被归 0**（`ParseVersionNumber` 截断）。所以别写 `1.0.70000` 这种。
- 解析用 `System.Version.TryParse`，失败（非数字格式）返回 0 → 等于"没填版本号"，保存会被拒。
- 4 段依次占 ulong 的高→低位，比较 ulong 即等于先比 Major 再比 Minor…（字典序 = 数值大小）。
- `VersionUlongToString` 会规范化显示（补齐 4 段）。

更新时让新版本号 > 旧版本号（如 `1.0.0` → `1.0.1`），Steam 才会判定为有更新。

### 版本号语义化建议（持续维护用）

用 `Major.Minor.Build` 三段就够（Revision 一般留 0）：

| 改动类型 | 怎么递增 | 例子 |
|---|---|---|
| 破坏性变更（改了设置项 Key/默认值、配置不兼容、依赖变动） | Major +1 | `1.2.3` → `2.0.0` |
| 新功能 / 明显改动 | Minor +1 | `1.2.3` → `1.3.0` |
| Bug 修复 / 小调整 / 适配游戏小版本 | Build +1 | `1.2.3` → `1.2.4` |

> 这只是约定，游戏不强制——但保持一致的语义能让玩家看更新日志就知道要不要重新配置。

## 更新已发布的 mod

mod 第一次发布后，后续每次改动重新发布叫"更新"。游戏里"上传"和"更新"是同一个发布流程的不同分支：代码 `UploadMod` 判断 `CurEditModIsNotCreated`（mod 是否已有 FileId），有 → 走 `SteamManager.UploadItemUpdate(FileId, ...)`（往同一个创意工坊项目推新内容），无 → 走 `CreateItem`（首次创建）。所以**更新的本质是往同一个 FileId 推新版本**。

### 更新工作流（完整链路）

1. **改代码 / 改 Config.Lua**：和平时开发一样，在工程里改。
2. **递增 `Version`**：必做。Steam 靠版本号变化判定有更新，**不递增会被当作没变化**（玩家收不到更新提示）。按上面"版本号语义化建议"选递增哪一段。
3. **`FileId` 不要动**：Config.Lua 里首次发布时游戏自动写入的 `FileId` 字段，更新时保持原样——它就是定位创意工坊项目的标识，改了会变成创建新 mod。
4. **核对 `GameVersion`**：如果游戏也更新了，重新从 `level0` 离线提取（方法见 config-lua-and-settings.md「怎么查当前游戏版本」）。
5. **重新编译并部署 dll** 到 mod 的 `Plugins\`（见 project-setup.md）。
6. **删掉 `Settings.Lua`**（可选，推荐）：让玩家用你的新 `DefaultSettings`，避免旧设置值和新代码冲突。
7. **游戏内更新**：Mod 管理器 → 选这个 mod → 点"更新"（不是"上传"）→ 填**更新日志** → 等上传进度条走完。

### 更新日志怎么写

- 游戏弹的更新日志框对应 `UpdateLogList`，填的每条会成为工坊页面"更新说明"的一条目。
- 建议写明：本次改了什么、是否有破坏性变更、玩家需不需要重新配置设置。
- 玩家主要靠它判断要不要更新 / 更新后要不要重新设置，认真写。

### 更新后发现问题要回滚

Steam 创意工坊**不保留旧版本**（不像 git 有历史），更新即覆盖。要回滚：

- **发布前**：部署新版 dll 前先备份旧的 `Plugins\*.dll` 和 `Config.Lua`（建议在 mod 工程里用 git 管版本，每次发布打个 tag，回滚就 checkout tag 重新部署）。
- **已更新**：从 mod 工程的 git 历史找回旧 dll，重新部署，再走一次"更新"流程把旧版推上去（Version 要再递增一次，不能回退——版本号只能往前走，否则工坊不认）。

> 版本号只能单调递增是 Steam 的限制：新 Version 必须 > 上一次发布的 Version，无法回退到一个更小的号。所以"回滚"实际是"发布一个内容是旧版、Version 更大的新版本"。

## 游戏更新后如何维护 mod

这是 mod 维护最常见的场景：游戏发了新版本，你的 mod 可能受影响。核心问题是**游戏更新后 patch 签名可能对不上了**。

### 第一步：判断我的 mod 是否受影响

- 看游戏更新公告（官方"接口变动"说明），确认有没有动你 patch 的方法。
- 游戏更新后，**先跑一遍反编译校验**（见 SKILL.md 阶段二 2a/2c）：对比新 `buildid` 和旧反编译目录的 buildid，**不一致就重新反编译**对齐新签名。
- 重新反编译后，对你 patch 过的每个方法，**用 Grep 核对签名**（类名/方法名/参数类型/参数个数）是否还和代码里写的一致。任何一个变了，patch 会静默失效或报错。

### 第二步：修复失效的 patch

- 签名变了：按新签名更新 `[HarmonyPatch]` 的参数类型数组，或改 Prefix/Postfix 的参数列表。
- 方法被删/改名/移到别的类：在反编译源码里搜替代方法，重新设计 patch 目标。
- 方法逻辑重构：可能要重新理解新逻辑，重写 patch（参考 backend-harmony.md 的定位方法）。

### 第三步：更新 mod 并发布

1. 修好代码、重新编译部署。
2. **递增 `Version`**（适配游戏版本通常算 Build +1 或 Minor +1）。
3. **更新 `GameVersion`** 为新的当前游戏版本（兼容性检查需要 Major.Minor 匹配，见 config-lua-and-settings.md）。
4. 游戏内走"更新"流程，更新日志里写明"适配游戏 x.y.z 版本"。

### 关于"游戏更新"和"mod 更新"的关系（避免混淆）

| 概念 | 是什么 | 在哪改 |
|---|---|---|
| `GameVersion`（Config.Lua） | 目标**游戏**版本，给兼容性检查用 | 游戏更新且你的 mod 要适配新版本时改 |
| `Version`（Config.Lua） | 你**自己的 mod** 版本 | 你每次发布新版 mod 时递增 |
| `buildid`（appmanifest） | Steam 给游戏的构建号，反编译版本指纹 | 不进 Config.Lua，只用于判断反编译源码是否过期 |

游戏更新后，**不一定**要更新你的 mod——如果你 patch 的方法没变，mod 可能仍然正常工作。先用上面第一步确认有没有影响，没必要盲目跟着游戏版本号刷新。
