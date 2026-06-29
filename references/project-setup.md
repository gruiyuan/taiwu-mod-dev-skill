# 独立 Mod 工程搭建

如何从零建一个能编译、能跑的独立 mod 工程。直接引用游戏目录里的真实 DLL。

## 前置：先拿到游戏目录

本 skill 阶段一已说明如何定位游戏根目录（注册表 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350` 的 `InstallLocation`，或用户手填）。下文用变量 `$GameDir` 指代它。

```powershell
# 从注册表读（PowerShell）
$GameDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350').InstallLocation
$Managed = Join-Path $GameDir 'The Scroll Of Taiwu_Data\Managed'
```

## 工程结构（独立项目，和任何 mod 仓库无关）

```
MyMod/
├── src/
│   └── MyMod.Backend/
│       ├── MyMod.Backend.csproj
│       └── BackendPlugin.cs
├── mod/                          ← 整个目录复制到 <游戏>/Mod/MyMod/
│   ├── Config.Lua
│   └── Plugins/
├── refs/                         ← 从游戏目录拷贝来的引用 DLL（见下）
│   └── (GameData.dll, TaiwuModdingLib.dll, 0Harmony.dll ...)
└── libs/                         ← 可选：mod 自己的第三方依赖
```

## 关键决策：引用 DLL 的两种方式

### 先确定端侧对应的 DLL 目录

游戏前后端是两个进程，DLL 分两个目录（见 SKILL.md 阶段一 1c）：

- **前端 mod** 引用 `<游戏根>\The Scroll Of Taiwu_Data\Managed\`（`Assembly-CSharp.dll` 在此）
- **后端 mod** 引用 `<游戏根>\Backend\`（`GameData.dll` 在此，含 `TaiwuDomain`/`DomainManager`）

> ⚠️ 后端 mod 不要去 `Managed\` 找 `GameData.dll`——那里只有 `GameData.Shared.dll`（共享类型库），反编译/引用它会缺核心域类。`GameData.dll` 只在 `Backend\`。
> `TaiwuModdingLib.dll` / `0Harmony.dll` 两个目录各有一份，**引用目标端侧目录那份**（后端 mod 引 Backend 的，前端 mod 引 Managed 的）。

### 方式 A：直接 HintPath 指向游戏目录（推荐，零拷贝）

csproj 里 `<HintPath>` 直接指向游戏目录的真实 DLL。**不拷贝、不进版本控制**，只在你本机能编译：

```xml
<!-- 后端 mod 示例：GameData.dll 在 Backend\ -->
<Reference Include="GameData">
  <HintPath>$([MSBuild]::GetRegistryValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350','InstallLocation'))\Backend\GameData.dll</HintPath>
  <Private>false</Private>
</Reference>
```

优点：DLL 总是和当前游戏版本一致，游戏更新后不用手动同步。缺点：换机器/CI 需要游戏也装在那。

> 上面的 `GetRegistryValue` 让 csproj 自动从注册表取游戏根路径，连变量都不用设。

### 方式 B：从游戏目录拷贝 DLL 到 refs/（便携、可入库）

把要引用的 DLL 从游戏目录拷一份到工程的 `refs/`，csproj 指向 `refs/`。**适合多人协作或想脱离游戏目录编译**。

```powershell
# 后端 mod 一次性拷贝（PowerShell）—— 注意是 Backend\ 目录
$GameDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350').InstallLocation
$Backend = Join-Path $GameDir 'Backend'
New-Item -ItemType Directory -Force -Path 'refs' | Out-Null
Copy-Item "$Backend\GameData.dll","$Backend\TaiwuModdingLib.dll","$Backend\0Harmony.dll" 'refs\'
# 后端按域拆分的 dll，用到哪个拷哪个，例如：
# Copy-Item "$Backend\GameData.Common.dll" 'refs\'

# 前端 mod 则从 Managed\ 拷：
# $Managed = Join-Path $GameDir 'The Scroll Of Taiwu_Data\Managed'
# Copy-Item "$Managed\Assembly-CSharp.dll","$Managed\TaiwuModdingLib.dll","$Managed\0Harmony.dll" 'refs\'
```

```xml
<Reference Include="GameData">
  <HintPath>..\..\refs\GameData.dll</HintPath>
  <Private>false</Private>
</Reference>
```

注意：`refs/` 里的 DLL 不会随游戏自动更新，**游戏更新后要重新拷贝**（可写个 `restore-refs.ps1` 脚本固定这个动作）。`refs/` 建议加进 `.gitignore`，或用 git-lfs，避免提交大文件。

> `refs/` 里的 DLL 仅供参考编译，**绝不能**打包进 mod 的 `Plugins/`（见下"Private=false"）。

## TargetFramework：前后端必须分开选（重要）

太吾前后端用不同的 .NET 运行时，所以 mod 工程的 `TargetFramework` 必须按端侧选：

| 端侧 | 运行时 | TargetFramework |
|---|---|---|
| **后端** | .NET 8（`Backend\GameData.runtimeconfig.json` 的 `tfm: net8.0`） | **`net8.0`** |
| **前端** | Unity Mono/IL2CPP | **`netstandard2.1`** |

> 后端用 `netstandard2.1` 也能编译并加载（net8 能加载 netstandard2.1），但会丢失 .NET 8 API，且不是游戏运行时的原生目标。正确做法是后端 `net8.0`。
> 最低需要 .NET 8 SDK（见 SKILL.md 阶段一 1a）。高版本 SDK（如 10）能编 `net8.0`，无需装 8 的 SDK。
> 跨端 mod 两个工程各设各的 TFM，不要共用。

## 最小 csproj（后端 mod，方式 A 版）

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- 后端 mod：net8.0（后端跑在 .NET 8 上）。前端 mod 改成 netstandard2.1 -->
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>MyMod.Backend</AssemblyName>
    <RootNamespace>MyMod.Backend</RootNamespace>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <!-- 从注册表取游戏根目录 -->
    <TaiwuGameDir>$([MSBuild]::GetRegistryValue('HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350','InstallLocation'))</TaiwuGameDir>
    <!-- 后端 mod 引 Backend\；前端 mod 改成 ...\The Scroll Of Taiwu_Data\Managed -->
    <TaiwuSideDir>$(TaiwuGameDir)\Backend</TaiwuSideDir>
  </PropertyGroup>

  <ItemGroup>
    <!-- 后端主程序集（含 TaiwuDomain、DomainManager）—— 注意来自 Backend\ -->
    <Reference Include="GameData">
      <HintPath>$(TaiwuSideDir)\GameData.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="GameData.Common">
      <HintPath>$(TaiwuSideDir)\GameData.Common.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="TaiwuModdingLib">
      <HintPath>$(TaiwuSideDir)\TaiwuModdingLib.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="0Harmony">
      <HintPath>$(TaiwuSideDir)\0Harmony.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- 让构建产物直接落到 mod 的 Plugins/ -->
  <PropertyGroup>
    <OutputPath>..\..\mod\Plugins\</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

</Project>
```

> 前端 mod：`TargetFramework` 改成 `netstandard2.1`，`TaiwuSideDir` 改为 `$(TaiwuGameDir)\The Scroll Of Taiwu_Data\Managed`，引用项里的 `GameData` 换成 `Assembly-CSharp` + 需要的 `UnityEngine.*`。

### `<Private>false</Private>` 是重中之重

游戏进程已经加载了这些 DLL。你的 mod 产物里**绝不能**再带一份——否则出现两个 `GameData.dll`，里面的同名类型身份不兼容，运行时崩（`TypeLoadException`/`InvalidCastException`/Harmony patch 全失效）。所有游戏 DLL 引用都必须 `<Private>false</Private>`，让产物里只留你自己的代码。

## 编译与部署

```powershell
# 1. 编译（产物自动落到 mod/Plugins/）
dotnet build src\MyMod.Backend\MyMod.Backend.csproj -c Release

# 2. 复制 mod 目录到游戏
$GameDir = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350').InstallLocation
$Dest = Join-Path $GameDir 'Mod\MyMod'
Copy-Item 'mod\*' $Dest -Recurse -Force
```

部署后：
```
<游戏>/Mod/MyMod/
├── Config.Lua
└── Plugins/
    └── MyMod.Backend.dll
```

## about internal 成员（重要取舍）

太吾很多有用方法是 `internal`/`private`。三条路：

1. **优先：只 patch public 方法**，在 Prefix/Postfix 里用 `__instance` 调 public API。最稳，不随版本崩。**独立 mod 默认走这条。**
2. **反射**：`AccessTools.Method(typeof(X), "PrivateMethod")` + `.Invoke()`。能用但失去编译期检查，改名就崩。
3. **Publicizer**（`Krafs.Publicizer`，公开 NuGet 包）：
   ```xml
   <PackageReference Include="Krafs.Publicizer" PrivateAssets="all" ExcludeAssets="runtime" />
   <Publicize Include="GameData" IncludeCompilerGeneratedMembers="false" />
   ```
   它生成编译期假 DLL 让 `nameof()` 强类型引用 internal，运行时仍是真游戏的真访问权限（Harmony 能 patch 任何方法，Publicizer 只是让编译期引用干净）。需要大量碰 internal 时才用，能用 public 别用。

## 常见编译/运行问题

| 现象 | 原因 / 处理 |
|---|---|
| `Could not load file or assembly GameData` | 产物带了游戏 DLL。检查所有游戏引用 `<Private>false</Private>`。 |
| patch 没生效 | 先确认 `Initialize()` 里调了 `PatchAll`（见 backend-harmony.md），再查日志 Harmony 报错；多为签名没对上。 |
| `TypeLoadException` / 类型身份冲突 | 同上，产物混入了游戏已有 DLL。 |
| 编译报 `GameData.Domains.Xxx` 找不到 | 后端被拆成多个 DLL，加对应 `GameData.Domains.Xxx.dll` 引用。 |
| HintPath 报找不到 DLL | 游戏没装、或注册表路径变了；用方式 B 拷贝 dll 到 `refs/` 改为本地引用即可。 |
| 后端 mod 反编译/引用到 `GameData.Shared` 而非 `GameData`，缺 `TaiwuDomain` 等核心类 | 引用错了目录：后端主 dll 是 `Backend\GameData.dll`，不在 `Managed\`。`Managed\` 下只有 `GameData.Shared.dll`（共享类型库）。改成 `Backend\` 路径即可。 |
| 前端 mod 误引了后端 dll（或反之） | 端侧目录搞混。前端引 `Managed\Assembly-CSharp.dll`，后端引 `Backend\GameData.dll`，`TaiwuModdingLib`/`0Harmony` 各引目标端侧目录那份。 |
