# 游戏配置数值（config-extractor）

从 `Backend\GameData.Shared.dll` 离线提取游戏**全部配置表的真实数值**——特性/武器/功法/盔甲等每个实体的完整属性（等级、属性加成、概率、消耗……）。这是做数值类 mod 时查「现状数值是多少」的权威来源。

## 何时用

凡涉及**具体数值**，而百晓册正文/数据表没覆盖时，查配置数值：

- 「**某特性**的具体效果数值？比如忠贞不渝加什么属性？」→ `config/<buildid>/CharacterFeature.json`
- 「**某功法**的破体破气、命中分段、运功格子？」→ `config/<buildid>/CombatSkill.json`
- 「**某武器**的破甲、坚韧、变招、追击？」→ `config/<buildid>/Weapon.json`（及 `Wupin_Bingqi_*`）
- 「**某建筑/物品/门派**的属性、消耗、产出？」→ 对应表
- 改任何数值前，先在这里查**当前值**是多少，才能确定 patch 成什么。

## 两个构建器的分工

| 查什么 | 用哪个 | 路径 |
|---|---|---|
| 游戏机制怎么运作（中毒几级、战斗怎么结算） | 百晓册知识库 | `knowledge-base/<buildid>/` |
| 百晓册自带的数据表（门派一览、捕捉点概率等明文表） | 百晓册知识库 → `data-tables/` | 同上 |
| **每个实体（特性/武器/功法…）的完整数值字段** | **config-extractor** | `config/<buildid>/` |
| 代码怎么实现、方法签名 | 反编译源码 | `decompiled/<buildid>/` |

> 简单记：百晓册答「机制/规则」，config 答「每个东西的具体数值」，反编译答「代码」。三者都用同一个 buildid 锚点判过期。

## 怎么构建（一次性，游戏更新后重建）

配置数值是本地产物（落在工作区根的 `config/<buildid>/`，不随仓库提交）。构建器是 skill 自带的 .NET 工程，用 Mono.Cecil 静态解析 IL，不启动游戏。

> ⚠️ **路径要点**：`--project` 用 skill 自身目录的绝对路径（config-extractor 在 skill 包内的 `scripts/` 下，不在用户工作目录）。在用户的工作区根（mod 工程根）执行，产物默认落到当前目录的 `config/<buildid>/`。

```bash
# 在用户工作区根目录执行。<skill> 替换成本 SKILL.md 所在目录的绝对路径。
dotnet run --project "<skill>/scripts/config-extractor" -c Release
# 单表提取（带详细校验）：
dotnet run --project "<skill>/scripts/config-extractor" -c Release -- -t CharacterFeature
# 强制重建 / 指定游戏目录 / 切语言：
dotnet run --project "<skill>/scripts/config-extractor" -c Release -- -g "D:\...\The Scroll Of Taiwu"
dotnet run --project "<skill>/scripts/config-extractor" -c Release -- -l CNH
```

- 纯静态解析、不启动游戏；依赖 Mono.Cecil（首次 `dotnet run` 自动还原 NuGet 包）。约 5 秒提取全部表。
- 提取后产物较大（几十 MB），按 buildid 分目录，与 `knowledge-base/<buildid>/`、`decompiled/<buildid>/` 同源锚点。游戏更新后 buildid 变，重跑即可（IL 可能变，但工具本身不用改）。

## 怎么查（给 AI）

每张表一个 JSON 文件，结构统一：`$fields`（字段名数组）+ `records`（每条记录一个对象，键是字段名）。`_manifest.json` 是全部表的清单（含表名、记录数、文件名、是否通过校验）。

1. **先看 `_manifest.json`** 找到目标表（按英文名，如 `CharacterFeature`/`CombatSkill`/`Weapon`/`Armor`）。
2. **读对应表的 JSON**，按 `TemplateId` 或 `Name` 定位具体实体。
3. 字段名是英文 PascalCase（如 `PersonalityClever`、`PenetrateOfOuter`、`HitRateStrength`）；查到数值后，如需理解字段含义，结合百晓册正文或反编译该 `ConfigItem` 类的字段定义对照。

> 配合反编译：想知道某个数值字段的精确语义（比如 `HitRateMind` 具体影响什么），反编译对应的 `Config.*Item` 类（在 `GameData.Shared.dll`）看字段定义和注释，往往能直接看出。

## 提取原理（理解边界）

- 配置数值不在外部数据文件，而是硬编码在 `GameData.Shared.dll` 的 IL 里——每条配置是 `CreateItems*()` 方法中一个 `new XxxItem(...)` 的位置参数。工具用 Mono.Cecil 模拟栈式执行，把位置参数还原成带字段名的字典。
- **只解析 dll 里静态写死的配置**，不包含 mod 运行时注入的 `_extraDataMap`（那是 mod 运行时才有的，dll 里不存在）。
- 字段名来自该表 `ConfigItem` 类的 `public readonly` 字段声明顺序；少数复杂表用 ctor 参数名 fallback。所有表走相同的通用提取逻辑，记录数与 `ConfigRefNameMapping/<表>.ref.txt` 校验。
- 部分表会有「警告」（个别字段遇到罕见 IL 模式未完全覆盖）——记录数仍正确，只是个别字段值可能退化。完整提取质量见运行时的 `_manifest.json` 的 `ok`/`warnings` 字段。

## 维护

- 产物不手改，要更新就重跑（读最新 dll 重新提取）。
- 游戏更新后 buildid 变，重跑即可（IL 可能变，但工具通用提取逻辑不用改）。
- 构建器源码在 skill 包内 `scripts/config-extractor/`，可读可改；若游戏更新后某表提取异常，对照运行时警告定位。
