using System.Text;
using System.Text.RegularExpressions;

namespace UAssetKismet.Experimental;

/// <summary>
/// 行级语义优化器（实验性，作用于渲染后的伪代码行）。
/// P2 值流：CallFunc_* 临时量若"赋值一次 + 只作为函数参数整体使用一次"→ 直接内联进参数，删赋值行。
/// P1 化简：!(!x) → x；已知恒量条件折叠 if(true)/if(false)。
/// 保守原则：只在无副作用搬移风险处替换（参数位置求值顺序与原执行顺序一致）；普通复制链不做搬移。
/// </summary>
public static class SemanticOptimizer
{
    // 形如  "Temp = expr;"  且左值是纯标识符（临时量）
    private static readonly Regex AssignRegex = new(
        @"^(?<indent>\s*)(?<lhs>[A-Za-z_]\w*)\s*=\s*(?<rhs>.+?);\s*$", RegexOptions.Compiled);

    /// <summary>把临时量内联到使用处；返回优化后的行列表（保留缩进）。</summary>
    public static List<string> OptimizeLines(List<string> lines)
    {
        var result = new List<string>(lines);
        for (var pass = 0; pass < 6; pass++)
        {
            if (!Pass(result)) break;
        }
        result = FoldConstants(result);
        result = SimplifyNames(result);
        return result;
    }

    private static bool Pass(List<string> lines)
    {
        var changed = false;
        var toRemove = new List<int>();

        // 1) 收集单次赋值临时量
        var tempDefs = new Dictionary<string, (int lineIdx, string rhs)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var m = AssignRegex.Match(lines[i]);
            if (!m.Success) continue;
            var lhs = m.Groups["lhs"].Value;
            if (!IsTempLike(lhs)) continue;
            if (tempDefs.ContainsKey(lhs)) tempDefs.Remove(lhs); // 二次赋值 → 不优化
            else tempDefs[lhs] = (i, m.Groups["rhs"].Value);
        }

        // 2) 先做所有参数内联替换（删除尚未发生，索引基于原表）
        foreach (var (temp, (defIdx, rhs)) in tempDefs)
        {
            if (defIdx >= lines.Count) continue;
            int useIdx = -1, useCount = 0;
            for (var i = defIdx + 1; i < lines.Count; i++)
            {
                if (IsComment(lines[i])) continue;
                if (CountToken(lines[i], temp) > 0)
                {
                    useCount++;
                    useIdx = i;
                    if (useCount > 1) break;
                }
            }
            if (useCount != 1 || useIdx < 0) continue;

            // 安全约束：赋值行与使用行之间只能隔"纯标识符复制行"（无调用/无副作用），保证求值顺序不变
            var betweenOk = true;
            for (var k = defIdx + 1; k < useIdx; k++)
            {
                if (!IsPureCopy(lines[k])) { betweenOk = false; break; }
            }
            if (!betweenOk) continue;

            if (!TryInlineIntoLine(lines[useIdx], temp, rhs, out var newLine)) continue;
            lines[useIdx] = newLine;
            toRemove.Add(defIdx);
            changed = true;
        }

        // 3) 倒序删除赋值行，避免索引错位
        foreach (var idx in toRemove.OrderByDescending(i => i))
            lines.RemoveAt(idx);

        return changed;
    }

    private static bool TryInlineIntoLine(string line, string temp, string rhs, out string newLine)
    {
        newLine = line;
        // 找每个独立出现；若恰好 1 处（非属性访问）则整体替换为 (rhs)
        // 允许位置：参数、if 条件、赋值右值等任意表达式上下文（相邻约束由调用方保证）
        var sb = new StringBuilder();
        var idx = 0;
        var matchedAny = false;
        foreach (Match m in Regex.Matches(line, $@"(?<![\w.]){Regex.Escape(temp)}(?![\w.])"))
        {
            sb.Append(line, idx, m.Index - idx);
            if (!matchedAny)
            {
                sb.Append('(').Append(rhs).Append(')'); // 括号包裹 rhs
                matchedAny = true;
            }
            else
            {
                newLine = line;
                return false; // 多次出现 → 放弃
            }
            idx = m.Index + temp.Length;
        }
        if (!matchedAny)
        {
            newLine = line;
            return false;
        }
        sb.Append(line, idx, line.Length - idx);
        newLine = sb.ToString();
        return true;
    }

    private static char PrevChar(string s, int idx)
    {
        for (var i = idx - 1; i >= 0; i--)
            if (!char.IsWhiteSpace(s[i])) return s[i];
        return '\0';
    }

    private static char NextChar(string s, int idx)
    {
        for (var i = idx; i < s.Length; i++)
            if (!char.IsWhiteSpace(s[i])) return s[i];
        return '\0';
    }

    private static int CountToken(string line, string token)
    {
        if (IsComment(line)) return 0;
        return Regex.Matches(line, $@"(?<![\w.]){Regex.Escape(token)}(?![\w.])").Count;
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//");

    /// <summary>纯标识符复制行（形如 `K2Node_Event_x = param;`，无函数调用/无副作用）。</summary>
    private static readonly Regex PureCopyRegex = new(
        @"^\s*[A-Za-z_]\w*\s*=\s*[A-Za-z_][\w.]*\s*;\s*$", RegexOptions.Compiled);
    private static bool IsPureCopy(string line)
    {
        if (IsComment(line)) return false;
        return PureCopyRegex.IsMatch(line) && !line.Contains("(");
    }

    /// <summary>P4 命名简化：去掉编译器临时量的统一前缀 CallFunc_（所有引用同步去掉，保持唯一性）。</summary>
    private static List<string> SimplifyNames(List<string> lines)
    {
        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (IsComment(l)) { outLines.Add(l); continue; }
            outLines.Add(Regex.Replace(l, @"(?<![\w.])CallFunc_", ""));
        }
        return outLines;
    }

    private static bool IsTempLike(string lhs) =>
        lhs.StartsWith("CallFunc_") || lhs.Contains("ReturnValue");

    /// <summary>P1/P3 常量折叠与简单化简（行内模式）</summary>
    private static List<string> FoldConstants(List<string> lines)
    {
        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (IsComment(l)) { outLines.Add(l); continue; }
            var s = l;
            var prev = "";
            while (prev != s)
            {
                prev = s;
                s = Regex.Replace(s, @"!\(\s*!(\s*\(?[^;()]+\)?)\s*\)", "$1"); // !(!x) → x
            }
            outLines.Add(s);
        }
        return outLines;
    }
}
