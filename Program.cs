using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;
using UAssetKismet;
using UAssetKismet.Experimental;

// 用法: KismetDecompiler --uasset <path> --usmap <path> [--inline] [--opt] [--out <dir>] [--uhtdump <dir>]
string GetArg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}

var uassetPath = GetArg("--uasset");
var usmapPath = GetArg("--usmap");
var experimentalInline = args.Contains("--inline"); // 实验性：dispatch 内联回事件函数
var optimize = args.Contains("--opt");              // 实验性：语义优化
var outDir = GetArg("--out") is { Length: > 0 } o ? o : "Decompiled";
var uhtDump = GetArg("--uhtdump");                  // UE4SS UHTHeaderDump 目录（import 函数签名）

if (uassetPath.Length == 0 || usmapPath.Length == 0)
{
    Console.WriteLine("用法: KismetDecompiler --uasset <蓝图.uasset> --usmap <mapping.usmap> [--inline] [--opt] [--out <dir>] [--uhtdump <UHTHeaderDump>]");
    Console.WriteLine("  --inline   dispatch case 体内联回事件函数（实验）");
    Console.WriteLine("  --opt      语义优化：临时量值流内联 / 常量折叠 / 命名简化（实验）");
    Console.WriteLine("  --uhtdump  UE4SS 的 UHTHeaderDump 目录：额外输出 import 调用签名清单");
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

// ============ 函数签名清单（UHT dump + 本地 LoadedProperties）============
UhtSignatureIndex? sigIndex = null;
if (uhtDump.Length > 0)
{
    Console.WriteLine($"[签名] 解析 UHTHeaderDump: {uhtDump}");
    sigIndex = UhtSignatureIndex.Load(uhtDump);
    Console.WriteLine($"[签名] 索引到 {sigIndex.ByFullKey.Count} 个 UFUNCTION");
}
WriteSignatures(outDir, asset, funcs, sigIndex);

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
        var inlined = DispatchInliner.InlineOne(asset, uber, calls, fn, sigIndex);
        if (inlined is not null)
        {
            code = inlined;
            File.WriteAllText(Path.Combine(inlineDir, name + ".inline.txt"), code);
        }
        else
        {
            code = new StructuredKismetDecompiler(asset, name, sigIndex).Decompile(fn);
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
        ? StructuredKismetDecompiler.DecompileDispatch(asset, fn, calls, sigIndex)
        : new StructuredKismetDecompiler(asset, name, sigIndex).Decompile(fn);
    code = ApplyOpt(code);
    combined.AppendLine(code).AppendLine();
    File.WriteAllText(Path.Combine(outDir, name + ".txt"), code);
    Console.WriteLine(code);
}
File.WriteAllText(Path.Combine(outDir, "_all.txt"), combined.ToString());
Console.WriteLine($"\n已写出 {funcs.Count} 个函数（含反 Dispatch）");

// ---- 签名清单输出 ----
void WriteSignatures(string dir, UAsset a, List<FunctionExport> fs, UhtSignatureIndex? idx)
{
    var sb = new StringBuilder();
    sb.AppendLine("// ===== 本地函数签名（来自 LoadedProperties，启发式类型）=====");
    foreach (var fn in fs)
        sb.AppendLine(ImportSignatures.BuildLocalSig(fn));

    if (idx is not null)
    {
        sb.AppendLine("\n// ===== import 调用签名（来自 UHTHeaderDump）=====");
        var paths = new HashSet<string>();
        foreach (var fn in fs)
            if (fn.ScriptBytecode is not null)
                ImportSignatures.CollectImportFuncs(a, fn.ScriptBytecode, paths);

        var hit = 0;
        foreach (var p in paths.OrderBy(x => x))
        {
            if (idx.ByFullKey.TryGetValue(p, out var sig))
            {
                sb.AppendLine($"// {sig}");
                hit++;
            }
            else
            {
                sb.AppendLine($"// {p}   (未命中 UHT dump)");
            }
        }
        Console.WriteLine($"[签名] import 调用 {paths.Count} 个，命中 UHT dump {hit} 个");
    }

    File.WriteAllText(Path.Combine(dir, "signatures.txt"), sb.ToString());
    Console.WriteLine($"[签名] 已写出 {Path.Combine(dir, "signatures.txt")}");
}

// ---- 查找事件函数里对 ExecuteUbergraph 的调用 ----
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
