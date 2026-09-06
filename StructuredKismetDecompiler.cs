using UAssetKismet.Experimental;
using System.Text;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace UAssetKismet;

/// <summary>
/// v3：CFG + 结构化控制流还原。
/// 将 ScriptBytecode 切为基本块 → 建 CFG → 递归做 if/else/while 结构识别，
/// 未识别路径退回 label/goto。
/// </summary>
public class StructuredKismetDecompiler
{
    private readonly UAsset _asset;
    private readonly ExprRenderer _renderer;

    // 基本块
    private sealed class Block
    {
        public int Id;
        public int Start;          // stmts 起始下标
        public int End;            // stmts 结束下标（不含）
        public string? CondExpr;   // 若为条件跳转块：条件表达式文本
        public int NextIdx = -1;   // fallthrough 块 id
        public int BranchIdx = -1; // 条件为真时跳转的块 id（JumpIfNot 的 false 目标）
        public string? JumpCode;   // 无条件跳转文本（保留用）
        public long Offset;
    }

    private List<(long off, KismetExpression expr)> _stmts = new();
    private Dictionary<long, int> _idxOf = new();
    private List<Block> _blocks = new();
    private Dictionary<int, Block> _blockById = new();
    private HashSet<int> _emitted = new();
    private StringBuilder _sb = new();
    private int _ind;
    private readonly string _funcName;

    public StructuredKismetDecompiler(UAsset asset, string funcName, UhtSignatureIndex? signatures = null)
    {
        _asset = asset;
        _renderer = new ExprRenderer(asset, signatures);
        _funcName = funcName;
    }

    public string Decompile(FunctionExport fn)
    {
        var exprs = fn.ScriptBytecode ?? Array.Empty<KismetExpression>();
        // 1) 顶层语句 + 字节偏移
        _stmts.Clear(); _idxOf.Clear();
        long pos = 0;
        for (var i = 0; i < exprs.Length; i++)
        {
            _idxOf[pos] = _stmts.Count;
            _stmts.Add((pos, exprs[i]));
            try { pos += exprs[i].GetSize(_asset); } catch { pos += 1; }
        }
        // 过滤空语句（Nothing/EndOfScript 保留跳转位置）
        return BuildAndStrucuture(fn);
    }

    /// <summary>反 Dispatch：把 ExecuteUbergraph 的 EntryPoint 跳转表还原为 switch-case。</summary>
    public static string DecompileDispatch(UAsset asset, FunctionExport uber,
        IEnumerable<(long n, string caller)> calls, UhtSignatureIndex? signatures = null)
    {
        var d = new StructuredKismetDecompiler(asset, uber.ObjectName.ToString(), signatures);
        return d.EmitDispatch(uber, calls.OrderBy(c => c.n).ToList());
    }

    private string EmitDispatch(FunctionExport uber, List<(long n, string caller)> calls)
    {
        var exprs = uber.ScriptBytecode ?? Array.Empty<KismetExpression>();
        _stmts.Clear(); _idxOf.Clear();
        long pos = 0;
        for (var i = 0; i < exprs.Length; i++)
        {
            _idxOf[pos] = _stmts.Count;
            _stmts.Add((pos, exprs[i]));
            try { pos += exprs[i].GetSize(_asset); } catch { pos += 1; }
        }
        if (calls.Count == 0) return EmitLabeled();

        // case 起点也要切成块边界
        var extraStarts = new List<int>();
        var caseEntries = new List<(int blockId, long n, string caller)>();
        foreach (var (n, caller) in calls)
            if (_idxOf.TryGetValue(n, out var si))
                extraStarts.Add(si);

        BuildBlocksCore(extraStarts);

        foreach (var (n, caller) in calls)
            if (_idxOf.TryGetValue(n, out var si) && BlockOf(si) >= 0)
                caseEntries.Add((BlockOf(si), n, caller));
        var otherCaseBlocks = new HashSet<int>(caseEntries.Select(c => c.blockId));

        _sb.Clear(); _ind = 0;
        _sb.AppendLine($"void {uber.ObjectName}(int EntryPoint)");
        _sb.AppendLine("{");
        _sb.AppendLine("// 反 Dispatch：ComputedJump 按 EntryPoint 字节偏移跳转");
        _sb.AppendLine("switch (EntryPoint)");
        _sb.AppendLine("{");
        _ind = 1;

        foreach (var (blockId, n, caller) in caseEntries.OrderBy(c => c.n))
        {
            _sb.Append(' ', _ind * 4).AppendLine($"case {n}: // ← {caller}");
            _ind++;
            EmitDispatchCaseLinear(blockId, otherCaseBlocks);
            _ind--;
        }

        _ind = 0;
        _sb.AppendLine("}");
        _sb.AppendLine("}");
        return _sb.ToString();
    }

    /// <summary>case 体线性保真输出：保留 if-goto / goto，遇到 return 区或下一 case 起点结束。</summary>
    private void EmitDispatchCaseLinear(int entryBlock, HashSet<int> otherCaseBlocks)
    {
        var id = entryBlock;
        var visited = new HashSet<int>();
        var tailEmitsBreak = false;
        while (id >= 0 && id < _blocks.Count && visited.Add(id))
        {
            var b = _blockById[id];
            // 仅当走到其它 case 的入口块时结束本 case（本 case 入口块自身除外）
            if (otherCaseBlocks.Contains(id) && id != entryBlock) break;

            // 顺序语句
            var lastIdx = b.End - 1;
            for (var i = b.Start; i < lastIdx; i++)
            {
                var e = _stmts[i].expr;
                if (e is EX_Nothing or EX_EndOfScript) continue;
                var l = RenderStatement(e);
                if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
            }

            var tail = _stmts[lastIdx].expr;
            switch (tail)
            {
                case EX_JumpIfNot j:
                    AppendLine($"if (!{RenderExpr(j.BooleanExpression)}) goto L_{j.CodeOffset:X4};");
                    id = b.NextIdx; // fallthrough
                    continue;
                case EX_Jump j:
                    var tb = TargetBlock(j.CodeOffset);
                    if (tb >= 0 && IsReturnOnly(tb))
                    {
                        AppendLine("return;");
                        tailEmitsBreak = true;
                    }
                    else
                    {
                        AppendLine($"goto L_{j.CodeOffset:X4};");
                    }
                    break; // case 结束（jump 离开本 case）
                case EX_Return r:
                    AppendLine(r.ReturnExpression is EX_Nothing ? "return;" : $"return {RenderExpr(r.ReturnExpression)};");
                    tailEmitsBreak = true;
                    break;
                case EX_EndOfScript:
                    break;
                default:
                    id = b.NextIdx;
                    continue;
            }
            break;
        }

        if (!tailEmitsBreak)
        {
            // 块链自然耗尽或掉出：补 break 防穿透
            if (visited.Count > 0) AppendLine("break;");
        }
        else
        {
            AppendLine("break;"); // return 后接 break（无害，防遗漏）
        }
    }

    private void BuildBlocksCore(List<int>? extraStarts = null)
    {
        _blocks.Clear(); _blockById.Clear();
        var starts = new SortedSet<int>();
        foreach (var s in _stmts)
        {
            var jmp = TargetIndex(s.expr);
            if (jmp >= 0) starts.Add(jmp);
        }
        if (extraStarts is not null)
            foreach (var x in extraStarts) starts.Add(x);
        starts.Add(0);
        starts.Add(_stmts.Count);

        var arr = starts.ToArray();
        for (var i = 0; i < arr.Length - 1; i++)
        {
            if (arr[i] >= arr[i + 1]) continue;
            _blocks.Add(new Block { Id = _blocks.Count, Start = arr[i], End = arr[i + 1] });
        }
        foreach (var b in _blocks) _blockById[b.Id] = b;

        foreach (var b in _blocks)
        {
            b.Offset = _stmts[b.Start].off;
            var last = _stmts[b.End - 1].expr;
            switch (last)
            {
                case EX_JumpIfNot j:
                    b.CondExpr = RenderExpr(j.BooleanExpression);
                    b.NextIdx = b.Id + 1 < _blocks.Count ? b.Id + 1 : -1;
                    b.BranchIdx = TargetBlock(j.CodeOffset);
                    break;
                case EX_Jump j:
                    b.JumpCode = $"goto L_{j.CodeOffset:X4}";
                    b.NextIdx = -1;
                    b.BranchIdx = TargetBlock(j.CodeOffset);
                    break;
                default:
                    b.NextIdx = b.Id + 1 < _blocks.Count ? b.Id + 1 : -1;
                    break;
            }
        }
    }

    private string BuildAndStrucuture(FunctionExport fn)
    {
        _blocks.Clear(); _blockById.Clear();
        BuildBlocks();

        // dispatch 型函数（ComputedJump 开头，如 ExecuteUbergraph）→ 线性 + label 保真输出
        if (_stmts.Count > 0 && _stmts[0].expr is EX_ComputedJump)
            return EmitLabeled();

        _sb.Clear(); _ind = 0; _emitted.Clear();
        _sb.AppendLine($"void {_funcName}()");
        _sb.AppendLine("{");
        _ind = 1;

        if (_blocks.Count > 0)
            EmitBlock(_blocks[0].Id, new HashSet<int>());

        _ind = 0;
        _sb.AppendLine("}");
        return _sb.ToString();
    }

    /// <summary>线性输出：每块打 label + 语句，跳转保留 goto（语义无损，类似 v2）。</summary>
    private string EmitLabeled()
    {
        var targets = new HashSet<long>();
        foreach (var b in _blocks)
            CollectLabelTargets(_stmts[b.End - 1].expr, targets);

        _sb.Clear(); _ind = 0;
        _sb.AppendLine($"void {_funcName}()");
        _sb.AppendLine("{");
        _ind = 1;

        foreach (var b in _blocks)
        {
            if (targets.Contains(b.Offset))
                _sb.Append(' ', _ind * 4).Append($"L_{b.Offset:X4}:").AppendLine();

            for (var i = b.Start; i < b.End - 1; i++)
            {
                var e = _stmts[i].expr;
                if (e is EX_EndOfScript or EX_Nothing) continue;
                var l = RenderStatement(e);
                if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
            }

            switch (_stmts[b.End - 1].expr)
            {
                case EX_JumpIfNot j:
                    AppendLine($"if (!{RenderExpr(j.BooleanExpression)}) goto L_{j.CodeOffset:X4};");
                    break;
                case EX_Jump j:
                    AppendLine($"goto L_{j.CodeOffset:X4};");
                    break;
                case EX_Return r:
                    var ret = r.ReturnExpression is EX_Nothing ? "return" : $"return {RenderExpr(r.ReturnExpression)}";
                    AppendLine(ret + ";");
                    break;
            }
        }

        _ind = 0;
        _sb.AppendLine("}");
        return _sb.ToString();
    }

    private static void CollectLabelTargets(KismetExpression e, HashSet<long> set)
    {
        switch (e)
        {
            case EX_Jump j: set.Add(j.CodeOffset); break;
            case EX_JumpIfNot j: set.Add(j.CodeOffset); break;
        }
    }

    private void BuildBlocks() => BuildBlocksCore();

    private int TargetBlock(long off) => _idxOf.TryGetValue(off, out var si) ? BlockOf(si) : -1;
    private int BlockOf(int stmtIdx)
    {
        foreach (var b in _blocks)
            if (stmtIdx >= b.Start && stmtIdx < b.End) return b.Id;
        return -1;
    }

    /// <summary>块内只有 return / nothing / EndOfScript（作为跳转目标时可折叠成 return）。</summary>
    private bool IsReturnOnly(int blockId)
    {
        if (!_blockById.TryGetValue(blockId, out var b)) return false;
        var hasReturn = false;
        for (var i = b.Start; i < b.End; i++)
        {
            switch (_stmts[i].expr)
            {
                case EX_Return: hasReturn = true; break;
                case EX_Nothing or EX_EndOfScript: break;
                default: return false;
            }
        }
        return hasReturn;
    }

    // ============ 结构化发射 ============

    private void EmitBlock(int id, HashSet<int> exits)
    {
        var b = _blockById[id];
        if (b is null) return;
        if (!_emitted.Add(id)) return; // 已被发射（共享块），避免重复

        // 顺序语句
        for (var i = b.Start; i < b.End; i++)
        {
            var (off, e) = _stmts[i];
            if (e is EX_EndOfScript) { return; }
            if (e is EX_Jump or EX_JumpIfNot) continue; // 块尾控制语句单独处理
            var line = RenderStatement(e);
            if (!string.IsNullOrEmpty(line)) AppendLine(line + ";");
        }

        // 尾控制语句
        var tail = _stmts[b.End - 1].expr;
        switch (tail)
        {
            case EX_JumpIfNot j:
                EmitCondJump(j, b, exits);
                break;
            case EX_Jump j:
                var tb = TargetBlock(j.CodeOffset);
                if (tb >= 0 && IsReturnOnly(tb))
                {
                    AppendLine("return;");
                    return;
                }
                if (tb < 0 || exits.Contains(tb))
                {
                    // 跳向父结构退出 → 无操作（结构闭合）
                }
                else
                {
                    AppendLine($"goto L_{j.CodeOffset:X4};");
                    // 不再继续后续块
                    return;
                }
                break;
            case EX_Return r:
                var ret = r.ReturnExpression is EX_Nothing ? "return" : $"return {RenderExpr(r.ReturnExpression)}";
                AppendLine(ret + ";");
                return;
            default:
                break;
        }

        // fallthrough 到下一块
        if (b.NextIdx >= 0 && b.NextIdx < _blocks.Count && !exits.Contains(b.NextIdx))
            EmitBlock(b.NextIdx, exits);
    }

    private void EmitCondJump(EX_JumpIfNot j, Block b, HashSet<int> exits)
    {
        var cond = RenderExpr(j.BooleanExpression);           // JumpIfNot: cond=false 时跳转
        var falseTarget = TargetBlock(j.CodeOffset);          // cond=false → branch
        var fall = NextBlockOr(b);                            // cond=true  → fallthrough

        // ---- 简单 while/do：回边，false 目标早于当前位置 ⇒ while(true) 内嵌 if-break 简化处理 ----
        if (falseTarget >= 0 && _blockById[falseTarget].Id < b.Id)
        {
            // 环形：cond 在底部跳回 → do { } while(!cond) 不精确，用 while(true)+if(!cond)break 近似
            AppendLine("while (true)");
            AppendLine("{");
            _ind++;
            AppendLine($"if (!{cond}) break;");
            var exit = new HashSet<int>(exits) { falseTarget };
            var nb = NextBlockOr(b);
            if (nb >= 0) EmitBlock(nb, exit);
            _ind--;
            AppendLine("}");
            return;
        }

        // ---- if / if-else：false 分支是后续块 ----
        if (falseTarget >= 0 && fall >= 0)
        {
            // 分支汇合：else 体尾部 Jump 回到 fallthrough 首块 → 反转条件，else 变 if 体
            var tBlock = _blockById.GetValueOrDefault(falseTarget);
            if (tBlock is { JumpCode: not null } && tBlock.BranchIdx == fall)
            {
                AppendLine($"if (!{cond})");
                AppendLine("{");
                _ind++;
                // 输出 else 体（不含尾 goto）
                for (var i = tBlock.Start; i < tBlock.End - 1; i++)
                {
                    var l = RenderStatement(_stmts[i].expr);
                    if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
                }
                _ind--;
                AppendLine("}");
                if (!exits.Contains(fall)) EmitBlock(fall, exits);
                return;
            }

            // then = fallthrough 链（直到到达 falseTarget 或自然终结）
            AppendLine($"if ({cond})");
            AppendLine("{");
            _ind++;
            var thenExit = new HashSet<int>(exits) { falseTarget };
            EmitSequential(fall, thenExit, out var thenConsumed);
            _ind--;
            AppendLine("}");

            // else = falseTarget 开始，直到 then 合并点（thenConsumed）
            if (thenConsumed >= 0 && thenConsumed != falseTarget)
            {
                AppendLine("else");
                AppendLine("{");
                _ind++;
                EmitSequential(falseTarget, new HashSet<int> { thenConsumed }, out _);
                _ind--;
                AppendLine("}");
                // 继续 from thenConsumed
                var nb = _blockById.GetValueOrDefault(thenConsumed);
                if (nb != null && !exits.Contains(thenConsumed)) EmitBlock(thenConsumed, exits);
            }
            else
            {
                if (thenConsumed >= 0 && !exits.Contains(thenConsumed))
                {
                    var nb = _blockById.GetValueOrDefault(thenConsumed);
                    if (nb != null) EmitBlock(thenConsumed, exits);
                }
                else
                {
                    // then 直接执行到 falseTarget → 无需 else，继续 falseTarget
                    var nb = _blockById.GetValueOrDefault(falseTarget);
                    if (nb != null && !exits.Contains(falseTarget)) EmitBlock(falseTarget, exits);
                }
            }
            return;
        }

        // 无法结构化的条件 → goto 形式
        AppendLine($"if (!{cond}) goto L_{j.CodeOffset:X4};");
    }

    /// <summary>顺序消费从 block 开始的链，直到到达 stop 块或结构性返回。返回合并点块 id（-1 = 到函数尾/return）。</summary>
    private void EmitSequential(int startId, HashSet<int> stop, out int mergeBlock)
    {
        var id = startId;
        while (id >= 0 && !stop.Contains(id) && id < _blocks.Count)
        {
            var b = _blockById[id];
            // 若该块以无条件 Jump 结束，且目标为后续块 ⇒ 该 Jump 是 then→merge 的桥，merge=target
            if (b.JumpCode is not null)
            {
                var tgt = b.BranchIdx;
                if (tgt >= 0 && tgt > id)
                {
                    // then 体到此为止，但块内顺序语句先输出
                    for (var i = b.Start; i < b.End - 1; i++)
                    {
                        var l = RenderStatement(_stmts[i].expr);
                        if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
                    }
                    mergeBlock = tgt;
                    return;
                }
                // 反向/不可达目标：输出 goto
                for (var i = b.Start; i < b.End - 1; i++)
                {
                    var l = RenderStatement(_stmts[i].expr);
                    if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
                }
                AppendLine(b.JumpCode + ";");
                mergeBlock = -1;
                return;
            }
            if (b.CondExpr is not null)
            {
                // 嵌套 if/while —— 直接递归（会让 stop 失效；嵌套合并处由递归处理）
                EmitBlock(id, stop);
                mergeBlock = -1;
                return;
            }
            // 顺序块：输出内容
            for (var i = b.Start; i < b.End; i++)
            {
                var e = _stmts[i].expr;
                if (e is EX_EndOfScript) { mergeBlock = -1; return; }
                var l = RenderStatement(e);
                if (!string.IsNullOrEmpty(l)) AppendLine(l + ";");
            }
            if (b.NextIdx < 0)
            {
                // 无后继（块尾是 Return？块内已含 return 由 RenderStatement 输出但未终止…此处终结）
                mergeBlock = -1;
                return;
            }
            id = b.NextIdx;
        }
        mergeBlock = stop.Count > 0 ? stop.First() : -1;
        if (stop.Contains(id)) mergeBlock = id;
        else mergeBlock = -1;
    }

    private int NextBlockOr(Block b) => b.NextIdx >= 0 ? b.NextIdx : -1;

    // ============ 表达式渲染（复用 v2 简化）============

    private string RenderStatement(KismetExpression e)
    {
        var s = _renderer.Render(e);
        if (s is "return" or "") return s;
        return s;
    }

    private string RenderExpr(KismetExpression? e)
    {
        if (e is null) return "null";
        return _renderer.Render(e);
    }

    private int TargetIndex(KismetExpression e) => e switch
    {
        EX_Jump j => _idxOf.TryGetValue(j.CodeOffset, out var si) ? si : -1,
        EX_JumpIfNot j => _idxOf.TryGetValue(j.CodeOffset, out var si) ? si : -1,
        _ => -1
    };

    private void AppendLine(string s) => _sb.Append(' ', _ind * 4).AppendLine(_renderer.CleanSelf(s));
}
