using Mono.Cecil;

namespace TaiwuConfigExtractor;

/// <summary>
/// 把"按 ctor 参数顺序的位置索引"映射成"字段名"。
/// 太吾的 ConfigItem 子类字段都是 public readonly，声明顺序与 ctor 参数顺序一一对应。
/// 但少数复杂表（Character/Organization/MapPickups 等）的字段定义可能因 Cecil 加载方式拿不到，
/// 此时 fallback 用 ctor 参数名（PascalCase 化）作为字段名。
/// </summary>
public sealed class FieldMapper
{
    private readonly List<string> _fieldNames = new();
    private readonly List<string> _ctorParamTypes = new();
    private readonly List<string> _ctorParamNames = new();
    private bool _usedFallback;

    private FieldMapper() { }

    public static FieldMapper Build(TypeDefinition itemType)
    {
        var m = new FieldMapper();

        // 1) 字段名：按声明顺序取所有 public 实例字段
        var publicFields = new List<string>();
        foreach (var f in itemType.Fields)
        {
            if (f.IsStatic) continue;
            if (f.IsPublic) publicFields.Add(f.Name);
        }

        // 2) ctor 参数：选参数数最多的那个（数值构造函数）
        MethodDefinition? ctor = null;
        foreach (var c in itemType.Methods)
        {
            if (!c.IsConstructor || !c.HasParameters) continue;
            if (ctor == null || c.Parameters.Count > ctor.Parameters.Count) ctor = c;
        }
        if (ctor != null)
        {
            foreach (var p in ctor.Parameters)
            {
                m._ctorParamTypes.Add(p.ParameterType.FullName);
                m._ctorParamNames.Add(p.Name);
            }
        }

        // 3) 决定字段名来源：优先用 public 字段声明顺序；字段数与 ctor 参数数一致才用字段名
        if (publicFields.Count > 0 && ctor != null && publicFields.Count == ctor.Parameters.Count)
        {
            m._fieldNames.AddRange(publicFields);
            m._usedFallback = false;
        }
        else
        {
            // fallback：用 ctor 参数名（PascalCase 化，如 templateId -> TemplateId）
            // 这是稳健兜底，即使字段定义因 Cecil 加载方式取不到也能拿到有意义的名字
            m._fieldNames.AddRange(m._ctorParamNames.Select(ToPascalCase));
            m._usedFallback = true;
        }

        return m;
    }

    /// <summary>把 ctor 参数名（camelCase）转成 PascalCase 作为字段名。</summary>
    private static string ToPascalCase(string paramName)
    {
        if (string.IsNullOrEmpty(paramName)) return paramName;
        // 处理首字母大写；保留下划线前缀（如 _foo）
        if (paramName[0] == '_') return "_" + ToPascalCase(paramName.Substring(1));
        return char.ToUpperInvariant(paramName[0]) + paramName.Substring(1);
    }

    public IReadOnlyList<string> FieldNames => _fieldNames;
    public IReadOnlyList<string> CtorParamTypes => _ctorParamTypes;
    public bool UsedFallback => _usedFallback;

    /// <summary>字段数是否与 ctor 参数数匹配。</summary>
    public bool IsConsistent => _fieldNames.Count == _ctorParamTypes.Count && _fieldNames.Count > 0;

    /// <summary>按 ctor 参数位置取字段名。</summary>
    public string GetName(int paramIndex) =>
        paramIndex < _fieldNames.Count ? _fieldNames[paramIndex] : $"Arg{paramIndex}";
}
