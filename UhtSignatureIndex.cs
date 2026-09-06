using System.Text.RegularExpressions;

namespace UAssetKismet.Experimental;

/// <summary>UHT 签名（函数完整 C++ 签名片段）。</summary>
public sealed class UhtFuncSig
{
    public string Module = "";       // 模块，如 Engine / kards
    public string Class = "";        // 无前缀类名，如 KismetMathLibrary
    public string Func = "";         // 函数名
    public string RetType = "";      // 返回类型
    public string Params = "";       // 参数列表（原样），可空
    public bool IsStatic;
    public override string ToString() =>
        $"{RetType} {Class}::{Func}({Params})";
}

/// <summary>
/// 解析 UE4SS 的 UHTHeaderDump（*.h）构建函数签名索引。
/// Key: "&lt;Module&gt;.&lt;Class&gt;.&lt;Func&gt;"（与 import 全路径对齐，如 Engine.KismetMathLibrary.EqualEqual_ByteByte）。
/// 额外提供 FuncOnly 唯一映射便于兜底。
/// </summary>
public class UhtSignatureIndex
{
    private static readonly Regex ClassRe = new(
        @"\bclass\s+(?:[A-Z_][A-Z0-9_]*\s+)?(U[A-Za-z0-9_]+)\s*:\s*public", RegexOptions.Compiled);
    private static readonly Regex FuncRe = new(
        @"(?:static\s+)?((?:[A-Za-z_][\w:<>,\s\*&]*?))\s+([A-Za-z_]\w*)\s*\(([^;]*?)\)\s*;", RegexOptions.Singleline);

    public readonly Dictionary<string, UhtFuncSig> ByFullKey = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, List<UhtFuncSig>> ByFunc = new(StringComparer.OrdinalIgnoreCase);

    public static UhtSignatureIndex Load(string dumpRoot)
    {
        var cache = dumpRoot.TrimEnd('\\', '/') + ".uhtidx.json";
        if (TryLoadCache(cache, out var cached)) return cached;

        var idx = new UhtSignatureIndex();
        if (!Directory.Exists(dumpRoot)) return idx;

        var files = Directory.GetFiles(dumpRoot, "*.h", SearchOption.AllDirectories);
        var moduleOf = new Dictionary<string, string>(); // 目录 → 模块（dumpRoot 下第一级目录名）
        foreach (var file in files)
        {
            string module;
            if (!moduleOf.TryGetValue(Path.GetDirectoryName(file) ?? "", out module!))
            {
                module = ResolveModule(file, dumpRoot);
                moduleOf[Path.GetDirectoryName(file) ?? ""] = module;
            }
            if (module.Length == 0) continue;
            idx.ParseHeader(file, module);
        }
        idx.SaveCache(cache);
        return idx;
    }

    private void SaveCache(string path)
    {
        try
        {
            var all = ByFullKey.Values.ToArray();
            var json = System.Text.Json.JsonSerializer.Serialize(all,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { /* 缓存失败不影响使用 */ }
    }

    private static bool TryLoadCache(string path, out UhtSignatureIndex idx)
    {
        idx = null!;
        try
        {
            if (!File.Exists(path)) return false;
            var list = System.Text.Json.JsonSerializer.Deserialize<UhtFuncSig[]>(File.ReadAllText(path),
                new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
            if (list is null || list.Length == 0) return false;
            var fresh = new UhtSignatureIndex();
            foreach (var s in list)
            {
                if (string.IsNullOrEmpty(s.Class) || string.IsNullOrEmpty(s.Func)) continue;
                fresh.ByFullKey[$"{s.Module}.{s.Class}.{s.Func}"] = s;
                if (!fresh.ByFunc.TryGetValue(s.Func, out var l)) fresh.ByFunc[s.Func] = l = new();
                l.Add(s);
            }
            if (fresh.ByFullKey.Count == 0) return false;
            idx = fresh;
            return true;
        }
        catch { return false; }
    }

    private static string ResolveModule(string file, string root)
    {
        var rel = Path.GetRelativePath(root, file);
        var idx = rel.IndexOf(Path.DirectorySeparatorChar);
        if (idx < 0) return "";
        var module = rel.Substring(0, idx);
        // 跳过 UHT 内部 meta 目录
        if (module == "Global" || module.StartsWith(".")) return "";
        return module;
    }

    private void ParseHeader(string file, string module)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { return; }

        string curClass = "";
        string? ufunc = null;      // 待解析的 UFUNCTION 块（跨行累积到 ')'）
        var decl = new List<string>();

        void FlushDecl()
        {
            if (curClass.Length == 0) return;
            var src = string.Join(" ", decl).Replace('\n', ' ');
            foreach (Match m in FuncRe.Matches(src))
            {
                var ret = m.Groups[1].Value.Trim();
                var name = m.Groups[2].Value;
                if (name == curClass) continue; // 构造函数
                var sig = new UhtFuncSig
                {
                    Module = module,
                    Class = StripPrefix(curClass),
                    Func = name,
                    RetType = ret,
                    Params = m.Groups[3].Value.Trim(),
                    IsStatic = src.Contains("static")
                };
                var key = $"{module}.{sig.Class}.{name}";
                ByFullKey[key] = sig;
                if (!ByFunc.TryGetValue(name, out var list)) ByFunc[name] = list = new List<UhtFuncSig>();
                list.Add(sig);
            }
            decl.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//")) continue;

            var cm = ClassRe.Match(line);
            if (cm.Success) curClass = cm.Groups[1].Value;

            if (ufunc is not null)
            {
                ufunc += " " + line;
                if (ufunc.Contains(')'))
                {
                    // UFUNCTION 段结束；下面声明行随后由 decl 累积
                    ufunc = null;
                }
                continue;
            }
            if (line.StartsWith("UFUNCTION"))
            {
                ufunc = line;
                // 同一行含 ')' 则忽略下一段
                if (ufunc.Contains(')')) ufunc = null;
                continue;
            }

            // 累积函数声明（直至 ';'）
            decl.Add(line);
            if (line.EndsWith(";")) FlushDecl();
        }
        FlushDecl();
    }

    private static string StripPrefix(string className)
    {
        if (className.StartsWith("U") || className.StartsWith("A") || className.StartsWith("F"))
            return className.Substring(1);
        return className;
    }
}
