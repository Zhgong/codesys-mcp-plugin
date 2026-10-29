# 第 04 章：双循环测试体系与 2.8 秒极速在线验证

## 1. 工业自动化测试的困境
传统 PLC 自动化测试通常依赖硬件或耗时的仿真环境，面临两大痛点：
1. **冷启动周期极长**：每次测试重新开启 CODESYS IDE、载入工程、编译下载耗时 60~120 秒，开发者极度抗拒频繁运行测试。
2. **环境强耦合**：CI/CD 节点往往没有安装 CODESYS 或 SoftPLC License，导致离线阶段无法做回归把关。

---

## 2. 创新解决方案：双循环分层测试体系

```
 ┌────────────────────────────────────────────────────────┐
 │  内循环 (Inner Loop): 2.89 秒极速端到端 SoftPLC 验证   │
 │  - 直连运行中的 CODESYS.exe & SoftPLC                 │
 │  - 自动化全链路: Login -> 读写校验 -> Trace -> 停启   │
 └────────────────────────────────────────────────────────┘
                            ▲
                            │ 热重载验证 / 本地集成验收
                            │
 ┌────────────────────────────────────────────────────────┐
 │  外循环 (Outer Loop): 56 项离线单元与协议测试          │
 │  - 零 CODESYS 依赖，纯 Mock/内存环境                  │
 │  - 毫秒级执行: JSON-RPC 协议、正则引擎、Diff 对比     │
 └────────────────────────────────────────────────────────┘
```

---

## 3. 内循环：[`test_online_fast.py`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/test_online_fast.py)

该脚本直接通过命名管道与本地 CODESYS 通信，执行 6 大完整生命周期步骤：

```python
# 1. 查询项目状态
get_project_info() # 2ms

# 2. 静默无弹窗登录 SoftPLC
plc_login(mode='try_online_change', delete_foreign_apps=True) # 1390ms

# 3. 连续采样读取变量
read_variables(['MOTION_PRG.eDispState', 'MOTION_PRG.rDispPos', ...]) # 147ms

# 4. 写入并验证读回 (支持 IEC 前缀归一化)
write_variable(name='MOTION_PRG.PressRecipe.rTargetForce', value='5250.0') # 200ms

# 5. 动态注入并核验 Trace 变量
add_trace_variable(variable_name='MOTION_PRG.PressRecipe.rTargetForce') # 78ms
get_trace_info() # 9ms

# 6. 控制 PLC 运行状态并登出
plc_control('stop') # 161ms
plc_control('start') # 47ms
plc_logout() # 236ms
```
**测试耗时**：全流程仅 **2.89 秒**！支持 `--reload` 参数在测试前 150ms 自动热重载最新 DLL，实现极致的调试反馈循环。

---

## 4. 外循环：[`run_tests.ps1`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/run_tests.ps1)

不需要启动 CODESYS，不需要连接 PLC，纯粹在 C# 与 Python CLI 层面做严密覆盖：

- **ProtocolTests (6 项)**：JSON-RPC 2.0 格式校验、Ping/Pong、错误码。
- **ToolValidationTests (14 项)**：25 项工具参数必填性、边界值检测。
- **StreamFramingTests (6 项)**：CRLF、LF、UTF-8 中文字符流粘包与拆包。
- **SearchEngineTests (6 项)**：大小写敏感、正则单词边界、非 ASCII 搜索。
- **DiffEngineTests (6 项)**：纯增、纯删、混合修改、上下文比对。
- **DiffViewerTests (3 项)**：ST 语法高亮结构体格式化、HTML 报告渲染。
- **OnlineTests (11 项)**：离线容错、未打开工程安全拦截、IEC 归一化。
- **Python Integration Tests (4 项)**：命令行标准输入输出管道测试。

**测试耗时**：编译 + 执行全部 **56 项测试仅需 4.5 秒**。
