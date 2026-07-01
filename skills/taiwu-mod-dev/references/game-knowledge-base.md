# 游戏机制知识库（百晓册）

把游戏内的《太吾百晓册》（官方百科）转成 markdown 知识库，让 AI 在写 mod 时**理解游戏机制和数值是怎么设计的**——这是反编译源码回答不了的问题。

源码（阶段二）告诉你「代码怎么实现、方法签名长啥样」；知识库告诉你「这个机制是什么、用什么数值、怎么运作」。两者互补：先查知识库理解机制，再去源码定位实现，patch 才写得准。

## 何时用知识库

凡涉及"**理解/还原游戏机制**"而非"只看代码签名"时，先查知识库：

- 「太吾的**中毒机制**是怎么运作的？毒素有哪些等级？」→ 百晓册正文「人物 > 伤病 > 毒素」
- 「**门派**有哪些？各自五行、立场、可用武学？」→ 数据表 `Menpai`（门派一览）
- 「**促织捕捉点**各颜色出现概率？」→ 数据表 `Buzhuodian`（捕捉点概率一览）
- 「**战斗**的化解、命中、攻击属性怎么算？」→ 百晓册正文「战斗」章
- 「**较艺/技艺**有哪些、效果如何？」→ 数据表 + 正文「修习」章

> 做数值微调类 mod（改概率/数值/规则）时，**知识库尤其关键**——你得先知道现状数值是多少，才知道 patch 成什么。

## 怎么构建（一次性，每次游戏大版本更新后重建）

知识库是按**当前安装的游戏 buildid**生成的本地产物，落在**用户工作区根目录**（mod 工程根）的 `knowledge-base/<buildid>/`，与 `decompiled/<buildid>/` 同源锚点、不随仓库提交（见 `.gitignore`）。先确保阶段一已完成（能定位到游戏目录）。

构建器是 skill 自带的零 NuGet 依赖 .NET 控制台工程，用 `dotnet run` 现场编译运行（需 .NET 8+ SDK，阶段一已要求）。

> ⚠️ **路径要点**：`--project` 必须指向 **skill 自身目录**（即本 SKILL.md / references 所在目录）下的 `scripts/dotnet-build-kb`，且用**绝对路径**。因为 `scripts/` 在 skill 安装目录里（如 `.agents/skills/taiwu-mod-dev/scripts/`），**不在用户的工作目录下**——写相对路径 `scripts/...` 会找不到工程。知识库则默认输出到**当前工作目录**（用户的工作区根），与 `decompiled/` 放一起。

```bash
# 在用户的工作区根目录（mod 工程根，即希望生成 knowledge-base/ 的地方）执行。
# 把 <skill> 替换成本 SKILL.md 所在目录的绝对路径。
dotnet run --project "<skill>/scripts/dotnet-build-kb" --configuration Release
# 例：skill 装在 D:/my-mod/.agents/skills/taiwu-mod-dev/ 时
dotnet run --project "D:/my-mod/.agents/skills/taiwu-mod-dev/scripts/dotnet-build-kb" -c Release
# 游戏装在非标准路径时显式指定 -g：
dotnet run --project "<skill>/scripts/dotnet-build-kb" -c Release -- -g "D:\...\The Scroll Of Taiwu"
# 游戏更新后强制重建（-f）：
dotnet run --project "<skill>/scripts/dotnet-build-kb" -c Release -- -f
# 也可用 -o 把知识库输出到别处（默认当前工作目录下的 knowledge-base/）：
dotnet run --project "<skill>/scripts/dotnet-build-kb" -c Release -- -o "D:\my-mod"
```

- 纯 .NET BCL、零 NuGet 依赖，首次几秒编译、之后增量；约 0.8s 生成。
- 编译产物 `scripts/dotnet-build-kb/bin/`、`obj/` 在 skill 安装目录里生成，已在 `.gitignore`，不入库。
- 产物落在工作区根的 `knowledge-base/<buildid>/`。**用 buildid 作目录名和版本指纹，与 `decompiled/<buildid>/` 完全一致**——同源同判定。
- 幂等：`_meta.json` 记录了生成时的 buildid；不带 `-f` 再跑，若 buildid 匹配则跳过（"已是最新"）。游戏更新后 buildid 变，再跑会自动重建到新 buildid 目录，旧的保留可对照。

> 知识库和反编译源码**用同一个 buildid 锚点**判断过期——两者要么都是最新、要么都需要重建，保持一致。

## 怎么查（给 AI 的查询姿势）

**第一步永远是先读 INDEX 定向**，不要凭猜测直接 grep：

```
<工作区>/knowledge-base/<buildid>/INDEX.md
```

INDEX 里有两层清单（章节树 / 数据表清单含列数行数）+ 章节跳转关系。读完 INDEX 你就知道要查的主题在哪个文件，再深读那一个文件。

知识库两层各有最适场景：

| 你要查 | 读哪层 | 路径 |
|---|---|---|
| 机制怎么运作（中毒/战斗/修习/交互…） | ① 百晓册正文（整章通读） | `encyclopedia/0X-章节.md` |
| 具体数值/掉率/门派/物品数据 | ② 数据表（表头已还原） | `data-tables/<表名>.md` |

正文里的数据表会用相对链接挂出来（`> 📊 数据表：[名](../data-tables/xxx.md)`），点进去即得完整数值表。

## 两层数据怎么来的（理解边界，避免误用）

知识库由构建器从游戏资源还原，解析逻辑**已对照游戏前端 `Assembly-CSharp.dll` 的 `Game.Views.Encyclopedia` 命名空间核实**：

- **① 正文**：`EncyclopediaAssets/EncyclopediaContent.tsv`。还原成多个顶级章节（主页/启程/世界/门派/人物/交互/修习/战斗/产业/物品/游历/扩展），按 Heading1-5 嵌套成 markdown 标题树，正文行带难度标记（初/中/高级）。具体章节数/段落数见生成后的 INDEX 与 `_meta.json`。
- **② 数据表**：`EncyclopediaAssets/*.tsv`。**关键：原始 .tsv 没有表头**，表头藏在 `EncyclopediaReference.tsv` 的 col4 里——构建器已把两者 JOIN，所以知识库里每张表都是有表头的 markdown 表格。少数表用了 rowspan/colspan（如门派一览），已按行展开。具体表数见 INDEX。

**转义与富文本**：游戏资源里的 `\u003c/\u003e`（转义尖括号）、`\n`、`\u002c`（数组内逗号）、TMP 标签（`<color>/<align>/<link>`）都被清洗成可读纯文本。正文里偶尔出现的 `{0}` 是**游戏运行时才填的占位符**（如"当前身龄 {0} 岁"里的 {0} 是运行时算出来的值），不是数据错误。

## 知识库 vs 反编译源码：分工

| 问题 | 知识库（百晓册） | 反编译源码（阶段二） |
|---|---|---|
| 中毒有几级、各发作概率？ | ✅ 正文+数据表 | ❌ |
| 门派数据、武学列表？ | ✅ 数据表 | ❌ |
| 某机制**设计意图**？ | ✅ 正文（官方解释） | ❌ |
| 改这个数值要 patch 哪个方法？ | ❌ | ✅ Grep 签名 |
| 方法参数类型/返回值？ | ❌ | ✅ |
| Domain 怎么组织、DataContext 怎么用？ | ❌ | ✅ |

**典型工作流**：用户说"做个让太吾免疫中毒的 mod" → ①查知识库正文「人物>伤病>毒素」理解中毒机制 → ②反编译后端 Grep 毒素相关方法签名 → ③写 Harmony patch（见 `backend-harmony.md`）。

## 维护

- 知识库是产物，**不手改**——要更新就重跑构建器（读最新游戏资源重新生成）。
- 游戏更新后：先反编译校验 buildid（阶段二），buildid 变了就重建知识库（`-f` 或删 `knowledge-base/<旧buildid>/`）。
- 构建器源码在仓库 `scripts/dotnet-build-kb/`（`Program.cs` + `BuildKnowledgeBase.csproj`），随仓库提交、可读可改；若游戏改了百晓册数据格式（新增列/改转义），改源码对应段落再用 `dotnet run` 重跑。
