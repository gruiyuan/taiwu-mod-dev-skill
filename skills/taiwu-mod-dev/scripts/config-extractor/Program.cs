using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using Mono.Cecil;
using TaiwuConfigExtractor;

// ── 解析参数 ──
// 默认行为：不带任何参数 = 提取全部表（批量模式）。
// -t <表名>：只提取指定表（单表模式，带详细校验输出）。
string? gameDir = null;
string? outDir = null;
string? tableName = null;   // null = 批量模式；非 null = 单表模式
string lang = "CN";
for (int i = 0; i < args.Length; i++)
{
    if ((args[i] == "-g" || args[i] == "--game") && i + 1 < args.Length) gameDir = args[++i];
    else if ((args[i] == "-o" || args[i] == "--output") && i + 1 < args.Length) outDir = args[++i];
    else if ((args[i] == "-t" || args[i] == "--table") && i + 1 < args.Length) tableName = args[++i];
    else if ((args[i] == "-l" || args[i] == "--lang") && i + 1 < args.Length) lang = args[++i];
    else if (args[i] == "--all" || args[i] == "-a") tableName = null;   // 显式全量（与默认相同）
}

// ── 定位游戏目录 ──
gameDir ??= LocateGameDir() ?? throw new InvalidOperationException(
    "找不到游戏目录。请用 -g 指定，例如：dotnet run -- -g \"D:\\...\\The Scroll Of Taiwu\"");
Console.WriteLine($"游戏目录: {gameDir}");

var backendDir = Path.Combine(gameDir, "Backend");
var sharedDll = Path.Combine(backendDir, "GameData.Shared.dll");
var streamingAssets = Path.Combine(gameDir, "The Scroll of Taiwu_Data", "StreamingAssets");
if (!File.Exists(sharedDll)) throw new FileNotFoundException($"找不到 {sharedDll}");

// buildId 提前读取：默认输出目录用它作子目录（与百晓册知识库 knowledge-base/<buildid>/ 同源锚点）
var buildId = ReadBuildId(gameDir) ?? "unknown";
outDir ??= Path.Combine(Directory.GetCurrentDirectory(), "config", buildId);
Directory.CreateDirectory(outDir);
Console.WriteLine($"输出目录: {outDir}");
Console.WriteLine($"buildid: {buildId}");
Console.WriteLine($"语言: Language_{lang}");
Console.WriteLine();

// ── 加载 dll（只加载一次，配程序集解析器以解析跨 dll 引用）──
Console.WriteLine($"加载 {Path.GetFileName(sharedDll)} ...");
var loadSw = Stopwatch.StartNew();
var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(backendDir);   // 让 Cecil 能找到 GameData.Utilities 等同目录 dll
var asm = AssemblyDefinition.ReadAssembly(sharedDll, new ReaderParameters { AssemblyResolver = resolver });
Console.WriteLine($"  加载耗时 {loadSw.ElapsedMilliseconds}ms");
Console.WriteLine();

var jsonOpts = new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};

if (tableName == null)
{
    RunAllTables(asm, streamingAssets, outDir, lang, buildId, jsonOpts);
}
else
{
    RunOneTable(tableName, asm, streamingAssets, outDir, lang, buildId, jsonOpts, verbose: true);
}

// ════════════════════════════════════════════════════════════════
// 批量模式：遍历所有 ConfigData 子类
// ════════════════════════════════════════════════════════════════
void RunAllTables(AssemblyDefinition asm, string streamingAssets, string outDir, string lang, string? buildId, JsonSerializerOptions jsonOpts)
{
    // 扫描所有 ConfigData<TItem, TKey> 子类
    var tables = new List<(TypeDefinition table, TypeReference itemRef)>();
    foreach (var t in asm.MainModule.Types)
    {
        if (t.BaseType is GenericInstanceType git && git.ElementType.FullName == "Config.Common.ConfigData`2")
        {
            tables.Add((t, git.GenericArguments[0]));
        }
    }
    tables.Sort((a, b) => string.Compare(a.table.Name, b.table.Name, StringComparison.Ordinal));
    Console.WriteLine($"=== 批量模式：共 {tables.Count} 个配置表 ===");
    Console.WriteLine();

    var totalSw = Stopwatch.StartNew();
    int okCount = 0, failCount = 0, warnCount = 0;
    long totalRecords = 0;
    long totalBytes = 0;
    var failures = new List<(string name, string reason)>();
    var warnings = new List<string>();
    // 每张表的摘要，供 _manifest.json 的 tables 字段使用（不能直接序列化 Cecil 的 TypeDefinition）
    var tableEntries = new List<object>();

    int idx = 0;
    foreach (var (tableType, itemRef) in tables)
    {
        idx++;
        var name = tableType.Name;
        Console.Write($"[{idx,3}/{tables.Count}] {name,-40} ");
        try
        {
            var sw = Stopwatch.StartNew();
            var itemDef = itemRef.Resolve() ?? throw new InvalidOperationException($"无法解析 item 类型 {itemRef.FullName}");
            var (records, diag, fields, refOk) = ExtractTableCore(tableType, itemDef, streamingAssets, lang);
            // 写文件
            var outPath = Path.Combine(outDir, name + ".json");
            var bytes = WriteTableJson(outPath, name, buildId, fields, records, jsonOpts);
            sw.Stop();

            bool ok = refOk;
            if (diag.WarningCount > 0) { warnCount++; warnings.Add($"{name}: {diag.WarningCount} 警告"); }
            if (ok) okCount++; else failCount++;
            totalRecords += records.Count;
            totalBytes += bytes;
            tableEntries.Add(new
            {
                name,
                recordCount = records.Count,
                bytes,
                file = name + ".json",
                ok,
                warnings = diag.WarningCount,
            });
            Console.WriteLine($"{records.Count,5} 条  {bytes/1024.0,7:F1} KB  {sw.ElapsedMilliseconds,4}ms  {StatusMark(ok, diag.WarningCount)}");
        }
        catch (Exception ex)
        {
            failCount++;
            failures.Add((name, ex.GetType().Name + ": " + ex.Message));
            Console.WriteLine($"抛错  ✗  {ex.GetType().Name}: {Truncate(ex.Message, 60)}");
        }
    }
    totalSw.Stop();

    Console.WriteLine();
    Console.WriteLine("═══════════════════════════════════════════════════");
    Console.WriteLine($"  表总数:     {tables.Count}");
    Console.WriteLine($"  成功:       {okCount}");
    Console.WriteLine($"  失败:       {failCount}");
    Console.WriteLine($"  有警告:     {warnCount}");
    Console.WriteLine($"  总记录数:   {totalRecords:N0}");
    Console.WriteLine($"  总大小:     {totalBytes/1024.0/1024.0:N2} MB");
    Console.WriteLine($"  总耗时:     {totalSw.Elapsed.TotalSeconds:F2} 秒 ({totalSw.ElapsedMilliseconds} ms)");
    Console.WriteLine($"  平均每表:   {totalSw.Elapsed.TotalMilliseconds / tables.Count:F0} ms");
    Console.WriteLine("═══════════════════════════════════════════════════");

    if (failures.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"失败明细（{failures.Count}）:");
        foreach (var (n, r) in failures) Console.WriteLine($"  {n}: {Truncate(r, 100)}");
    }
    if (warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"警告明细（前 20 / {warnings.Count}）:");
        foreach (var w in warnings.Take(20)) Console.WriteLine("  " + w);
    }

    // 写汇总清单
    var manifestPath = Path.Combine(outDir, "_manifest.json");
    var manifest = new
    {
        gameBuildId = buildId,
        extractedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        totalTables = tables.Count,
        succeeded = okCount,
        failed = failCount,
        withWarnings = warnCount,
        totalRecords,
        totalBytes,
        totalSeconds = totalSw.Elapsed.TotalSeconds,
        tables = tableEntries,
    };
    File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));
    Console.WriteLine();
    Console.WriteLine($"汇总清单: {manifestPath}");

    if (failCount > 0) Environment.ExitCode = 1;
}

// ════════════════════════════════════════════════════════════════
// 单表模式（详细输出 + 抽查校验）
// ════════════════════════════════════════════════════════════════
void RunOneTable(string tableName, AssemblyDefinition asm, string streamingAssets, string outDir, string lang, string? buildId, JsonSerializerOptions jsonOpts, bool verbose)
{
    var tableType = FindType(asm, tableName) ?? throw new InvalidOperationException($"找不到配置表类 {tableName}");
    // item 类型从基类泛型实参取（比 "tableName+Item" 拼接更可靠）
    TypeReference itemRef = ((GenericInstanceType)tableType.BaseType).GenericArguments[0];
    var itemDef = itemRef.Resolve() ?? throw new InvalidOperationException($"无法解析 item 类型 {itemRef.FullName}");

    if (verbose)
    {
        Console.WriteLine($"表类型: {tableType.FullName}");
        Console.WriteLine($"项类型: {itemDef.FullName}");
    }

    var sw = Stopwatch.StartNew();
    var (records, diag, fields, refOk) = ExtractTableCore(tableType, itemDef, streamingAssets, lang, verbose);
    sw.Stop();

    if (verbose)
    {
        Console.WriteLine($"提取耗时 {sw.ElapsedMilliseconds}ms，共 {records.Count} 条");
        if (diag.Warnings.Count > 0)
        {
            Console.WriteLine($"[警告] {diag.WarningCount} 条（显示前 {diag.Warnings.Count} 条）：");
            foreach (var w in diag.Warnings) Console.WriteLine("  - " + w);
        }
    }

    // 校验：只有 ExtractTableCore 里的通用校验（记录数与 ref.txt 匹配）。
    // 不对任何特定表做硬编码数值抽查——所有表一视同仁。
    bool ok = refOk;

    if (verbose)
    {
        Console.WriteLine();
        Console.WriteLine(ok ? "✅ 全部校验通过" : "❌ 校验未通过，请检查上方 [失败] 项");
    }

    var outPath = Path.Combine(outDir, tableName + ".json");
    var bytes = WriteTableJson(outPath, tableName, buildId, fields, records, jsonOpts);
    if (verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"已写出: {outPath} ({bytes} bytes)");
        if (ok)
        {
            Console.WriteLine("预览前 2 条:");
            for (int i = 0; i < Math.Min(2, records.Count); i++)
                Console.WriteLine(JsonSerializer.Serialize(records[i], jsonOpts));
        }
    }
    if (!ok) Environment.ExitCode = 1;
}

// ════════════════════════════════════════════════════════════════
// 单表提取核心：返回记录、诊断、字段、校验结果
// ════════════════════════════════════════════════════════════════
(List<Dictionary<string, object?>> records, ExtractorDiagnostics diag, IReadOnlyList<string> fields, bool refOk)
    ExtractTableCore(TypeDefinition tableType, TypeDefinition itemDef, string streamingAssets, string lang, bool verbose = false)
{
    var createMethods = tableType.Methods
        .Where(m => m.Name.StartsWith("CreateItems"))
        .OrderBy(m => m.Name, StringComparer.Ordinal)
        .ToList();
    if (createMethods.Count == 0) throw new InvalidOperationException($"{tableType.Name} 没有 CreateItems 方法");

    var diag = new ExtractorDiagnostics();
    var extractor = new ValueExtractor(itemDef, diag);
    var records = new List<Dictionary<string, object?>>();
    foreach (var m in createMethods)
    {
        var recs = extractor.ExtractRecords(m);
        records.AddRange(recs);
    }

    // 解析本地化引用
    var loc = new LocalizationResolver(streamingAssets, lang);
    loc.ResolveRefsInPlace(records);

    // 校验：以"记录数与 ref.txt 一致"为主信号。
    // Name 比对仅作信息提示（很多表的 Name 走自己的 *_language.txt，不等于 ref.txt 存的内部 key，
    // 所以 Name 不匹配不能判提取失败——记录数匹配才是可靠信号）。
    var refMap = loc.LoadRefNameMap(tableType.Name);
    bool refOk = true;
    if (refMap.Count > 0 && records.Count != refMap.Count)
    {
        if (verbose) Console.WriteLine($"[失败] 记录数 {records.Count} != ref.txt 的 {refMap.Count}");
        refOk = false;
    }
    else if (refMap.Count > 0 && verbose)
    {
        Console.WriteLine($"[通过] 记录数 {records.Count} == ref.txt 的 {refMap.Count}");
    }
    // Name 命中率统计（仅信息，用于发现"提取错位"这类系统性问题）
    int nameMatch = 0, nameChecked = 0;
    foreach (var rec in records)
    {
        if (!rec.TryGetValue("TemplateId", out var idObj)) continue;
        int id;
        try { id = Convert.ToInt32(idObj); } catch { continue; }
        if (refMap.TryGetValue(id, out var refName) &&
            rec.TryGetValue("Name", out var nameObj))
        {
            nameChecked++;
            if (nameObj?.ToString() == refName) nameMatch++;
        }
    }
    if (nameChecked >= 10 && nameMatch > 0 && nameMatch < nameChecked)
        diag.Warn($"{tableType.Name}: Name 命中 {nameMatch}/{nameChecked}（部分匹配，Name 可能走不同语言包，记录数已匹配则视为成功）");

    return (records, diag, extractor.ItemMapper.FieldNames, refOk);
}

// 写出一个表的 JSON，返回字节数
long WriteTableJson(string outPath, string tableName, string? buildId, IReadOnlyList<string> fields, List<Dictionary<string, object?>> records, JsonSerializerOptions jsonOpts)
{
    using var fs = File.Create(outPath);
    using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    w.WriteStartObject();
    w.WriteString("$table", tableName);
    w.WriteString("$gameBuildId", buildId);
    w.WriteString("$extractedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    w.WriteNumber("$recordCount", records.Count);
    w.WritePropertyName("$fields");
    JsonSerializer.Serialize(w, fields, jsonOpts);
    w.WritePropertyName("records");
    JsonSerializer.Serialize(w, records, jsonOpts);
    w.WriteEndObject();
    w.Flush();
    return new FileInfo(outPath).Length;
}

string StatusMark(bool ok, int warn) => ok ? (warn > 0 ? "⚠" : "✓") : "✗";
string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";

// ── 辅助：定位游戏、读 buildid、找类型 ──
static string? LocateGameDir()
{
    try
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 838350");
        return key?.GetValue("InstallLocation") as string;
    }
    catch { return null; }
}

static string? ReadBuildId(string gameDir)
{
    // appmanifest 在 steamapps\ 下，即游戏目录(steamapps\common\<Game>)的祖父目录。
    // 与百晓册知识库构建器用同一套定位逻辑，保持 buildid 锚点一致。
    var candidates = new List<string>
    {
        Path.Combine(gameDir, "..", "appmanifest_838350.acf"),
        Path.Combine(gameDir, "..", "..", "appmanifest_838350.acf"),
    };
    string? steamapps = null;
    try
    {
        var common = Path.GetDirectoryName(gameDir);          // ...\steamapps\common
        if (common != null) steamapps = Path.GetDirectoryName(common);  // ...\steamapps
    }
    catch { }
    if (steamapps != null) candidates.Add(Path.Combine(steamapps, "appmanifest_838350.acf"));

    foreach (var c in candidates)
    {
        string full;
        try { full = Path.GetFullPath(c); } catch { continue; }
        if (!File.Exists(full)) continue;
        foreach (var line in File.ReadAllLines(full))
            if (line.Trim().StartsWith("\"buildid\""))
            {
                var m = System.Text.RegularExpressions.Regex.Match(line, "\"(\\d+)\"");
                if (m.Success) return m.Groups[1].Value;
            }
    }
    return null;
}

static TypeDefinition? FindType(AssemblyDefinition asm, string simpleName)
{
    foreach (var t in asm.MainModule.Types)
    {
        if (t.Name == simpleName) return t;
        foreach (var nt in t.NestedTypes)
            if (nt.Name == simpleName) return nt;
    }
    return null;
}
