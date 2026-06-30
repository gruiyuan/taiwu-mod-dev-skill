using System.Collections;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TaiwuConfigExtractor;

/// <summary>
/// 解析游戏的本地化文件，把 ValueExtractor 产生的 { $langPack, $key } 引用解析成中文文本。
/// 同时加载 ConfigRefNameMapping/*.ref.txt 提供 id↔name 映射，用于校验。
/// </summary>
public sealed class LocalizationResolver
{
    private readonly string _streamingAssetsDir;
    private readonly string _langCode;
    // pack -> (key -> text)
    private readonly Dictionary<string, Dictionary<string, string>> _packs = new(StringComparer.Ordinal);

    /// <param name="streamingAssetsDir">游戏的 StreamingAssets 目录。</param>
    /// <param name="langCode">语言代码，默认 "CN"（对应 Language_CN）。</param>
    public LocalizationResolver(string streamingAssetsDir, string langCode = "CN")
    {
        _streamingAssetsDir = streamingAssetsDir;
        _langCode = langCode;
    }

    /// <summary>按需加载某个语言包（如 CharacterFeature_language、Accessory_language）。</summary>
    public void LoadPack(string packName)
    {
        if (_packs.ContainsKey(packName)) return;
        var path = Path.Combine(_streamingAssetsDir, $"Language_{_langCode}", packName + ".txt");
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            // 格式：奇数行 key，偶数行 value（key 形如 Name_0/Desc_0/EffectDesc_0）
            var lines = File.ReadAllLines(path);
            for (int i = 0; i + 1 < lines.Length; i += 2)
            {
                var key = lines[i];
                if (string.IsNullOrEmpty(key)) break; // 空行表示文件结束
                var val = lines[i + 1];
                dict[key] = val;
            }
        }
        _packs[packName] = dict;
    }

    /// <summary>解析某个 (pack, key) 引用为中文文本；找不到返回 null。</summary>
    public string? Resolve(string pack, string key)
    {
        LoadPack(pack);
        return _packs.TryGetValue(pack, out var d) && d.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>读取 ConfigRefNameMapping/&lt;tableName&gt;.ref.txt，返回 id->name 映射。
    /// 排除 id&lt;0 的占位项（如 None/-1，它们不是真实记录）。</summary>
    public Dictionary<int, string> LoadRefNameMap(string tableName)
    {
        var path = Path.Combine(_streamingAssetsDir, "ConfigRefNameMapping", tableName + ".ref.txt");
        var map = new Dictionary<int, string>();
        if (!File.Exists(path)) return map;
        var lines = File.ReadAllLines(path);
        for (int i = 0; i + 1 < lines.Length; i += 2)
        {
            var name = lines[i];
            if (string.IsNullOrEmpty(name)) break;
            if (int.TryParse(lines[i + 1], out var id) && id >= 0) map[id] = name;
        }
        return map;
    }

    /// <summary>
    /// 深度遍历已提取的记录，把所有 { $langPack, $key } 字典原地替换成解析后的中文文本。
    /// 找不到时保留原引用结构（便于排查）。
    /// </summary>
    public int ResolveRefsInPlace(List<Dictionary<string, object?>> records)
    {
        int unresolved = 0;
        foreach (var rec in records) unresolved += ResolveInObj(rec);
        return unresolved;
    }

    private int ResolveInObj(Dictionary<string, object?> dict)
    {
        int unresolved = 0;
        foreach (var key in dict.Keys.ToArray())
        {
            dict[key] = ResolveInValue(dict[key], ref unresolved);
        }
        return unresolved;
    }

    private object? ResolveInValue(object? v, ref int unresolved)
    {
        if (v is Dictionary<string, object?> d)
        {
            // 是本地化引用？
            if (d.TryGetValue("$langPack", out var p) && d.TryGetValue("$key", out var k))
            {
                var text = Resolve(p?.ToString() ?? "", k?.ToString() ?? "");
                if (text != null) return text;
                unresolved++;
                return d; // 保留以便排查
            }
            // 嵌套对象
            foreach (var key in d.Keys.ToArray())
                d[key] = ResolveInValue(d[key], ref unresolved);
            return d;
        }
        if (v is IList list)
        {
            for (int i = 0; i < list.Count; i++)
                list[i] = ResolveInValue(list[i], ref unresolved);
            return list;
        }
        return v;
    }
}
