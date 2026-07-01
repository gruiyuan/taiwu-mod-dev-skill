using System.Collections;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TaiwuConfigExtractor;

/// <summary>提取过程中对未知指令的反馈。</summary>
public sealed class ExtractorDiagnostics
{
    public int WarningCount;
    public readonly List<string> Warnings = new();
    public void Warn(string msg)
    {
        WarningCount++;
        if (Warnings.Count < 50) Warnings.Add(msg);
    }
}

/// <summary>
/// 在 CreateItems* 方法体上模拟值栈，把每个 new Item(...) 的位置参数还原成带字段名的 dict。
///
/// IL 栈语义（实测）：
///   - ldc.* / ldstr / ldnull → 压值
///   - call GetConfig(s,s) → 弹2压1（LocRef）
///   - newobj List.ctor() → 压 ListCollector（占位列表）
///   - dup → 复制栈顶（用于 List 连续 Add：每个 Add 前 dup 复制列表引用）
///   - newobj NestedStruct.ctor → 弹参数压 dict
///   - callvirt Add(item) → 弹 item、弹 list；若 list 是 ListCollector 则把 item 加入并【重新压回 list】
///       （因为 IL 里 list 引用通常由 dup 提供，Add 消耗 dup 的副本，本体仍在栈上；
///        但我们的实现把本体弹出后又压回，等价）
///   - newarr → 弹数量压数组（空或非空）
///   - stelem.* → 弹 值、索引、数组；写入数组
///   - newobj Item.ctor → 弹 N 参数压 dict（一条记录候选）
///   - callvirt _dataArray.Add(item) → 弹 item、弹 _dataArray；item 是记录 dict 则登记
///   - ldarg.0 / ldfld / pop / nop / ret → 忽略
/// </summary>
public sealed class ValueExtractor
{
    private readonly FieldMapper _itemMapper;
    private readonly ExtractorDiagnostics _diag;
    private readonly TypeDefinition _itemType;
    private readonly Dictionary<string, FieldMapper> _structMappers = new();
    private readonly string _itemFullName;

    public ValueExtractor(TypeDefinition itemType, ExtractorDiagnostics diag)
    {
        _itemType = itemType;
        _itemFullName = itemType.FullName;
        _diag = diag;
        _itemMapper = FieldMapper.Build(itemType);
        if (!_itemMapper.IsConsistent)
            diag.Warn($"{itemType.FullName}: field count ({_itemMapper.FieldNames.Count}) != ctor param count ({_itemMapper.CtorParamTypes.Count}); falling back to ArgN names");
    }

    public FieldMapper ItemMapper => _itemMapper;

    public List<Dictionary<string, object?>> ExtractRecords(MethodDefinition createItems)
    {
        var records = new List<Dictionary<string, object?>>();
        var stack = new Stack<object?>();
        var instrs = createItems.Body.Instructions;
        var firstItemField = _itemMapper.FieldNames.Count > 0 ? _itemMapper.FieldNames[0] : "__item__";
        // 局部变量表（支持 stloc/ldloc：复杂表会先把 List/数组存入局部变量再填充）
        var locals = new object?[createItems.Body.Variables.Count];

        for (int i = 0; i < instrs.Count; i++)
        {
            var ins = instrs[i];
            var op = ins.OpCode.Code;

            // —— 常量入栈 ——
            if (IsLoadConst(op, ins, out var constVal)) { stack.Push(constVal); continue; }
            if (op == Code.Ldnull) { stack.Push(null); continue; }
            if (op == Code.Ldstr) { stack.Push((string)ins.Operand!); continue; }

            // —— call / callvirt ——
            if ((op == Code.Call || op == Code.Callvirt) && ins.Operand is MethodReference mr)
            {
                // 本地化引用
                if (mr.Name == "GetConfig" && mr.Parameters.Count == 2)
                {
                    var key = PopString(stack);
                    var pack = PopString(stack);
                    stack.Push(new LocRef(pack, key));
                    continue;
                }
                // InitializeArray(Array, FieldHandle) —— 编译器内联数组填充
                // IL 栈 [arr, handle]，dup 已在前面复制了 arr 副本。InitializeArray 弹 handle 和 1 个 arr 副本，
                // 返回 void。结果：arr 本体仍留在栈上（不压回）。
                if (mr.Name == "InitializeArray" && mr.Parameters.Count == 2)
                {
                    var handle = Pop(stack);
                    var arr = Pop(stack);
                    if (arr is ArrayBuilder ab && handle is StaticInitBlob blob)
                        FillArrayFromBlob(ab, blob, ins.Offset);
                    else
                        _diag.Warn($"InitializeArray at IL_{ins.Offset:X4}: arr={arr?.GetType().Name}, handle={handle?.GetType().Name}");
                    // 不压回：dup 的副本已被消费，arr 本体（更早压的）仍在栈上
                    continue;
                }
                // Add(item) —— IL 栈 [list, item]，item 在顶。Add 弹 item 和顶部的 list 副本；
                // 内部 List 的本体由之前的 dup 维持在栈底（不压回，因为 dup 已提供副本）。
                if (mr.Name == "Add" && mr.Parameters.Count == 1)
                {
                    var itemVal = Pop(stack);
                    var listVal = Pop(stack);
                    // 内部 List<T>.Add：listVal 是 ListCollector
                    if (listVal is ListCollector lc)
                    {
                        lc.Items.Add(itemVal);
                        // 不压回：IL 里 Add 消耗的是 dup 出来的副本，本体（更早压的）仍在栈上
                        continue;
                    }
                    // _dataArray.Add（精确识别）：listVal 是 _dataArray 标记，itemVal 是目标记录 dict
                    if (listVal is DataArrayRef && itemVal is Dictionary<string, object?> dict)
                    {
                        records.Add(dict);
                        continue;
                    }
                    // _dataArray.Add（旧兜底，针对 _dataArray 字段名非标准或 ldfld 未标记的情况）：
                    // listVal 不是 ListCollector（即不是内部 List），且 itemVal 是目标 item dict
                    if (listVal is not ListCollector && itemVal is Dictionary<string, object?> dict2
                        && dict2.Count > 0 && ContainsKey(dict2, _itemMapper.FieldNames))
                    {
                        records.Add(dict2);
                        continue;
                    }
                    _diag.Warn($"Add at IL_{ins.Offset:X4}: item={itemVal?.GetType().Name}, list={listVal?.GetType().Name} — 未识别的 Add 形态");
                    continue;
                }
                // 其他意外 call：弹参压 null
                for (int k = 0; k < mr.Parameters.Count; k++) Pop(stack);
                stack.Push(null);
                _diag.Warn($"unexpected call {mr.FullName} at IL_{ins.Offset:X4}");
                continue;
            }

            // —— newarr ——
            if (op == Code.Newarr)
            {
                var cntVal = Pop(stack);
                int count = ToIntSafe(cntVal, ins.Offset, "newarr count");
                stack.Push(new ArrayBuilder(count));
                continue;
            }

            // —— ldtoken —— 编译器内联数组初始化用的静态数据字段句柄。记录 byte[] 备用。
            if (op == Code.Ldtoken && ins.Operand is FieldReference fref)
            {
                byte[]? data = null;
                if (fref is FieldDefinition fd)
                {
                    data = ReadFieldInitialData(fd);
                }
                stack.Push(new StaticInitBlob(data ?? Array.Empty<byte>()));
                if (data == null) _diag.Warn($"ldtoken at IL_{ins.Offset:X4}: 无法读取字段 {fref.FullName} 的初始数据");
                continue;
            }

            // —— stelem.* （数组元素赋值，IL 栈：[arr, idx, val]，val 在顶）——
            if (op == Code.Stelem_Any || op == Code.Stelem_Ref || op == Code.Stelem_I ||
                op == Code.Stelem_I1 || op == Code.Stelem_I2 || op == Code.Stelem_I4 ||
                op == Code.Stelem_I8 || op == Code.Stelem_R4 || op == Code.Stelem_R8)
            {
                var val = Pop(stack);
                var idx = ToIntSafe(Pop(stack), ins.Offset, "stelem idx");
                var arr = Pop(stack);
                if (arr is ArrayBuilder ab) ab.Set(idx, val);
                else _diag.Warn($"stelem at IL_{ins.Offset:X4}: target not ArrayBuilder (got {arr?.GetType().Name})");
                continue;
            }

            // —— newobj ——
            if (op == Code.Newobj && ins.Operand is MethodReference ctor)
            {
                var built = HandleNewobj(ctor, stack, ins);
                stack.Push(built);
                continue;
            }

            // —— dup ——
            if (op == Code.Dup) { stack.Push(stack.Peek()); continue; }

            // —— ldfld —— 若是 _dataArray 字段，压一个标记（用于精确识别 _dataArray.Add）。
            //    其他字段引用丢弃（不影响记录识别）。
            if (op == Code.Ldfld && ins.Operand is FieldReference fld)
            {
                if (fld.Name == "_dataArray") stack.Push(DataArrayRef.Instance);
                continue;
            }

            // —— stloc.* / ldloc.* —— 局部变量存取（复杂表先把 List/数组存局部变量再填充）
            if (IsStloc(op, ins, out int stIdx))
            {
                var v = Pop(stack);
                if (stIdx >= 0 && stIdx < locals.Length) locals[stIdx] = v;
                continue;
            }
            if (IsLdloc(op, ins, out int ldIdx))
            {
                stack.Push((ldIdx >= 0 && ldIdx < locals.Length) ? locals[ldIdx] : null);
                continue;
            }

            // —— 可忽略 ——
            if (op == Code.Ldarg_0 || op == Code.Nop || op == Code.Ret) continue;
            if (op == Code.Pop) { Pop(stack); continue; }

            _diag.Warn($"unhandled opcode {op} at IL_{ins.Offset:X4}");
        }

        return records;
    }

    private object? HandleNewobj(MethodReference ctor, Stack<object?> stack, Instruction ins)
    {
        int n = ctor.Parameters.Count;
        var decl = ctor.Resolve();
        var declFullName = decl?.DeclaringType.FullName ?? ctor.DeclaringType.FullName;

        // 1) 目标 ConfigItem
        if (declFullName == _itemFullName)
        {
            var args = PopArgs(stack, n);
            return BuildNamedObject(_itemMapper, args);
        }

        // 2) List<T>（FullName 形如 System.Collections.Generic.List`1，不含尖括号）
        if (declFullName.StartsWith("System.Collections.Generic.List`1") ||
            declFullName.EndsWith("`1") && declFullName.Contains("List`1"))
        {
            for (int k = 0; k < n; k++) Pop(stack);
            return new ListCollector();
        }

        // 3) 嵌套值类型
        if (decl != null)
        {
            var args = PopArgs(stack, n);
            var nestedMapper = GetStructMapper(decl.DeclaringType);
            return BuildNamedObject(nestedMapper, args);
        }

        for (int k = 0; k < n; k++) Pop(stack);
        _diag.Warn($"unhandled newobj {ctor.FullName} at IL_{ins.Offset:X4}");
        return null;
    }

    private FieldMapper GetStructMapper(TypeDefinition td)
    {
        if (!_structMappers.TryGetValue(td.FullName, out var m))
        {
            m = FieldMapper.Build(td);
            _structMappers[td.FullName] = m;
        }
        return m;
    }

    private static Dictionary<string, object?> BuildNamedObject(FieldMapper mapper, object?[] args)
    {
        var o = new Dictionary<string, object?>(args.Length);
        for (int i = 0; i < args.Length; i++)
            o[mapper.GetName(i)] = Materialize(args[i]);
        return o;
    }

    private static object? Materialize(object? v)
    {
        if (v is LocRef loc) return new Dictionary<string, object?> { ["$langPack"] = loc.Pack, ["$key"] = loc.Key };
        if (v is ListCollector lc) return lc.Items;
        if (v is ArrayBuilder ab) return ab.Items;
        if (v is StaticInitBlob) return null; // 不应出现在最终结构里（被 InitializeArray 消费）
        if (v is DataArrayRef) return null;  // 不应出现在最终结构里（记录 Add 时消费）
        return v;
    }

    /// <summary>
    /// 把编译器内联的静态数据（little-endian）填进 ArrayBuilder。
    /// 数组元素类型未知（InitializeArray 不带类型信息），但太吾里这类数组都是 int32（CustomGroupCount）。
    /// 因此按 4 字节小端解析；若数组大小×4 != blob 长度，按实际情况退化为 byte 解析。
    /// </summary>
    private void FillArrayFromBlob(ArrayBuilder ab, StaticInitBlob blob, int offset)
    {
        var data = blob.Data;
        int n = ab.Items.Length;
        // 优先按 int32（4 字节）解析
        if (n > 0 && data.Length == n * 4)
        {
            for (int i = 0; i < n; i++)
                ab.Items[i] = BitConverter.ToInt32(data, i * 4);
            return;
        }
        // 按 int16（2 字节，CustomGroupItem 里 TemplateKey 等）
        if (n > 0 && data.Length == n * 2)
        {
            for (int i = 0; i < n; i++)
                ab.Items[i] = BitConverter.ToInt16(data, i * 2);
            return;
        }
        // 按 byte
        if (n > 0 && data.Length == n)
        {
            for (int i = 0; i < n; i++)
                ab.Items[i] = data[i];
            return;
        }
        _diag.Warn($"InitializeArray at IL_{offset:X4}: 数组大小 {n} 与 blob {data.Length} 字节不匹配，跳过填充");
    }

    /// <summary>读取编译器内联静态数据字段的初始字节（来自 PE 的 .data 节，Cecil 通过 InitialValue 暴露）。</summary>
    private static byte[]? ReadFieldInitialData(FieldDefinition fd)
    {
        try
        {
            // Cecil 0.11.x：InitialValue 返回内联数据的 byte[] 副本
            var v = fd.InitialValue;
            return v;
        }
        catch { return null; }
    }

    private static object? Pop(Stack<object?> s) => s.Count > 0 ? s.Pop() : null;
    private static string PopString(Stack<object?> s) => Pop(s)?.ToString() ?? "";

    /// <summary>dict 是否包含字段名列表中的任意一个（用于判断是否是目标 item dict 而非嵌套结构）。
    /// 检查多个字段名比只查第一个更可靠——目标 item 的字段集与嵌套结构字段集通常整体不同。</summary>
    private static bool ContainsKey(Dictionary<string, object?> dict, IReadOnlyList<string> fieldNames)
    {
        if (fieldNames.Count == 0) return false;
        // 检查前 3 个字段名是否都在 dict 中（目标 item 必然全有，嵌套结构通常不全有）
        int checkCount = Math.Min(3, fieldNames.Count);
        for (int i = 0; i < checkCount; i++)
            if (!dict.ContainsKey(fieldNames[i])) return false;
        return true;
    }

    /// <summary>安全转 int；遇到非整数（栈错位）时记警告返回 0，不崩溃。</summary>
    private int ToIntSafe(object? v, int offset, string ctx)
    {
        if (v == null) return 0;
        try { return Convert.ToInt32(v); }
        catch
        {
            _diag.Warn($"IL_{offset:X4} {ctx}: 期望 int 但栈上是 {v.GetType().Name}（栈错位）");
            return 0;
        }
    }
    private static object?[] PopArgs(Stack<object?> stack, int n)
    {
        var args = new object?[n];
        for (int k = n - 1; k >= 0; k--) args[k] = Pop(stack);
        return args;
    }

    private static bool IsLoadConst(Code op, Instruction ins, out object? val)
    {
        val = null;
        switch (op)
        {
            case Code.Ldc_I4_0: val = 0; return true;
            case Code.Ldc_I4_1: val = 1; return true;
            case Code.Ldc_I4_2: val = 2; return true;
            case Code.Ldc_I4_3: val = 3; return true;
            case Code.Ldc_I4_4: val = 4; return true;
            case Code.Ldc_I4_5: val = 5; return true;
            case Code.Ldc_I4_6: val = 6; return true;
            case Code.Ldc_I4_7: val = 7; return true;
            case Code.Ldc_I4_8: val = 8; return true;
            case Code.Ldc_I4_M1: val = -1; return true;
            case Code.Ldc_I4: val = Convert.ToInt32(ins.Operand); return true;
            case Code.Ldc_I4_S: val = Convert.ToInt32(ins.Operand); return true;
            case Code.Ldc_I8: val = Convert.ToInt64(ins.Operand); return true;
            case Code.Ldc_R4: val = Convert.ToSingle(ins.Operand); return true;
            case Code.Ldc_R8: val = Convert.ToDouble(ins.Operand); return true;
        }
        return false;
    }

    /// <summary>识别 stloc.* 指令，返回局部变量索引。</summary>
    private static bool IsStloc(Code op, Instruction ins, out int idx)
    {
        switch (op)
        {
            case Code.Stloc_0: idx = 0; return true;
            case Code.Stloc_1: idx = 1; return true;
            case Code.Stloc_2: idx = 2; return true;
            case Code.Stloc_3: idx = 3; return true;
            case Code.Stloc:
            case Code.Stloc_S:
                idx = IdxFromVariableOperand(ins.Operand); return true;
        }
        idx = -1; return false;
    }

    /// <summary>识别 ldloc.* 指令，返回局部变量索引。</summary>
    private static bool IsLdloc(Code op, Instruction ins, out int idx)
    {
        switch (op)
        {
            case Code.Ldloc_0: idx = 0; return true;
            case Code.Ldloc_1: idx = 1; return true;
            case Code.Ldloc_2: idx = 2; return true;
            case Code.Ldloc_3: idx = 3; return true;
            case Code.Ldloc:
            case Code.Ldloc_S:
                idx = IdxFromVariableOperand(ins.Operand); return true;
        }
        idx = -1; return false;
    }

    private static int IdxFromVariableOperand(object? operand)
    {
        if (operand is int i) return i;
        if (operand is Mono.Cecil.Cil.VariableDefinition vd) return vd.Index;
        return -1;
    }

    // —— 中间表示 ——
    private sealed record LocRef(string Pack, string Key);
    /// <summary>标记栈顶是 ConfigData._dataArray 引用（用于精确识别 _dataArray.Add = 一条记录完成）。</summary>
    private sealed class DataArrayRef { public static readonly DataArrayRef Instance = new(); }
    private sealed class ListCollector { public readonly List<object?> Items = new(); }
    /// <summary>支持非空数组的占位构建器：先按大小分配，stelem 填充。</summary>
    private sealed class ArrayBuilder
    {
        public readonly object?[] Items;
        public ArrayBuilder(int count) => Items = new object?[count];
        public void Set(int idx, object? val) { if (idx >= 0 && idx < Items.Length) Items[idx] = val; }
    }

    /// <summary>编译器内联数组初始化的静态数据（来自 ldtoken 的 FieldDefinition.InitialValue）。</summary>
    private sealed class StaticInitBlob
    {
        public readonly byte[] Data;
        public StaticInitBlob(byte[] data) => Data = data;
    }
}
