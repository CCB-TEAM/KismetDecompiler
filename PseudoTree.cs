namespace UAssetKismet.Experimental;

/// <summary>
/// 伪代码括号树：把（自产）伪代码按 '{' '}' 嵌套解析成树。
/// 节点保留原始行索引（LineIndex ≥ 0），优化 pass 可直接改回原始行，无需渲染往返。
/// if/else 只影响作用域划分（else 行是叶子，不参与块内 def/use）。
/// </summary>
public sealed class StmtNode
{
    public string Text = "";
    public int LineIndex = -1;              // 原始行号（-1 表示 '{' 合成节点或 root）
    public List<StmtNode> Children = new();
    public StmtNode? Parent;
    public bool IsBlock => Text == "{";
}

public static class PseudoTreeParser
{
    public static StmtNode Parse(List<string> lines)
    {
        var root = new StmtNode { Text = "(root)" };
        var cur = root;
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].TrimEnd('\r', '\n');
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed == "{")
            {
                var b = new StmtNode { Text = "{", Parent = cur };
                cur.Children.Add(b);
                cur = b; // '{' 之后内容进入该块
                continue;
            }
            if (trimmed == "}")
            {
                cur = cur.Parent ?? root;
                continue;
            }
            // 保留原始行文本（含缩进），替换时缩进不丢失
            var n = new StmtNode { Text = raw, LineIndex = i, Parent = cur };
            cur.Children.Add(n);
        }
        return root;
    }

    /// <summary>递归遍历所有非 '{' 节点（保序）。</summary>
    public static IEnumerable<StmtNode> Walk(StmtNode node)
    {
        foreach (var c in node.Children)
        {
            if (c.Text == "{")
            {
                foreach (var x in Walk(c)) yield return x;
            }
            else
            {
                yield return c;
                foreach (var x in Walk(c)) yield return x;
            }
        }
    }
}
