# 06. 配方与复杂数据引擎架构 (Recipe & Complex Data Engine)

## 1. 架构动机与核心挑战

在传统工业自动化和 PLC 交互中，上位机/AI 写入多变量配方时通常面临两大痛点：
1. **多周期撕裂风险 (Torn Writes)**：
   若通过逐个调用 `write_variable` 依次写入 10 个配方参数，由于网络传输与 PLC 循环扫描的异步性，PLC 逻辑可能在第 3 个与第 4 个参数写入之间执行了一次周期计算，导致控制器读到了“半新半旧”的异常工艺配方，引发工件压坏或伺服过载。
2. **IEC 结构体与数组的层级解析**：
   CODESYS 底层在线监控返回的复合数据格式通常为 IEC 原始元组字符串（如 `(rForce:=5000.0, rSpeed:=50.0)`）或需通过 `DUT (Data Unit Type)` 定义反射其字段，传统的简单标量读写无法直接满足高阶配方管理。

---

## 2. 两阶段原子提交引擎 (Two-Phase Commit Engine)

为了保证工业级的绝对原子性，`CodesysOnlineHelper.BatchWriteVariables` 实现了两阶段提交模式：

```
[Phase 1: 内存预设缓冲 (Memory Buffer)]
  set_prepared_value("MOTION_PRG.PressRecipe.rTargetForce", "5600.0")
  set_prepared_value("MOTION_PRG.PressRecipe.rFastSpeed", "46.0")
  set_prepared_value("MOTION_PRG.PressRecipe.rPressSpeed", "6.5")
  set_prepared_value("MOTION_PRG.PressRecipe.rWorkpieceContactZ", "81.5")
                     ↓ (此时 PLC 仍运行旧参数，无任何撕裂)
[Phase 2: 单周期原子提交 (Atomic Commit)]
  write_prepared_values() (在下一个 PLC 扫描周期边界全部生效)
                     ↓ (等待 100ms 监控缓存刷新)
[Phase 3: 异步回读与容差校验 (Readback & Tolerance Verification)]
  read_value(...) 匹配校验，若未就绪自动重试至多 3 次 (60ms)
```

### 关键机制设计：
- **`set_prepared_value(stExpression, stText)`**：仅在 CODESYS 在线驱动内存中挂起修改请求，控制器运行状态完全不受影响。
- **`write_prepared_values()`**：向控制器下发写入提交命令，运行时在下一次任务扫描周期的输入/准备阶段一次性同步全部变量。
- **网关监控缓存时延缓冲 (Gateway Settle Time)**：由于控制器执行与网关通信刷新存在 20ms~50ms 的扫描周期时差，引擎在提交后预留 100ms 刷新窗口，并支持 3 次重试自愈，彻底解决了上位机偶发回读旧值的问题。

---

## 3. 结构体与配方反射探测 (`read_struct` & `write_struct`)

### 3.1 `write_struct`：JSON 递归树扁平化
接收标准的层级 JSON 对象，自动递归解析为点号路径：
```json
{
  "rTargetForce": 5850.0,
  "rFastSpeed": 49.0,
  "rPressSpeed": 7.2,
  "rWorkpieceContactZ": 83.0,
  "rRetractSpeed": 65.0
}
```
自动转换为：
- `MOTION_PRG.PressRecipe.rTargetForce` -> `"5850.0"`
- `MOTION_PRG.PressRecipe.rFastSpeed` -> `"49.0"`
- ...
直接委托给 `BatchWriteVariables` 执行两阶段原子提交与回读。

### 3.2 `read_struct`：双模提取引擎
1. **模式一（直接 Tuple 解析）**：优先尝试对结构体主变量调用 `read_value`，若底层返回类似 `(rHomePos:=0, rTargetForce:=5000)` 的 IEC 元组字符串，直接通过递归状态机解析出结构体键值字典。
2. **模式二（DUT 成员反射探测）**：若底层只返回类型指纹，则通过项目 AST 树遍历 `ST_PressRecipe` 或 POU 声明中的 `STRUCT ... END_STRUCT` 语法树，自动探测出全部成员名字列表（如 `rHomePos`, `rWorkpieceContactZ`, ...），并发起批量子变量读取归一化。

---

## 4. 数组切片批量引擎 (`read_array`)

- 支持对任意 IEC 数组（如 `ARRAY[0..4] OF REAL`）进行区间切片读取：
  `read_array(variable_name="MOTION_PRG.aForceProfile", start_index=0, count=5)`
- 自动生成 `var[i]` 索引路径，执行类型前缀剥离（自动将 `REAL#100` 或 `100.0` 归一化为纯数值字符串），返回格式化列表。

---

## 5. 三层验收测试矩阵 (Three-Tier Acceptance Matrix)

| 层级 | 测试类型 | 覆盖工具/模块 | 执行耗时 | 依赖环境 |
|---|---|---|---|---|
| **Level 1** | C# 核心单元测试 (`run_tests.ps1`) | `BatchRecipeTests.cs` (17 项) + 协议校验 (48 项) | ~3.0s | 无需硬件/PLC，纯内存 Mock 验证 |
| **Level 2** | Python 协议回归测试 | `test_cli_protocol.py` (4 项) | ~1.5s | 本地 Named Pipe / Stdio CLI 进程 |
| **Level 3** | SoftPLC 实机联调 (HIL) | `test_recipe_batch.py` (5 步骤完整业务流) | ~7.2s | `CODESYSControlService -d` + `ServoPress_VirtualAxis.project` |

### Level 3 实机验收记录 (`ServoPress_VirtualAxis.project`)
- `Step 1/5` 软 PLC 在线登录：1296ms，状态 `run (program_loaded)`。
- `Step 2/5` `read_struct` 自动探测读取：1300ms，成功提取全部 10 项工艺参数。
- `Step 3/5` `batch_write_variables` 原子写入：212ms，4 项变量单周期原子生效并校验。
- `Step 4/5` `write_struct` 完整配方注入：1046ms，5 项变量全自动扁平化写入并验证。
- `Step 5/5` `read_array` 切片读取：1021ms，成功读取 `['100', '500', '1500', '3000', '5000']`。
- **总体验收耗时 7.21s，全项 PASS**。
