// 《太吾百晓册》知识库构建器（.NET 版）
//
// 把游戏自带的《太吾百晓册》（官方百科）中文资源转成对 AI 友好的 markdown 知识库。
// 供 mod 开发时让 AI 理解游戏机制与数值（与反编译源码互补：本库答「机制/数值是什么」，
// 源码答「代码怎么实现、方法签名」）。
//
// 选 .NET 而非 PowerShell 的原因：全程显式 UTF-8 读写无编码坑；约 0.8s 生成。零 NuGet 依赖，纯 BCL。
//
// 数据格式已对照游戏前端 Assembly-CSharp.dll 的 Game.Views.Encyclopedia 命名空间核实。
// 两层结构：① EncyclopediaContent.tsv（百晓册正文，机制说明）② EncyclopediaReference.tsv + *.tsv
//          （数据表：表头在 Reference 的 col4，数据在 *.tsv，二者 JOIN 还原成带表头的数值表）。
//
// 用法（需 .NET 8+ SDK，skill 阶段一已要求）：
//   <skill> = 本 SKILL.md 所在目录（skill 安装目录）的绝对路径；知识库默认输出到当前工作目录。
//   dotnet run --project "<skill>/scripts/dotnet-build-kb" -c Release -- [-g <游戏根>] [-o <输出根>] [-f]
//   dotnet run --project "<skill>/scripts/dotnet-build-kb" -c Release -- -g "D:\...\The Scroll Of Taiwu" -f

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaiwuKbBuilder;

internal static class Program
{
    // Steam App ID of 《太吾绘卷》
    private const int SteamAppId = 838350;

    private static int Main(string[] args)
    {
        var (gameDir, outDir, force) = ParseArgs(args);

        // ---- 0. 定位游戏 ----
        gameDir = ResolveGameDir(gameDir);
        if (gameDir is null)
        {
            Console.Error.WriteLine("未能定位《太吾绘卷》安装目录。请用 -g 显式指定游戏根目录，例如：");
            Console.Error.WriteLine("  dotnet run --project \"<skill>/scripts/dotnet-build-kb\" -c Release -- -g \"D:\\...\\The Scroll Of Taiwu\"");
            Console.Error.WriteLine("（根目录下应存在 The Scroll of Taiwu_Data\\StreamingAssets\\Language_CN\\EncyclopediaAssets）");
            return 1;
        }

        string langCn = Path.Combine(gameDir, "The Scroll of Taiwu_Data", "StreamingAssets", "Language_CN");
        string encDir = Path.Combine(langCn, "EncyclopediaAssets");
        string contentTsv = Path.Combine(encDir, "EncyclopediaContent.tsv");
        string refTsv = Path.Combine(encDir, "EncyclopediaReference.tsv");
        foreach (var p in new[] { contentTsv, refTsv })
        {
            if (!File.Exists(p))
            {
                Console.Error.WriteLine($"缺少必需资源：{p}");
                return 1;
            }
        }

        // ---- 1. buildid（与反编译目录 decompiled/<buildid>/ 同源锚点）----
        string buildId = ReadBuildId(gameDir);
        if (buildId == "unknown")
        {
            Console.WriteLine("警告：未能从 appmanifest 读到 buildid，输出目录将用 'unknown'——建议确认游戏为 Steam 正版安装。");
        }

        // ---- 2. 输出目录与幂等 ----
        outDir = string.IsNullOrEmpty(outDir)
            ? Path.Combine(Environment.CurrentDirectory, "knowledge-base")
            : outDir;
        string outRoot = Path.Combine(outDir, buildId);
        string metaFile = Path.Combine(outRoot, "_meta.json");

        if (!force && File.Exists(metaFile))
        {
            try
            {
                using var m = JsonDocument.Parse(File.ReadAllText(metaFile));
                if (m.RootElement.TryGetProperty("buildid", out var bidEl) && bidEl.GetString() == buildId)
                {
                    Console.WriteLine($"知识库已是最新（buildid={buildId}）：");
                    Console.WriteLine($"  {outRoot}");
                    Console.WriteLine("如需强制重建，加 -f。");
                    return 0;
                }
            }
            catch { /* meta 损坏则重建 */ }
        }

        if (Directory.Exists(outRoot))
        {
            try { Directory.Delete(outRoot, recursive: true); }
            catch (Exception ex)
            {
                // 目录被占用（编辑器/索引器/杀软）时整目录删不掉，退而求其次原地覆盖写。
                Console.WriteLine($"警告：无法整体删除旧目录（可能被占用）：{ex.Message}");
                Console.WriteLine("警告：改为原地覆盖重建。");
            }
        }
        Directory.CreateDirectory(outRoot);
        string encOut = Path.Combine(outRoot, "encyclopedia");
        string tablesOut = Path.Combine(outRoot, "data-tables");
        Directory.CreateDirectory(encOut);
        Directory.CreateDirectory(tablesOut);

        Console.WriteLine($"游戏目录: {gameDir}");
        Console.WriteLine($"buildid: {buildId}");
        Console.WriteLine($"输出到: {outRoot}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // ---- 3. 解析 ② EncyclopediaReference.tsv ----
        Console.WriteLine("解析 EncyclopediaReference.tsv ...");
        var refRows = ReadTsv(refTsv);
        var refDict = new Dictionary<string, RefEntry>();          // LinkId -> 条目
        var tableRefIds = new List<string>();                      // 表/表格集合 类型的 LinkId
        var hyperLinks = new List<(string Id, string Target, string Text)>(); // 超链接
        var refTypeCount = new Dictionary<string, int>();
        foreach (var r in refRows)
        {
            if (r.Length == 0 || string.IsNullOrEmpty(r[0])) continue;
            string id = r[0];
            string type = r.Length > 1 ? r[1] : "";
            string param = r.Length > 2 ? r[2] : "";
            string[] pms = r.Length > 3 ? ParseArrayCell(r[3]) : Array.Empty<string>();
            string[] desc = r.Length > 4 ? ParseArrayCell(r[4]) : Array.Empty<string>();
            string title = r.Length > 5 ? r[5] : "";
            refDict[id] = new RefEntry(id, type, param, pms, desc, title);
            refTypeCount[type] = refTypeCount.TryGetValue(type, out var c) ? c + 1 : 1;
            if (type == "表" || type == "表格集合") tableRefIds.Add(id);
            if (type == "超链接")
            {
                string text = desc.Length > 0 ? desc[0] : id;
                hyperLinks.Add((id, param, text));
            }
        }
        Console.WriteLine($"  {refDict.Count} 条引用，其中 {tableRefIds.Count} 个数据表引用、{hyperLinks.Count} 个超链接");

        // ---- 4. 解析 ① EncyclopediaContent.tsv，按顶级章节分组 ----
        Console.WriteLine("解析 EncyclopediaContent.tsv ...");
        var contentRows = ReadTsv(contentTsv);
        var chapterOrder = new List<string>();
        var chapters = new Dictionary<string, List<string[]>>();
        foreach (var r in contentRows)
        {
            if (r.Length == 0 || string.IsNullOrEmpty(r[0])) continue;
            if (!chapters.ContainsKey(r[0]))
            {
                chapters[r[0]] = new List<string[]>();
                chapterOrder.Add(r[0]);
            }
            chapters[r[0]].Add(r);
        }
        // 正文段落数：Layer 非 Heading 且 col6 有正文
        int contentBodyCount = contentRows.Count(r =>
            (r.Length <= 5 || !r[5].StartsWith("Heading")) && r.Length > 6 && !string.IsNullOrEmpty(r[6]));
        Console.WriteLine($"  {chapterOrder.Count} 个顶级章节：{string.Join(" / ", chapterOrder)}");

        // ---- 5. 写 ① 百晓册正文（每章一个 markdown）----
        Console.WriteLine("写出百晓册正文 ...");
        var encIndex = new List<(string Name, string File)>();
        for (int ci = 0; ci < chapterOrder.Count; ci++)
        {
            string ch = chapterOrder[ci];
            var rows = chapters[ch];
            string fname = $"{ci:D2}-{SanitizeFileSeg(ch)}.md";
            string fpath = Path.Combine(encOut, fname);
            var sb = new StringBuilder();
            sb.AppendLine($"# {ch}").AppendLine();
            sb.AppendLine($"> 《太吾百晓册》「{ch}」章节。由游戏资源 EncyclopediaContent.tsv 还原。").AppendLine();

            foreach (var r in rows)
            {
                string layer = r.Length > 5 ? r[5] : "";
                string body = r.Length > 6 ? r[6] : "";
                string level = r.Length > 7 ? r[7] : "";
                string insertsCell = r.Length > 11 ? r[11] : "";
                var titles = new List<string>();
                for (int t = 0; t <= 4; t++)
                    if (r.Length > t && !string.IsNullOrEmpty(r[t])) titles.Add(r[t]);

                if (layer.StartsWith("Heading"))
                {
                    int depth = 1;
                    var m = Regex.Match(layer, @"(\d+)");
                    if (m.Success) depth = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    depth = Math.Clamp(depth, 1, 5);
                    string headingText = "";
                    if (titles.Count >= depth) headingText = titles[depth - 1];
                    if (string.IsNullOrEmpty(headingText) && titles.Count > 0) headingText = titles[^1];
                    string hashes = new string('#', depth + 1); // 章标题已用 #，下级从 ## 开始
                    string lvl = !string.IsNullOrEmpty(level) ? $" <sub>难度: {level}</sub>" : "";
                    sb.AppendLine($"{hashes} {headingText}{lvl}").AppendLine();
                }
                else if (!layer.StartsWith("Heading") && !string.IsNullOrEmpty(body))
                {
                    // 正文行：直接渲染为普通段落（不做列表/缩进处理）。
                    // 注：col9 Layout 列({枚举0} 等)在游戏里表示列表点/缩进，但其 key 是游戏源码
                    // EnumMap.Layout 里硬编码的魔数(枚举440_400_840 等，名字数字不代表缩进)，
                    // 精确还原需对照游戏字典维护。为避免引入这种需持续维护的映射，这里一律按普通段落输出；
                    // 章节标题树(col0-4 + Heading)已表达了文档结构，正文段落本身的信息完整不丢。
                    sb.AppendLine(CleanRichText(body));
                    var inserts = ParseArrayCell(insertsCell);
                    if (inserts.Length > 0)
                    {
                        foreach (var ins in inserts)
                        {
                            if (string.IsNullOrEmpty(ins)) continue;
                            if (refDict.TryGetValue(ins, out var e))
                            {
                                switch (e.Type)
                                {
                                    case "表":
                                        string tblTitle = !string.IsNullOrEmpty(e.Title)
                                            ? CleanRichText(e.Title).Split('\n')[0]
                                            : e.Param;
                                        // 链接文字里若有 ] 会破坏 markdown 链接语法，兜底去掉
                                        string linkText = tblTitle.Replace("]", "").Replace("[", "");
                                        // 文件名必须与数据表生成处一致（SanitizeFileSeg(e.Param)），否则断链
                                        string tblFile = SanitizeFileSeg(e.Param) + ".md";
                                        sb.AppendLine();
                                        sb.AppendLine($"> 📊 数据表：[{linkText}](../data-tables/{tblFile})");
                                        break;
                                    case "表格集合":
                                        sb.AppendLine();
                                        sb.AppendLine($"> 📊 表格集合：{e.Param}");
                                        break;
                                    default:
                                        string disp = !string.IsNullOrEmpty(e.Title) ? e.Title
                                            : (!string.IsNullOrEmpty(e.Param) ? e.Param : e.Id);
                                        sb.AppendLine($"> 🔗 {disp}（类型: {e.Type}）");
                                        break;
                                }
                            }
                        }
                    }
                    sb.AppendLine();
                }
            }
            File.WriteAllText(fpath, sb.ToString(), new UTF8Encoding(false));
            encIndex.Add((ch, $"encyclopedia/{fname}"));
        }

        // ---- 6. 写 ② 数据表（表头 JOIN 自 Reference col4）----
        Console.WriteLine("写出数据表 ...");
        var tableIndex = new List<TableIndexEntry>();
        int tableBuilt = 0, tableMissing = 0;
        foreach (var refId in tableRefIds)
        {
            try
            {
                var e = refDict[refId];
                if (e.Type == "表格集合") continue; // 多表聚合，跳过单文件
                if (string.IsNullOrEmpty(e.Param)) continue;
                string tsvPath = Path.Combine(encDir, e.Param + ".tsv");
                if (!File.Exists(tsvPath)) { tableMissing++; continue; }

                // 表头：Reference col4 Desc，每项 "表头名:附加" 取冒号前
                var headers = e.Desc.Select(d => d.Split(':')[0].Trim()).ToList();
                int extraHeaderCount = e.Params.Length > 0 && int.TryParse(e.Params[0], out var ehc) ? ehc : 0;
                // 标题/页脚：Reference col5 Title，首 \n 切：[0]=标题 [1]=页脚
                var titleParts = e.Title.Split('\n', 2);
                string tblTitle = titleParts.Length > 0 && !string.IsNullOrEmpty(titleParts[0])
                    ? CleanRichText(titleParts[0]) : e.Param;
                string tblFooter = titleParts.Length > 1 ? CleanRichText(titleParts[1]) : "";

                var dataRows = ReadTsv(tsvPath);
                int maxCols = dataRows.Count > 0 ? dataRows.Max(d => d.Length) : 0;
                int colCount = headers.Count > 0 ? headers.Count : maxCols;
                if (colCount < 1) colCount = maxCols;

                string fname = SanitizeFileSeg(e.Param) + ".md";
                string fpath = Path.Combine(tablesOut, fname);
                var sb = new StringBuilder();
                sb.AppendLine($"# {tblTitle}").AppendLine();
                sb.AppendLine($"> 数据表 {e.Param}.tsv。表头来自 EncyclopediaReference（col4），数据来自游戏资源。");
                if (!string.IsNullOrEmpty(tblFooter)) { sb.AppendLine($"> {tblFooter}"); sb.AppendLine(); }

                var headerRow = headers.Count > 0 ? headers : Enumerable.Range(1, colCount).Select(i => $"列{i}").ToList();
                while (headerRow.Count < colCount) headerRow.Add("");
                headerRow = headerRow.Take(colCount).Select(h => EscapeMdCell(CleanRichText(h))).ToList();
                sb.AppendLine("| " + string.Join(" | ", headerRow) + " |");
                sb.AppendLine("| " + string.Join(" | ", headerRow.Select(_ => "---")) + " |");

                int startData = (extraHeaderCount > 0 && headers.Count > 0) ? extraHeaderCount : 0;
                for (int i = startData; i < dataRows.Count; i++)
                {
                    var d = dataRows[i];
                    var cells = Enumerable.Range(0, colCount)
                        .Select(c => EscapeMdCell(CleanRichText(c < d.Length ? d[c] : "")));
                    sb.AppendLine("| " + string.Join(" | ", cells) + " |");
                }
                File.WriteAllText(fpath, sb.ToString(), new UTF8Encoding(false));
                tableIndex.Add(new TableIndexEntry(tblTitle, $"data-tables/{fname}", e.Param, colCount, dataRows.Count - startData));
                tableBuilt++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：数据表 {refId} 处理失败：{ex.Message}");
            }
        }
        Console.WriteLine($"  成功 {tableBuilt} 个，缺失/跳过 {tableMissing} 个");

        // ---- 7. 交叉链接图 ----
        {
            string clPath = Path.Combine(encOut, "_cross-links.md");
            var sb = new StringBuilder();
            sb.AppendLine("# 百晓册超链接图（锚点 → 目标）").AppendLine();
            sb.AppendLine("> 由 EncyclopediaReference.tsv 的「超链接」条目还原。可用于理解章节间跳转关系。").AppendLine();
            sb.AppendLine("| 链接文字 | 跳转目标（Key 锚点） |");
            sb.AppendLine("|---|---|");
            foreach (var hl in hyperLinks)
                sb.AppendLine($"| {EscapeMdCell(CleanRichText(hl.Text))} | {hl.Target} |");
            File.WriteAllText(clPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ---- 9. INDEX.md ----
        {
            string indexPath = Path.Combine(outRoot, "INDEX.md");
            var sb = new StringBuilder();
            sb.AppendLine("# 《太吾百晓册》知识库 —— AI 入口").AppendLine();
            sb.AppendLine($"**buildid**: {buildId}  ｜  **生成时间**: {DateTime.Now:yyyy-MM-dd HH:mm:ss}").AppendLine();
            sb.AppendLine("本知识库由游戏资源《太吾百晓册》自动还原，供 mod 开发时让 AI 理解游戏机制与数值。");
            sb.AppendLine("**与反编译源码分工**：本库答「某机制/数值是什么、怎么设计的」；反编译源码答「代码怎么实现、方法签名」。").AppendLine();
            sb.AppendLine("## 如何用（给 AI）").AppendLine();
            sb.AppendLine("1. **先读本 INDEX** 定向：确定要查的主题属于哪一层、哪个文件。");
            sb.AppendLine("2. **机制理解** → 读 encyclopedia/ 下对应章节 markdown（整章通读最有效）。");
            sb.AppendLine("3. **具体数值/掉率/门派数据** → 读 data-tables/ 下对应表（表头已 JOIN 进去）。");
            sb.AppendLine("4. 定位代码实现时再转向反编译源码（decompiled/<buildid>/）。").AppendLine();
            sb.AppendLine($"## ① 百晓册正文（机制解释，{chapterOrder.Count} 章）").AppendLine();
            sb.AppendLine("| 章节 | 文件 |");
            sb.AppendLine("|---|---|");
            foreach (var e in encIndex) sb.AppendLine($"| {e.Name} | {e.File} |");
            sb.AppendLine();
            sb.AppendLine($"## ② 数据表（具体数值，{tableIndex.Count} 个，表头已还原）").AppendLine();
            sb.AppendLine("| 表名（中文） | 源表 | 列 | 行 | 文件 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in tableIndex.OrderBy(t => t.Name, StringComparer.CurrentCulture).ThenBy(t => t.Table, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"| {e.Name} | {e.Table} | {e.Cols} | {e.Rows} | {e.File} |");
            sb.AppendLine();
            sb.AppendLine("## 章节跳转关系").AppendLine();
            sb.AppendLine("见 encyclopedia/_cross-links.md（百晓册内超链接锚点 → 目标）。");
            File.WriteAllText(indexPath, sb.ToString(), new UTF8Encoding(false));
        }

        // ---- 10. _meta.json ----
        {
            var meta = new
            {
                buildid = buildId,
                generatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                gameDir,
                counts = new
                {
                    chapters = chapterOrder.Count,
                    encyclopediaBody = contentBodyCount,
                    referenceTotal = refDict.Count,
                    dataTables = tableBuilt,
                    dataTablesMissing = tableMissing,
                },
                sourceFiles = new
                {
                    content = $"EncyclopediaAssets/EncyclopediaContent.tsv ({contentRows.Count} rows)",
                    reference = $"EncyclopediaAssets/EncyclopediaReference.tsv ({refRows.Count} rows)",
                },
            };
            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaFile, json, new UTF8Encoding(false));
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"✅ 知识库生成完成（耗时 {sw.Elapsed.TotalSeconds:F1}s）");
        Console.WriteLine($"   位置: {outRoot}");
        Console.WriteLine($"   正文 {chapterOrder.Count} 章 ({contentBodyCount} 段) | 数据表 {tableBuilt} 个");
        Console.WriteLine();
        Console.WriteLine($"下一步：先读 {Path.Combine(outRoot, "INDEX.md")} 定向，再按需深读各文件。");
        return 0;
    }

    // ============================================================
    // 参数解析
    // ============================================================
    private static (string? GameDir, string? OutDir, bool Force) ParseArgs(string[] args)
    {
        string? gameDir = null, outDir = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-g":
                case "--game-dir":
                    if (i + 1 < args.Length) gameDir = args[++i];
                    break;
                case "-o":
                case "--out-dir":
                    if (i + 1 < args.Length) outDir = args[++i];
                    break;
                case "-f":
                case "--force":
                    force = true;
                    break;
                case "-h":
                case "--help":
                    Console.WriteLine("用法: dotnet run --project \"<skill>/scripts/dotnet-build-kb\" -c Release -- [-g <游戏根>] [-o <输出根>] [-f]");
                    Console.WriteLine("  <skill> = 本 SKILL.md / references 所在目录（skill 安装目录）的绝对路径；");
                    Console.WriteLine("            scripts/ 在 skill 包内，不在用户工作目录，故 --project 必须用 skill 目录的绝对路径。");
                    Console.WriteLine("  -g, --game-dir <dir>  游戏安装根目录（不传则自动探测：注册表→常见 Steam 路径）");
                    Console.WriteLine("  -o, --out-dir  <dir>  知识库输出根目录（默认当前工作目录下的 knowledge-base/）");
                    Console.WriteLine("  -f, --force           即使 buildid 已匹配也强制重建");
                    Environment.Exit(0);
                    break;
            }
        }
        return (gameDir, outDir, force);
    }

    // ============================================================
    // 定位游戏
    // ============================================================
    private static string? ResolveGameDir(string? hint)
    {
        bool HasEncAssets(string d) =>
            Directory.Exists(Path.Combine(d, "The Scroll of Taiwu_Data", "StreamingAssets", "Language_CN", "EncyclopediaAssets"));

        if (!string.IsNullOrEmpty(hint) && HasEncAssets(hint)) return hint;

        // a. 注册表（Steam 标准 uninstall key，仅 Windows）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App {SteamAppId}");
                if (key?.GetValue("InstallLocation") is string loc && HasEncAssets(loc)) return loc;
            }
            catch { }
        }

        // b. 常见 Steam 路径
        string[] candidates =
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\The Scroll Of Taiwu",
            @"D:\Steam\steamapps\common\The Scroll Of Taiwu",
            @"D:\SteamLibrary\steamapps\common\The Scroll Of Taiwu",
            @"D:\Program Files (x86)\Steam\steamapps\common\The Scroll Of Taiwu",
            @"E:\SteamLibrary\steamapps\common\The Scroll Of Taiwu",
        };
        foreach (var c in candidates) if (HasEncAssets(c)) return c;
        return null;
    }

    // 读 Steam buildid 作为版本指纹（与 decompiled/<buildid>/ 同源锚点）
    private static string ReadBuildId(string gameDir)
    {
        var candidates = new List<string>
        {
            Path.Combine(gameDir, "..", "appmanifest_838350.acf"),
            Path.Combine(gameDir, "..", "..", "appmanifest_838350.acf"),
        };
        string? steamapps = Path.GetDirectoryName(Path.GetDirectoryName(gameDir)); // ...\steamapps
        if (steamapps != null) candidates.Add(Path.Combine(steamapps, "appmanifest_838350.acf"));

        foreach (var c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full))
            {
                var mc = File.ReadAllText(full);
                var m = Regex.Match(mc, @"""buildid""\s+""(\d+)""");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        return "unknown";
    }

    // ============================================================
    // TSV 读取（UTF-8，去 BOM，按制表符切）
    // ============================================================
    private static List<string[]> ReadTsv(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes.AsSpan(3).ToArray();
        string text = Encoding.UTF8.GetString(bytes);
        var rows = new List<string[]>();
        foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            if (string.IsNullOrEmpty(line)) continue;
            rows.Add(line.Split('\t'));
        }
        return rows;
    }

    // {a,b,c} 形式切成数组；无大括号则按逗号切（与游戏 ParseStrArray 一致）
    private static string[] ParseArrayCell(string? cell)
    {
        if (string.IsNullOrEmpty(cell)) return Array.Empty<string>();
        cell = cell.Trim();
        if (cell.StartsWith("{") && cell.EndsWith("}"))
            cell = cell.Substring(1, cell.Length - 2);
        return cell.Split(',').Select(s => s.Trim()).ToArray();
    }

    // ============================================================
    // 文本清洗：转义还原 + 富文本标签清洗
    // ============================================================
    private static readonly Regex _multiNewline = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex _anyTag = new(@"<[^>]+>", RegexOptions.Compiled);

    private static string CleanRichText(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = ConvertEscapes(s);
        // 各种 TMP 标签去视觉、保留语义
        s = Regex.Replace(s, @"(?s)<align=[^>]*>", "");
        s = Regex.Replace(s, @"(?s)</align>", "");
        s = Regex.Replace(s, @"(?s)<color=[^>]*>", "");
        s = Regex.Replace(s, @"(?s)</color>", "");
        s = Regex.Replace(s, @"(?s)</?[biu]>", "");
        s = Regex.Replace(s, @"(?s)<link=[^>]*>", "");
        s = Regex.Replace(s, @"(?s)</link>", "");
        s = Regex.Replace(s, @"(?s)<size=[^>]*>", "");
        s = Regex.Replace(s, @"(?s)</size>", "");
        s = Regex.Replace(s, @"<SpName=([^>]*)>", "[$1]");
        s = Regex.Replace(s, @"(?s)<td[^>]*>", "");
        s = Regex.Replace(s, @"(?s)</td>", "");
        s = _anyTag.Replace(s, ""); // 兜底
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = _multiNewline.Replace(s, "\n\n");
        return s.Trim();
    }

    // 转义还原（与游戏 EncyclopediaDataProcessor 一致）
    private static string ConvertEscapes(string s) => s
        .Replace("\\u003c", "<")
        .Replace("\\u003e", ">")
        .Replace("\\n", "\n")
        .Replace("\\u002c", ",");

    // markdown 表格单元格安全转义
    private static string EscapeMdCell(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>").Trim();
    }

    // 文件名段：保留中文/字母数字，其余转 _
    private static string SanitizeFileSeg(string s) =>
        Regex.Replace(s, @"[^\w\u4e00-\u9fff]", "_");

    // ============================================================
    // 数据结构
    // ============================================================
    private sealed record RefEntry(string Id, string Type, string Param, string[] Params, string[] Desc, string Title);
    private sealed record TableIndexEntry(string Name, string File, string Table, int Cols, int Rows);
}
