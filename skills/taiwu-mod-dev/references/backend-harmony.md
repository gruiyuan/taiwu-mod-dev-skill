# 后端 Harmony Patch 实战

后端逻辑（数值、规则、战斗、AI、事件）全在后端主程序集 **`Backend\GameData.dll`**（含 `TaiwuDomain`、`DomainManager`）。改它用 HarmonyLib。

> 本 skill 阶段二已说明如何反编译出可读源码（反编译目标是 `Backend\GameData.dll`，**不是** `Managed\GameData.Shared.dll`——后者是共享类型库，不含域逻辑）。下文"反编译源码""在源码里 Grep"均指工作区 `decompiled/<buildid>/` 下反编译 `Backend\GameData.dll` 得到的产物。

## 前置：先读源码定位目标

写任何 patch 之前，**先在反编译出的后端源码里 Grep 到目标方法，抄下真实签名**（类名、方法名、每个参数的类型）。Harmony 靠签名匹配，错一个类型就静默不生效，日志里才有线索。

例：想让太吾免疫中毒，在后端源码的 `GameData.Domains.Character/Character.cs` 里搜到：
- `void SetPoisoned(PoisonInts poisoned)`
- `bool ChangePoisoned(DataContext context, sbyte poisonType, sbyte poisonType2, int delta)`
- `void DirectlyChangePoisoned(PoisonInts delta)`

这些就是 patch 目标。

## 安装 / 卸载 Harmony

集中在一个静态类里管，在 `BackendPlugin.Initialize` 调 `Install(ModIdStr)`、`Dispose` 调 `Uninstall()`。`ModIdStr` 来自基类，作为 Harmony 的 owner id（多 mod 不冲突、可干净卸载）：

```csharp
using HarmonyLib;

internal static class MyPatches
{
    private static Harmony? s_harmony;

    internal static void Install(string modId)
    {
        s_harmony = new Harmony(modId + ".MyMod");
        s_harmony.PatchAll(typeof(MyPatches).Assembly);  // 扫描当前程序集所有 [HarmonyPatch]
    }

    internal static void Uninstall()
    {
        s_harmony?.UnpatchSelf();
        s_harmony = null;
    }
}
```

`PatchAll(assembly)` 会找当前 mod dll 里所有标了 `[HarmonyPatch]` 的类。不要用全局 `PatchAll()`（会扫到别的程序集，危险）。

## 三种 patch 写法

### Prefix（执行前；可拦截原方法）

`return false` 跳过原方法，`return true`（或不返回）执行原方法。`ref` 参数可在原方法执行前改写值：

```csharp
[HarmonyPatch(typeof(GameData.Domains.Character.Character),
              nameof(GameData.Domains.Character.Character.ChangePoisoned),
              new[] { typeof(DataContext), typeof(sbyte), typeof(sbyte), typeof(int) })]
internal static class ChangePoisonedPatch
{
    // 方法名必须是 Prefix；参数名要与原方法对应，__instance 是原方法 this
    private static bool Prefix(GameData.Domains.Character.Character __instance,
                               sbyte poisonType, int delta)
    {
        // 阻止对太吾增加毒素
        return !IsTaiwu(__instance) || delta <= 0;  // 返回 false 跳过原方法
    }
}
```

### Postfix（执行后；可改返回值）

用 `ref __result` 改原方法返回值：

```csharp
[HarmonyPatch(typeof(GameData.Domains.Character.Character),
              nameof(GameData.Domains.Character.Character.GetMindImmunity))]
internal static class MindImmunityPatch
{
    private static void Postfix(GameData.Domains.Character.Character __instance, ref bool __result)
    {
        if (IsTaiwu(__instance)) __result = true;
    }
}
```

### Transpiler（改 IL；最强大最脆弱）

需要 `using System.Reflection.Emit;`。当 Prefix/Postfix 不够（比如要插在方法中间、改某个局部调用）时才用。太吾反编译能看到 IL 对应的 C#，对得上方法调用。要点：用 `AccessTools.Method` 解析目标方法引用，遍历 `IEnumerable<CodeInstruction>` 时按需注入/替换，**完成后校验注入次数**，次数不对直接抛异常（比静默错强）：

```csharp
private static readonly MethodInfo TargetCall =
    AccessTools.Method(typeof(X), nameof(X.SomeMethod), new[] { typeof(A), typeof(B) })
    ?? throw new MissingMethodException(nameof(X), nameof(X.SomeMethod));

private static readonly MethodInfo InjectCall =
    AccessTools.Method(typeof(MyRules), nameof(MyRules.MyShaping), new[] { typeof(X) })
    ?? throw new MissingMethodException(nameof(MyRules), nameof(MyRules.MyShaping));

private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
{
    int hits = 0;
    foreach (var ins in instructions)
    {
        yield return ins;
        if (!ins.Calls(TargetCall)) continue;
        if (++hits > 1) continue;
        yield return new CodeInstruction(OpCodes.Ldarg_0);   // 按调用约定压参数
        yield return new CodeInstruction(OpCodes.Call, InjectCall);
    }
    if (hits != 1)
        throw new InvalidOperationException($"expected exactly 1 call, found {hits}");
}
```

Transpiler 调试难，写完务必看日志确认没抛异常。

## 太吾里常用的运行时 API

判断"这个角色是不是太吾"（最常用的 gate）：
```csharp
int taiwuId = DomainManager.Taiwu.GetTaiwuCharId();
bool isTaiwu = character.GetId() == taiwuId;
// 或直接拿太吾对象：
var taiwu = DomainManager.Taiwu.GetTaiwu();
```

`DomainManager`（`GameData.Domains.DomainManager`）是访问所有后端域的入口：`DomainManager.Taiwu`、`DomainManager.Character`、`DomainManager.Combat`、`DomainManager.Map`、`DomainManager.Item`、`DomainManager.Mod` 等。

**`DataContext`**：太吾后端绝大多数写操作要带一个 `DataContext context` 参数，它记录"这次修改是哪个 mod/哪个事件触发的"，用于存档和依赖追踪。patch 拿到 `context` 就直接透传，**不要自己 new**（除非你确实要在某个明确的事务上下文里主动改数据，那需要了解 `DataContextManager`）。

## 反编译里查 patch 目标的方法

- 想拦截某个行为 → 先想"玩家做这个动作，游戏最终调哪个方法改数据"，往数据写入处找（带 `Set*`/`Change*`/`Add*` 的方法）。
- 想改计算结果 → 找 `Get*`/`Calc*` 方法，用 Postfix 改 `__result`。
- 多个重载 → `nameof` + 显式参数类型数组，否则 Harmony 可能匹配错重载。
- 域拆分后方法可能在 `GameData.Domains.<域>.dll` 对应的反编译子目录，但 C# 里都用同一个完整命名空间引用。

## 稳定性与防冲突

- **Harmony owner id 唯一**：用 `ModIdStr + 后缀`，卸载时 `UnpatchSelf()` 只摘自己的，不碰别的 mod。
- **patch 窄而准**：Prefix/Postfix 里先用 `IsTaiwu(...)` 之类 gate 早退，避免影响所有角色/NPC/敌人。
- **优先 patch public 方法**（见 project-setup.md 的 internal 取舍）。
- **别在 patch 里做重活**：每次原方法调用都触发，patch 体要轻；耗时操作异步出去。
- **`ArgumentType` 用于 ref/out 参数**：重载里 `ref` 参数要用 `new[]{ ArgumentType.Normal, ArgumentType.Ref }` 声明。

## 后端日志

后端在独立进程，`UnityEngine.Debug.Log` 在后端不一定可用。后端日志看：
- `%USERPROFILE%\AppData\LocalLow\Conchship\The Scroll of Taiwu\Player.log`（`Player-prev.log` 是上次启动的）
- 游戏目录 `Logs/`

太吾后端有自己的日志工具，可在反编译里搜 `class.*Logger` / `DebugUtility` 找现成的；patch 里抛异常会被 Harmony 捕获并写进日志（带 owner id），是排查 patch 失效的第一线索。
