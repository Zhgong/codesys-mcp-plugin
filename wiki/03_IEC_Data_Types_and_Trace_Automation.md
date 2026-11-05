# 第 03 章：IEC 61131-3 数据类型归一化比对与 Trace 动态注入

## 1. 工业数据类型归一化与回读校验

### 1.1 校验误判问题
在工业自动化控制中，写入变量 `"5250.0"`，CODESYS PLC 内部成功写入，但回读返回带 IEC 61131-3 类型前缀的格式：
```
写入值: "5250.0"
回读值: "REAL#5250"
```
由于简单的 `string.Equals("5250.0", "REAL#5250")` 返回 `false`，导致自动化测试误判“写入校验失败”。类似的情况还包括：
- `BOOL#TRUE` vs `"true"`
- `LREAL#12.345678` vs `"12.3456"`
- `INT#10` vs `"10"`

### 1.2 归一化与容差算法实现
我们在 [`CodesysOnlineHelper.cs`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/CodesysMcp/CodesysOnlineHelper.cs#L41-L75) 中提炼了标准比对算法：
```csharp
public static string NormalizeIecValue(string raw)
{
    if (raw == null) return string.Empty;
    string s = raw.Trim();
    int hashIdx = s.IndexOf('#');
    if (hashIdx >= 0)
    {
        s = s.Substring(hashIdx + 1).Trim();
    }
    return s;
}

public static bool ValuesMatch(string actual, string target)
{
    string normActual = NormalizeIecValue(actual);
    string normTarget = NormalizeIecValue(target);

    // 1. 忽略大小写字符串直接匹配
    if (string.Equals(normActual, normTarget, StringComparison.OrdinalIgnoreCase))
        return true;

    // 2. 浮点数 Delta 容差比对 (容忍精度微差)
    if (double.TryParse(normActual, NumberStyles.Any, CultureInfo.InvariantCulture, out var d1) &&
        double.TryParse(normTarget, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2))
    {
        return Math.Abs(d1 - d2) < 0.001;
    }

    // 3. 布尔值智能比对
    if (bool.TryParse(normActual, out var b1) && bool.TryParse(normTarget, out var b2))
    {
        return b1 == b2;
    }

    return false;
}
```
**单元测试保障**：在 `CodesysMcpTests/OnlineTests.cs` 中覆盖各类 IEC 类型前缀、负数、大小写与浮点精度测试。

---

## 2. 动态 Trace 图表对象解析与变量注入

### 2.1 Trace 对象模型
CODESYS 项目树中的 `Trace` 不是一个纯粹的 POU，而是一个具备图表渲染与数据采样的复合对象：
- 节点包装类：`IExtendedObject<IScriptObject>`
- 实际承载扩展：`_3S.CoDeSys.ScriptDriverTraceObject.ScriptTraceObject`（位于 `ScriptDriverTraceObject.plugin.dll`）

### 2.2 动态注入核心逻辑
通过 MCP 工具 [`add_trace_variable`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/CodesysMcp/CodesysOperations.cs#L1119)，代码在 UI 线程上动态穿透扩展树：
```csharp
// 1. 查找 Trace 对象并解析 ScriptTraceObject 扩展
var tObj = FindObjectByName("Trace");
var exts = tObj.GetType().GetProperty("Extensions")?.GetValue(tObj, null) as IEnumerable;
object traceExt = exts.FirstOrDefault(x => x.GetType().Name.Contains("TraceObject"));

// 2. 检查或新增到 variable_list
var varList = traceExt.GetType().GetProperty("variable_list")?.GetValue(traceExt, null);
var targetVar = FindVariable(varList, "MOTION_PRG.PressRecipe.rTargetForce");
if (targetVar == null)
{
    targetVar = varList.GetType().GetMethod("add").Invoke(varList, null);
    targetVar.GetType().GetProperty("variable_name").SetValue(targetVar, "MOTION_PRG.PressRecipe.rTargetForce", null);
}

// 3. 关联至曲线图表 (Diagram)
var diagrams = traceExt.GetType().GetProperty("diagrams")?.GetValue(traceExt, null) as IEnumerable;
foreach (var diag in diagrams)
{
    diag.GetType().GetMethod("add_diagram_variable")?.Invoke(diag, new object[] { targetVar, false });
}
```

### 2.3 状态回读与验证 ([`get_trace_info`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/CodesysMcp/CodesysOperations.cs#L1224))
新增 `get_trace_info` 工具，可直接通过 JSON-RPC 查询 Trace 内已注册的全部变量及图表数，实现注入后 100% 闭环核验。
