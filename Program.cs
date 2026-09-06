using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetKismet;
using UAssetKismet.Experimental;

// 用法: KismetDecompiler --uasset <path> --usmap <path> [--inline] [--opt] [--out <dir>]
string GetArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}

var uassetPath = GetArg("--uasset");
var usmapPath = GetArg("--usmap");
var experimentalInline = args.Contains("--inline"); // 实验性：dispatch 内联回事件函数
var optimize = args.Contains("--opt");              // 实验性：语义优化（行级值流）
var outDir = GetArg("--out") is { Length: > 0 } o ? o : "Decompiled";

if (uassetPath.Length == 0 || usmapPath.Length == 0)
{
    Console.WriteLine("用法: KismetDecompiler --uasset <AssetRegistry/Blueprint.uasset> --usmap <mapping.usmap> [--inline] [--opt] [--out <dir>]");
    Console.WriteLine("  --inline   dispatch case 体内联回事件函数（实验）");
    Console.WriteLine("  --opt      语义优化：CallFunc 临时量值流内联 / 常量折叠（实验）");
    return;
}

string ApplyOpt(string code)
{
    if (!optimize) return code;
    var normalized = code.Replace("\r\n", "\n").TrimEnd('\n');
    var lines = normalized.Split('\n').ToList();
    var opt = SemanticOptimizer.OptimizeLines(lines);
    return string.Join("\n", opt) + "\n";
}

var usmap = new Usmap(usmapPath);
var asset = new UAsset(uassetPath, EngineVersion.VER_UE5_6, usmap, CustomSerializationFlags.None);

var funcs = asset.Exports.OfType<FunctionExport>().Where(f => f.ScriptBytecode is { Length: > 0 }).ToList();
var uber = funcs.FirstOrDefault(f => f.ObjectName.ToString().StartsWith("ExecuteUbergraph"));
var uberIdx = uber is null ? -1 : asset.Exports.IndexOf(uber) + 1;

// 收集 ExecuteUbergraph 调用点: (EntryPoint, 来源函数)
var calls = new List<(long n, string caller)>();
if (uber is not null)
{
    foreach (var fn in funcs)
    {
        if (fn == uber) continue;
        var n = FindUberCall(fn, uberIdx);
        if (n is not null) calls.Add((n.Value, fn.ObjectName.ToString()));
    }
}

Directory.CreateDirectory(outDir);

// ============ 实验模式：dispatch 内联回事件函数 ============
if (experimentalInline)
{
    if (uber is null)
    {
        Console.WriteLine("未找到 ExecuteUbergraph 函数，无法内联");
        return;
    }
    var inlineDir = Path.Combine(outDir, "Inline");
    Directory.CreateDirectory(inlineDir);
    var combinedInline = new StringBuilder();
    foreach (var fn in funcs)
    {
        if (fn == uber) continue;
        var name = fn.ObjectName.ToString();
        string code;
        var inlined = UAssetKismet.Experimental.DispatchInliner.InlineOne(asset, uber, calls, fn);
        if (inlined is not null)
        {
            code = inlined;
            File.WriteAllText(Path.Combine(inlineDir, name + ".inline.txt"), code);
        }
        else
        {
            code = new StructuredKismetDecompiler(asset, name).Decompile(fn);
            code = ApplyOpt(code);
            File.WriteAllText(Path.Combine(inlineDir, name + ".txt"), code);
        }
        combinedInline.AppendLine(code).AppendLine();
        Console.WriteLine(code);
    }
    File.WriteAllText(Path.Combine(inlineDir, "_all.txt"), combinedInline.ToString());
    Console.WriteLine($"\n[实验] 已内联 {calls.Count} 个 dispatch 调用点 → {inlineDir}");
    return;
}

// ============ 默认模式：普通结构化 + uber 反 Dispatch ============
var combined = new StringBuilder();
foreach (var fn in funcs)
{
    var name = fn.ObjectName.ToString();
    var code = fn == uber
        ? StructuredKismetDecompiler.DecompileDispatch(asset, fn, calls)
        : new StructuredKismetDecompiler(asset, name).Decompile(fn);
    code = ApplyOpt(code);
    combined.AppendLine(code).AppendLine();
    File.WriteAllText(Path.Combine(outDir, name + ".txt"), code);
    Console.WriteLine(code);
}
File.WriteAllText(Path.Combine(outDir, "_all_functions.txt"), combined.ToString());
Console.WriteLine($"\n已写出 {funcs.Count} 个函数（含反 Dispatch）");

static int? FindUberCall(FunctionExport fn, int uberExportIdx)
{
    int? found = null;
    void Walk(KismetExpression e)
    {
        switch (e)
        {
            case EX_FinalFunction f when f.StackNode.Index == uberExportIdx:
                found = FirstConst(f.Parameters); return;
            case EX_CallMath c when c.StackNode.Index == uberExportIdx:
                found = FirstConst(c.Parameters); return;
            case EX_LocalVirtualFunction l when l.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph"):
                found = FirstConst(l.Parameters); return;
            case EX_VirtualFunction v when v.VirtualFunctionName.ToString().StartsWith("ExecuteUbergraph"):
                found = FirstConst(v.Parameters); return;
        }
        foreach (var f in e.GetType().GetFields())
        {
            if (f.FieldType == typeof(KismetExpression) && f.GetValue(e) is KismetExpression sub) Walk(sub);
            if (f.FieldType == typeof(KismetExpression[]) && f.GetValue(e) is KismetExpression[] arr)
                foreach (var x in arr) { if (found is not null) return; Walk(x); }
        }
    }
    foreach (var e in fn.ScriptBytecode) { Walk(e); if (found is not null) break; }
    return found;

    static int? FirstConst(KismetExpression[]? ps)
    {
        if (ps is null || ps.Length == 0) return null;
        var raw = ps[0].GetType().GetField("RawValue")?.GetValue(ps[0]);
        return raw is int i ? i : raw is byte b ? (int)b : null;
    }
}
