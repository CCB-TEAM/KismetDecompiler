# KismetDecompiler — UE5 蓝图 Kismet 字节码反编译器

基于 **UAssetAPI**（`KismetExpression[]` AST）的 Unreal 蓝图反编译工具：
把 `FunctionExport.ScriptBytecode`（已解析的 Kismet 字节码表达式树）渲染为可读的 C++ 风格伪代码，并做结构化控制流还原与语义优化。

## 特性

- **表达式渲染**：`ExprRenderer` — AST → 伪代码字符串（变量路径/函数名/常量/类型）
- **控制流结构化**：CFG 切块 + if/else/汇合反转还原（17/19 函数零 goto）
- **反 Dispatch**：识别 `ExecuteUbergraph`（`ComputedJump(EntryPoint)` 起手，EntryPoint == 字节偏移），还原为 `switch (EntryPoint)`，case 标注来源事件函数
- **Dispatch 内联**（实验）：case 体内联回各事件函数，跳转段栈式追踪内联
- **语义优化**（实验）：临时量值流内联（含跨纯赋值行传播）、`CallFunc_` 命名简化、常量折叠
- 全程作为开关叠加，不覆盖基础路径

## 用法

```
用法: KismetDecompiler --uasset <蓝图.uasset> --usmap <mapping.usmap> [--inline] [--opt] [--out <dir>]

  --uasset  目标 .uasset 蓝图文件
  --usmap   UE5 unversioned properties 映射文件（.usmap）
  --out     输出目录（默认 ./Decompiled）
  --inline  dispatch case 体内联回事件函数（实验）
  --opt     语义优化：临时量值流内联 / 常量折叠 / 命名简化（实验）
```

示例：

```bash
# 默认：结构化反编译 + ExecuteUbergraph 反 Dispatch
KismetDecompiler --uasset BP_Card.uasset --usmap mappings.usmap

# 开启全部实验特性
KismetDecompiler --uasset BP_Card.uasset --usmap mappings.usmap --inline --opt
```

产物输出到 `Decompiled\`（每函数一个 `*.txt` + `_all.txt`；`--inline` 时事件内联版在 `Decompiled\Inline\`）。

## 项目结构

```
KismetDecompiler/
├── KismetDecompiler.csproj        # net10.0 + UAssetAPI 1.1.0
├── Program.cs                     # 入口：uasset/usmap 加载 + 模式开关
├── ExprRenderer.cs                # 表达式 → 伪代码（含名称解析）
├── StructuredKismetDecompiler.cs  # CFG 结构化 + 反 Dispatch（switch-case）
├── DispatchInliner.cs             # [实验] dispatch 内联回事件函数
├── SemanticOptimizer.cs           # [实验] 行级语义优化（值流）
└── Decompiled/                    # 输出（不入库）
```

## 技术要点

- UAssetAPI 已把字节码解析为 `KismetExpression[]`（`StructExport.ScriptBytecode`），本工具只做渲染/结构/优化
- `EX_*` 表达式的字节偏移用 `GetSize(asset)` 累计，与 `CodeOffset`/`EntryPoint` 精确对齐
- `ExecuteUbergraph` 调用在事件函数中是 `EX_LocalFinalFunction`（`StackNode.Index` 匹配 export）
- UE 函数调用 out 形参：`getSkirmishBP(Temp)` 直接把结果写引用参数（非返回赋值），此类不做内联

## 已知限制

- Dispatch case 内个别跨段 goto 以 label/goto 形式保真保留（不强制结构化）
- 行级优化仅处理"赋值一次 + 相邻使用"的临时量，引用形参模式暂不内联
- 实验性功能可能随输入变化有边界情况
