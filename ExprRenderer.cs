using System.Text.RegularExpressions;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;
using UAssetKismet.Experimental;

namespace UAssetKismet;

/// <summary>把单个 KismetExpression 渲染为伪代码字符串（可内嵌，含名称解析）。</summary>
public class ExprRenderer
{
    private static readonly Regex SelfMemberRegex = new(@"\.self\.", RegexOptions.Compiled);
    private readonly UAsset _asset;
    private readonly UhtSignatureIndex? _sigs;
    public ExprRenderer(UAsset asset, UhtSignatureIndex? signatures = null)
    {
        _asset = asset;
        _sigs = signatures;
    }

    public string CleanSelf(string s) => SelfMemberRegex.Replace(s, ".");

    public string Render(KismetExpression? e)
    {
        if (e is null) return "null";
        switch (e)
        {
            // 控制流（独立语句，父级处理）
            case EX_Jump j: return $"goto L_{j.CodeOffset:X4}";
            case EX_JumpIfNot j: return $"if (!{Render(j.BooleanExpression)}) goto L_{j.CodeOffset:X4}";
            case EX_ComputedJump j: return $"// switch ({Render(j.CodeOffsetExpression)})";
            case EX_Return r: return r.ReturnExpression is EX_Nothing ? "return" : $"return {Render(r.ReturnExpression)}";
            case EX_PushExecutionFlow: return "";
            case EX_PopExecutionFlow: return "";
            case EX_PopExecutionFlowIfNot p: return $"if (!{Render(p.BooleanExpression)}) break";
            case EX_Assert a: return $"assert({Render(a.AssertExpression)})";
            case EX_EndOfScript: return "";

            // 赋值
            case EX_Let l: return $"{Render(l.Variable)} = {Render(l.Expression)}";
            case EX_LetBool l: return $"{Render(l.VariableExpression)} = {Render(l.AssignmentExpression)}";
            case EX_LetBase l: return $"{Render(l.VariableExpression)} = {Render(l.AssignmentExpression)}";
            case EX_LetValueOnPersistentFrame l:
                return $"{RenderVariablePath(l.DestinationProperty)} = {Render(l.AssignmentExpression)}";

            // 函数调用（子类在前）
            case EX_CallMath f: return RenderCall(f.StackNode, f.Parameters);
            case EX_FinalFunction f: return RenderCall(f.StackNode, f.Parameters);
            case EX_LocalVirtualFunction f: return $"{f.VirtualFunctionName}({RenderParams(f.Parameters)})";
            case EX_VirtualFunction f: return $"{f.VirtualFunctionName}({RenderParams(f.Parameters)})";
            case EX_InstanceDelegate d: return d.FunctionName.ToString();

            // 上下文调用（子类在前）
            case EX_Context_FailSilent ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";
            case EX_ClassContext ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";
            case EX_Context ctx: return $"{Render(ctx.ObjectExpression)}.{Render(ctx.ContextExpression)}";

            // 字面量
            case EX_Self: return "self";
            case EX_NoObject: return "null";
            case EX_NoInterface: return "null";
            case EX_True: return "true";
            case EX_False: return "false";
            case EX_IntZero: return "0";
            case EX_IntOne: return "1";
            case EX_Nothing: return "nothing";
            case EX_EndStructConst: return "";

            default:
                if (e is EX_VariableBase varBase)
                {
                    var name = RenderVariablePath(varBase.Variable);
                    return e is EX_InstanceVariable or EX_DefaultVariable ? $"self.{name}" : name;
                }
                var raw = e.GetType().GetField("RawValue")?.GetValue(e);
                if (raw is not null) return FormatRaw(raw);
                return DumpInline(e);
        }
    }

    private static string FormatRaw(object raw) => raw switch
    {
        float f => f.ToString("0.######"),
        bool b => b ? "true" : "false",
        FName n => n.ToString(),
        FString s => $"\"{s.Value}\"",
        string s => $"\"{s}\"",
        _ => raw.ToString() ?? "null"
    };

    public string RenderVariablePath(KismetPropertyPointer? kp)
    {
        if (kp is null) return "?";
        if (kp.New?.Path is { Length: > 0 } path)
            return string.Join(".", path.Select(p => p.ToString()));
        if (kp.Old.Index > 0)
            return _asset.Exports[kp.Old.Index - 1].ObjectName.ToString();
        return "?";
    }

    public string RenderParams(KismetExpression[]? ps)
    {
        if (ps is null || ps.Length == 0) return "";
        return string.Join(", ", ps.Select(Render));
    }

    public string ResolveStackNode(FPackageIndex idx)
    {
        if (idx.Index > 0 && idx.Index - 1 < _asset.Exports.Count)
            return _asset.Exports[idx.Index - 1].ObjectName.ToString();
        if (idx.Index < 0 && -idx.Index - 1 < _asset.Imports.Count)
            return _asset.Imports[-idx.Index - 1].ObjectName.ToString();
        return $"<fn:{idx.Index}>";
    }

    /// <summary>渲染函数调用；命中 UHT 签名时按位置给 out 实参加 out 前缀（调用名不限定类）。</summary>
    public string RenderCall(FPackageIndex idx, KismetExpression[]? ps)
    {
        var name = ResolveStackNode(idx);
        if (ps is null || ps.Length == 0) return $"{name}()";
        var mods = ResolveParamMods(idx);
        if (mods is not null && mods.Length == ps.Length)
        {
            var parts = new string[ps.Length];
            for (var i = 0; i < ps.Length; i++)
                parts[i] = mods[i] == "out" ? $"out {Render(ps[i])}" : Render(ps[i]);
            return $"{name}({string.Join(", ", parts)})";
        }
        return $"{name}({RenderParams(ps)})";
    }

    private string[]? ResolveParamMods(FPackageIndex idx)
    {
        if (_sigs is null || idx.Index >= 0) return null;
        var path = ImportSignatures.ResolveImportPath(_asset, idx);
        if (path is null) return null;
        return _sigs.ByFullKey.TryGetValue(path, out var s) ? s.ParamModifiers() : null;
    }

    private static string DumpInline(KismetExpression e)
    {
        var bits = e.GetType().GetFields()
            .Where(f => f.Name is not ("Tag" or "RawValue"))
            .Where(f => f.GetValue(e) is not null)
            .Select(f =>
            {
                var v = f.GetValue(e)!;
                return v is KismetExpression || v is KismetExpression[]
                    ? $"<{f.Name}>"
                    : $"{f.Name}={v}";
            });
        return $"{e.GetType().Name[3..]}({string.Join(", ", bits)})";
    }
}
