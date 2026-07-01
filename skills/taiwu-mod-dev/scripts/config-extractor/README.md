# 太吾绘卷配置离线提取器（config-extractor）

**纯静态、不启动游戏**地从 `GameData.Shared.dll` 提取太吾绘卷的数值配置，导出为带字段名的 JSON。

## 为什么需要这个

太吾的配置数值**不是外部数据文件**，而是直接硬编码在 `Backend\GameData.Shared.dll` 的 IL 代码里——每条配置是 `CreateItems*()` 方法中一个 `new XxxItem(...)` 调用的位置参数。

游戏没提供"导出配置"的官方命令行（只有运行时 API `ConfigData.ExportToFiles`，需启动游戏）。本工具用 **Mono.Cecil 静态解析 IL**，模拟栈式执行把每个 `new Item(...)` 的位置参数还原成带字段名的字典，无需运行游戏。

## 适用范围

- **批量模式**（默认）：不带参数即扫描 dll 里所有 `ConfigData<TItem, TKey>` 子类，一次提取**全部配置表**（数量随游戏版本变化，当前 buildid 23957505 下为 303 张），约 5 秒完成。也可用 `--all` 显式指定（与默认相同）。
- **单表模式**（`-t <表名>`）：只提取指定表，带详细输出。所有表走相同的通用校验（记录数与 ref.txt 匹配），没有任何表被特殊对待。

## 用法

```bash
cd <工作区根>  # 在用户的 mod 工程根目录下执行；产物默认输出到当前目录的 config/<buildid>/

# 提取全部配置表（默认行为，也可显式 --all）
dotnet run --project <skill>/config-extractor -c Release
# → config/<buildid>/<表名>.json × N + config/<buildid>/_manifest.json（汇总清单，N = 当前游戏的配置表数）

# 单表提取（带详细校验输出）
dotnet run --project <skill>/config-extractor -c Release -- -t CharacterFeature
dotnet run --project <skill>/config-extractor -c Release -- -t Accessory

# 可选参数
dotnet run --project <skill>/config-extractor -c Release -- -g "D:\Steam\...\The Scroll Of Taiwu"   # 指定游戏目录
dotnet run --project <skill>/config-extractor -c Release -- -o "D:\some\dir"                         # 指定输出目录
dotnet run --project <skill>/config-extractor -c Release -- -l CNH                                   # 切换语言（默认 CN）
```

## 实测性能（buildid 23957505，仅作参考快照）

> 以下数字是某一时刻的实测，**会随游戏版本变化**（游戏更新后表数、记录数都会变）。表数和记录数由程序运行时动态扫描得到，不写死在代码里。

| 指标 | 值（该 buildid 下） |
|---|---|
| 表总数 | 303 |
| 成功 | 303 / 303（100%） |
| 总记录数 | 67,505 |
| 总大小 | 72 MB |
| **总耗时** | **约 5 秒**（含 dll 加载，纯提取约 4.8s） |
| 平均每表 | 17 ms |

## 输出示例

每张表输出到 `config/<buildid>/<表名>.json`，结构统一（带元信息头 + records 数组）：
```json
{
  "$table": "CharacterFeature",
  "$gameBuildId": "23957505",
  "$extractedAt": "2026-06-30 ...",
  "$recordCount": 905,
  "$fields": ["TemplateId", "Name", "Type", "Level", ...],
  "records": [
    {
      "TemplateId": 0,
      "Name": "孔武有力",
      "Type": 1,
      "Level": 1,
      "Desc": "此人魁梧奇伟，强壮有力...",
      ...
    },
    {
      "TemplateId": 1,
      "Name": "牛虎怪力",
      "Type": 1,
      "Level": 2,
      ...
    },
    ...
  ]
}
```

字段名来自该表 ConfigItem 类的字段声明（或 ctor 参数名 fallback），不同表的字段不同——每张表用自己的真实字段，互不干扰。

## 校验机制

所有表一视同仁，走同一条通用校验（没有任何表被特殊对待）：

- **记录数**（主信号）：与 `ConfigRefNameMapping/<表>.ref.txt` 的 id 数一致（排除 `None/-1` 占位）。当前版本下全部表通过。
- **TemplateId + Name 一致性**：仅作信息提示（很多表的 Name 走自己的 `*_language.txt` 而非 ref.txt，所以 Name 不匹配不判失败——记录数匹配才是可靠信号）。

批量模式还有"警告"统计：表示某表遇到未完全覆盖的 IL 模式（如罕见 opcode），记录仍能提取但个别字段可能不完整。当前版本下约两成表有警告（多为 1 条，少数复杂表如 CharacterFeature 有上千条——这些表的字段值类型组合更多样）。

## 工作原理

核心是 `ValueExtractor`（`IlValueExtractor.cs`）——在 `CreateItems*()` 方法体上模拟一个值栈（含局部变量表），识别 IL 模式：

| 模式 | IL 形态 | 处理 |
|---|---|---|
| 整数常量 | `ldc.i4.*` | Cecil 直接给 int 值，负数天然支持 |
| 字符串字面量 | `ldstr "x"` | 直接压字符串 |
| 本地化文本 | `ldstr "key"` + `call GetConfig` | 压 `{langPack, key}` 引用，事后解析成中文 |
| `List<T>` | `newobj List.ctor()` + 循环(`dup`+元素+`Add`) | 累加成 List |
| 数组（逐元素） | `newarr N` + `stelem.*`/`stelem.any` | ArrayBuilder 逐元素填 |
| 数组（内联） | `ldtoken` + `InitializeArray` | 从 PE `.data` 节读字节，按 int32/int16/byte 小端解 |
| 局部变量 | `stloc`/`ldloc` | 复杂表先把 List/数组存局部变量再填充 |
| 目标记录 | `newobj Item.ctor(N)` + `_dataArray.Add` | 按 ctor 字段名生成 dict，登记为一条记录 |

**两个关键设计**：

1. **字段名映射**（`FieldMapper`）：优先用 `ConfigItem` 子类的 `public readonly` 字段声明顺序；少数复杂表（Character/Organization/MapPickups 等）字段定义因 Cecil 加载方式取不到时，fallback 用 ctor 参数名（PascalCase 化），仍能拿到有意义的字段名。

2. **`_dataArray.Add` 精确识别**：用 `DataArrayRef` 标记追踪 `ldfld _dataArray`——当 Add 的 list 操作数是这个标记时，即为"一条记录完成"。这避免了复杂表里嵌套结构 dict 被误判为记录（Character/OrganizationMember 等复杂表曾因此多提取数倍）。

**跨 dll 引用解析**：用 `DefaultAssemblyResolver` 把 `Backend\` 加入搜索路径，让 Cecil 能解析 `GameData.Utilities` 等同目录 dll 的类型引用（否则约 10 张表的 item 类型 Resolve 失败）。

## 文件结构

```
config-extractor/
├── config-extractor.csproj    net8.0 + Mono.Cecil 0.11.5
├── Program.cs                 入口：定位游戏、提取、校验、写 JSON
├── IlValueExtractor.cs         IL 栈式模拟核心（识别多种模式）
├── FieldMapper.cs             字段名 ↔ ctor 参数位置映射
├── LocalizationResolver.cs    解析 Language_CN/*.txt 和 ConfigRefNameMapping/*.ref.txt
├── README.md
└── config/                    提取产物（gitignore，默认在工作区根生成）
```

## 下一步（可选优化）

批量提取已完整可用。若要进一步提升：

1. **降低警告数**：68 张表有警告，主要是少数罕见 IL 模式（如枚举 box/unbox、特殊 newobj）未覆盖。逐个深挖这些表的 warning 即可补齐，但当前记录数已全部正确，警告多为个别字段值退化。
2. **CSV 导出**：加扁平化导出器便于 Excel 对比。
3. **属性 id 映射**：`PropertyAndValueAndModifyType.Type` 是 `ECharacterPropertyReferencedType`（如 0=膂力、112=膂力恢复），可加映射表让输出更可读。

## 限制

- 仅静态解析 IL，**不执行 mod 注入的运行时配置**（`_extraDataMap`）——那是 mod 运行时才有的，dll 里不存在。
- 文本来自当前游戏的 `Language_CN`，换语言需 `-l` 参数。
- 游戏更新（buildid 变）后需重新跑（IL 可能变），但工具本身不用改。
