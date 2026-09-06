using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace UAssetKismet.Experimental;

/// <summary>import 全路径解析与函数签名收集工具。</summary>
public static class ImportSignatures
{
    /// <summary>把 FPackageIndex 解析为 "Module.Class.Func"（import 方向）；export 返回 null。</summary>
    public static string? ResolveImportPath(UAsset asset, FPackageIndex idx)
    {
        if (idx.Index >= 0) return null;
        var imp = asset.Imports[-idx.Index - 1];
        var func = imp.ObjectName.ToString();
        string? cls = null, module = null;
        var outer = imp.OuterIndex;
        var guard = 0;
        while (outer.Index < 0 && guard++ < 6)
        {
            var o = asset.Imports[-outer.Index - 1];
            if (cls is null) cls = o.ObjectName.ToString();
            else { module = o.ObjectName.ToString(); break; }
            outer = o.OuterIndex;
        }
        if (module is null && cls is not null)
            module = imp.PackageName.ToString(); // 兜底
        module = module?.Replace("/Script/", "").Replace("/Engine/", "");
        if (cls is null) return func;
        return $"{module}.{cls}.{func}";
    }

    /// <summary>递归收集函数体内所有 EX_FinalFunction/CallMath 的 import 全路径。</summary>
    public static void CollectImportFuncs(UAsset asset, IEnumerable<KismetExpression> exprs, HashSet<string> outPaths)
    {
        void Walk(KismetExpression e)
        {
            if (e is EX_FinalFunction f)
            {
                var p = ResolveImportPath(asset, f.StackNode);
                if (p is not null) outPaths.Add(p);
            }
            foreach (var fi in e.GetType().GetFields())
            {
                if (fi.FieldType == typeof(KismetExpression) && fi.GetValue(e) is KismetExpression sub) Walk(sub);
                if (fi.FieldType == typeof(KismetExpression[]) && fi.GetValue(e) is KismetExpression[] arr)
                    foreach (var x in arr) Walk(x);
            }
        }
        foreach (var e in exprs) Walk(e);
    }

    private static string TypeName(object? p)
    {
        if (p is null) return "?";
        var n = p.GetType().Name;
        return n switch
        {
            "FBoolProperty" => "bool",
            "FObjectProperty" => "UObject*",
            "FClassProperty" => "UClass*",
            "FEnumProperty" => "enum",
            "FFloatProperty" => "float",
            "FIntProperty" => "int32",
            "FByteProperty" => "uint8",
            "FStrProperty" => "FString",
            "FNameProperty" => "FName",
            "FTextProperty" => "FText",
            "FStructProperty" => "FStruct",
            _ => "auto"
        };
    }

    /// <summary>从 FunctionExport.LoadedProperties 生成本地函数签名（参数 + 返回的近似还原）。</summary>
    public static string BuildLocalSig(FunctionExport fn)
    {
        var args = new List<string>();
        var rets = new List<string>();
        if (fn.LoadedProperties is { Length: > 0 } props)
        {
            foreach (var p in props)
            {
                var name = p.Name.ToString();
                if (name.StartsWith("CallFunc_") || name.StartsWith("K2Node_")) continue;
                var flags = (ulong)p.PropertyFlags;
                // 参数/返回值带标志；局部/临时为 0（启发式）
                if (flags == 0) continue;
                var tn = TypeName(p);
                if ((flags & 0x8) != 0) rets.Add($"{tn} {name}");
                else args.Add($"{tn} {name}");
            }
        }
        var sb = new StringBuilder();
        if (rets.Count > 0) sb.AppendJoin(", ", rets).Append(" :: ");
        else sb.Append("void ");
        sb.Append(fn.ObjectName).Append('(').AppendJoin(", ", args).Append(')');
        return sb.ToString();
    }
}
