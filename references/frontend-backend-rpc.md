# 前后端通信（Mod RPC）

太吾前端（Unity 进程）和后端（独立进程）是分离的。**前端 mod 不能直接访问后端的 `DomainManager`**。当你在后端用 `AddModMethod` 注册了一个游戏原本没有的自定义方法、又想让前端调用它时，必须通过这套 RPC 机制跨进程调用。

> 何时需要这套机制：**当你新增了游戏原本没有的后端接口**（在后端用 `AddModMethod` 注册了一个自定义方法），前端要调用这个新接口时。注意区分——前端访问游戏**已有的**后端数据（走游戏自带的查询/UI 数据流）不需要 mod 自己写 RPC；只有调用你自己**新加的**后端方法时才用。纯后端 mod（只 patch 后端逻辑）和纯前端 mod（只改 UI）都用不到。

## 核心模型

```
┌──────── 前端进程 (Unity) ────────┐         ┌──────── 后端进程 ────────┐
│ FrontendPlugin                    │         │ BackendPlugin             │
│                                   │         │   Initialize:             │
│   BackendClient.Foo(args, cb)  ───┼──IPC───>│     DomainManager.Mod     │
│        │                          │         │       .AddModMethod(      │
│        │ AsyncCall.CallModMethod  │         │         modId, "Foo",     │
│        │   用 SerializableModData │         │         委托)             │
│        │   打包参数               │         │                           │
│   cb(success, reason)  <──────────┼──IPC────│   委托(context, param):    │
│        │                          │         │     后端真实逻辑           │
│   更新 UI                          │         │     返回 SerializableModData│
└───────────────────────────────────┘         └───────────────────────────┘
```

四个要点：
1. **两端各自一个 `TaiwuRemakePlugin`**（前端引 `Managed\` 的 dll，后端引 `Backend\` 的 dll，见 project-setup.md）。
2. **后端注册方法**：`DomainManager.Mod.AddModMethod(modId, "方法名", 委托)`。
3. **前端调用方法**：`AsyncCall.CallModMethod*(modId, "方法名", 参数, 回调)`。
4. **数据载体**：`SerializableModData`（来自 `GameData.Shared.dll` 的 `GameData.Domains.Mod` 命名空间）——一个 key-value 容器，是跨进程序列化的唯一中介。**前后端用一致的字符串 key 契约**交换数据。

## 后端：注册方法（服务端）

在 `BackendPlugin.Initialize` 里注册。方法签名必须是 `DomainManager.Mod.AddModMethod` 的四种重载之一（见下表）：

```csharp
// BackendPlugin.cs（后端）
public override void Initialize()
{
    // ModIdStr 是基类字段，当前 mod 的运行时标识；RPC 方法名靠它定位
    DomainManager.Mod.AddModMethod(
        ModIdStr, "DoSomething",
        (Func<DataContext, SerializableModData, SerializableModData>)DoSomething);
}

// 后端真实逻辑。param 是前端传来的 SerializableModData，ret 是要返回的 SerializableModData
private static SerializableModData DoSomething(DataContext context, SerializableModData param)
{
    var ret = new SerializableModData();
    int someInput = 0;
    if (!param.Get("InputKey", out someInput))   // 读前端传的参数
        return Fail(ret, "缺少 InputKey");

    // ... 在后端用 DomainManager 干活 ...
    ret.Set("Success", true);
    ret.Set("Reason", string.Empty);
    ret.Set("ResultKey", someInput * 2);
    return ret;
}

private static SerializableModData Fail(SerializableModData ret, string reason)
{
    ret.Set("Success", false);
    ret.Set("Reason", reason);
    return ret;
}
```

要点：
- **方法名（`"DoSomething"`）和参数 key（`"InputKey"`/`"ResultKey"`）是前后端约定的字符串契约**。建议在前后端各定义一份 `const string` 常量保持同步。
- `DataContext context` 是后端事务上下文，透传即可，**不要自己 new**。
- 返回值约定：成功/失败用 `Success`(bool) + `Reason`(string) 表达，前端按这个约定解析。这是社区常见做法（如「手动存档[天心帷幕正式版]」，FileId `2871612756`，需自行反编译查看）。

## 前端：调用方法（客户端）

前端用 `AsyncCall` 异步调用，**必须用回调接结果**（跨进程是异步的）：

```csharp
// FrontendPlugin 侧的 BackendClient
internal static void DoSomething(int input, Action<bool, string, int> callback)
{
    var param = new SerializableModData();
    param.Set("InputKey", input);   // 打包参数

    // 关键调用：CallModMethodWithParamAndRet
    AsyncCall.CallModMethodWithParamAndRet(
        requestHandler: null,
        modIdStr: ModMain.ModId,            // 注意：用前端 mod 的 ModId（见下"ModId 同步"）
        methodName: "DoSomething",
        parameter: param,
        callback: (offset, dataPool) => OnResult(offset, dataPool, callback));
}

private static void OnResult(int offset, RawDataPool dataPool, Action<bool, string, int> callback)
{
    SerializableModData ret = null;
    SerializerHolder<SerializableModData>.Deserialize(dataPool, offset, ref ret);

    bool success = false;
    string reason = string.Empty;
    int result = 0;
    if (ret != null)
    {
        ret.Get("Success", out success);     // 注意：反编译里常写成 ref（ilspycmd 的 out/ref 互转）
        ret.Get("Reason", ref reason);
        if (success) ret.Get("ResultKey", ref result);
    }
    callback?.Invoke(success, reason, result);
}
```

> **关于 `out` vs `ref`**：ilspycmd 反编译常把 `Get(string, out T)` 显示成 `Get(string, ref T)`——这是反编译工具的 out/ref 互转，**写代码时用 `ref` 或 `out` 都能编译**（C# 里调用处两者等价于传引用）。照搬反编译的 `ref` 即可。

## 四种 RPC 变体（前后端配对）

按"要不要参数 / 要不要返回值"，有四种注册+调用组合，前后端必须配对：

| 后端注册委托类型 | 后端/前端调用名 | 前端调用 (`AsyncCall.`) | 用途 |
|---|---|---|---|
| `Func<DataContext, SerializableModData, SerializableModData>` | `CallModMethodWithParamAndRet` | `CallModMethodWithParamAndRet` | **最常用**，带参+有返回 |
| `Func<DataContext, SerializableModData>` | `CallModMethodWithRet` | `CallModMethodWithRet` | 无参+有返回 |
| `Action<DataContext, SerializableModData>` | `CallModMethodWithParam` | `CallModMethodWithParam` | 带参+无返回 |
| `Action<DataContext>` | `CallModMethod` | `CallModMethod` | 无参+无返回 |

> 后端注册和前端调用的方法名、委托签名必须严格配对，否则调用静默失败或报错。

## ModId 同步问题（重要）

前端调 `AsyncCall.CallModMethod*(modId, ...)` 里的 `modId` 要和后端 `AddModMethod(modId, ...)` 里的一致，才能路由到对应方法。但前端和后端是两个不同的 `TaiwuRemakePlugin` 实例（各自的 `ModIdStr`）。

社区做法（见上文手动存档 mod）：前端 mod 把自己的 `ModId` 暴露成静态字段，前端 RPC 客户端直接用它。因为**游戏保证同一 mod 的前端和后端插件 ModIdStr 相同**（都来自同一个 mod 目录）。

```csharp
// FrontendPlugin.cs
internal static string ModId { get; private set; }
public override void Initialize() { ModId = ModIdStr; /* ... */ }
```

前端 RPC 客户端：`AsyncCall.CallModMethodWithParamAndRet(null, FrontendPlugin.ModId, ...)`。

> 如果你不确定 ModIdStr 是否真的一致，可在两端 `Initialize` 里 `Debug.Log(ModIdStr)` 打到 Player.log 对比。

## SerializableModData：数据载体

定义在 **`GameData.Shared.dll`**（`GameData.Domains.Mod.SerializableModData`）——这就是 `GameData.Shared` 的核心用途：**前后端共享的类型库**。前后端各自引用**本端目录**那份 `GameData.Shared.dll`（后端引 Backend 的，前端引 Managed 的）。

它本质是 5 个字典的集合，支持的类型：

| 类型 | Set | Get |
|---|---|---|
| `int` | `Set(key, int)` | `Get(key, out int)` |
| `float` | `Set(key, float)` | `Get(key, out float)` |
| `bool` | `Set(key, bool)` | `Get(key, out bool)` |
| `string` | `Set(key, string)` | `Get(key, out string)` |
| `ISerializableGameData` | `Set<T>(key, T)` | `Get<T>(key, out T)` |

- **只能放这 5 种类型**。要传复杂结构，拆成多个基本类型 key（如 `Timestamp0`/`Timestamp1`... + `Count` 计数），或自定义实现 `ISerializableGameData` 的类。
- 传 `long`/`sbyte` 等：转成 `int`（如 `(int)sbyteValue`）或 `string`（如 `timestamp.ToString()`），接收方再 `long.TryParse`。手动存档 mod 就把 timestamp 当 string 传。
- `Get` 返回 `bool`（key 是否存在），用 `ref`/`out` 接值。

## 工程上的引用（两端都要）

每个端除引用本端目录的 `TaiwuModdingLib.dll`/`0Harmony.dll` 外，**都要引用本端目录的 `GameData.Shared.dll`**（RPC 类型在这）：

- 前端 mod：`Managed\` 的 `Assembly-CSharp.dll` + `GameData.Shared.dll` + `TaiwuModdingLib.dll` + `0Harmony.dll`
- 后端 mod：`Backend\` 的 `GameData.dll` + `GameData.Shared.dll` + `TaiwuModdingLib.dll` + `0Harmony.dll`

都 `<Private>false</Private>`。具体见 project-setup.md。

## 调试

- **调用没反应**：检查方法名/key 契约前后端是否完全一致；检查前端用的 `modId` 是否和后端注册的一致。
- **回调不触发**：RPC 是异步的，确认 `AsyncCall.CallModMethod*` 的回调签名对了（`AsyncMethodCallbackDelegate`，参数是 `int offset, RawDataPool dataPool`）。
- **结果解析错**：反序列化用 `SerializerHolder<SerializableModData>.Deserialize(dataPool, offset, ref val)`，别忘了 null 检查。
- 日志看 `Player.log`（前端）和后端日志；后端方法里抛异常会被 RPC 框架捕获，可能不直接崩，但返回为空。

## 完整范例参考（需要时再看）

遇到 RPC 写法上的疑问时，可以参考创意工坊的「手动存档[天心帷幕正式版]」（FileId `2871612756`，带 .pdb，质量高）。先在 `<Steam>\steamapps\workshop\content\838350\2871612756\` 找有没有已订阅下载的，没有就让用户订阅一下；然后按 SKILL.md 阶段二 2f 的方式反编译两个 dll。重点可看：
- 后端：方法注册 + 参数解析 + 成功/失败（`Success`/`Reason`）约定。
- 前端：RPC 客户端的参数打包 + 回调解包。

> 没遇到具体疑问就不必特意看——本文档的示例已覆盖常见用法。
