# 第 00 章：产品研发路线图与商业演进计划 (Roadmap 2026)

> [!CAUTION]
> **商业机密与内部规划**
> 本路线图包含 CODESYS MCP 插件的商业化功能规划、协议扩展计划与企业版私有能力定义。严禁推送到公开开源仓库！

---

## 1. 2026 演进目标概览

```mermaid
flowchart LR
    M1["里程碑 M1: 基础在线稳定 (已达成)
    - 零卡顿 UI 封送
    - Stack empty 破解
    - 2.8s 在线闭环
    - 双仓库安全门禁"] --> M2A["里程碑 M2-A: 配方与数据引擎 (已达成)
    - batch_write_variables 原子提交
    - write_struct / read_struct 双模探测
    - read_array 切片读取
    - 三层测试矩阵 100% 通过"]
    
    M2A --> M2B["里程碑 M2-B: 调试与执行控制 (Q1)
    - 断点管理 Breakpoints
    - 单步执行 Step In/Over
    - CallStack 调用栈检查
    - Call Hierarchy 分析"]
    
    M2A --> M2C["里程碑 M2-C: IDE 视觉指示与看板 (Q1)
    - 底部状态栏原生指示器
    - 运行态绿灯/未启动灰灯
    - 悬停 Tooltip 诊断卡片
    - 点击弹出 MCP 运维菜单"]
    
    M2B --> M3["里程碑 M3: 复杂工程自动化 (Q2)
    - 库管理器 (LibManager) 安装
    - 设备拓扑自动重构
    - 批量 ST 代码重构与格式化
    - CI/CD Headless 编译管道"]

    M2C --> M3

    M3 --> M4["里程碑 M4: 企业版商业化套件 (Q3-Q4)
    - 多 PLC 集群并行调度
    - 工业网络通信协议模拟 (EtherCAT/Profinet)
    - 智能代码静态安全与性能分析器"]
```

---

## 2. 核心特性拆解

### 阶段一：高阶配方、在线调试与 IDE 视觉感知 (Q1)

#### 1. 复杂结构体与配方引擎 (M2-A, 已达成 - 2026/10)
- `batch_write_variables`：双阶段单周期原子提交（零撕裂保证）。
- `write_struct`：JSON 递归树自动扁平化注入。
- `read_struct`：DUT 语法树 AST 成员自动探测与 Tuple 双模提取。
- `read_array`：IEC 数组区间切片批量读取与类型剥离。

#### 2. IDE 视觉感知与状态指示器 (M2-C, 阶段1已交付 - 2026/10)
- **交付状态**：
  - [x] **Phase 1: 原生状态栏嵌入与实时感知 (已完成)**
    - `CodesysIdeUiHelper.cs`：动态接入 CODESYS `MainForm` 原生底栏 `_progressStatusStrip`（无原生底栏时自动创建轻量停靠面板）。
    - 实时状态动态变色：`● MCP: Online`（ForestGreen）、`● MCP: Busy (<tool>)`（DarkOrange）、`○ MCP: Offline`（Gray）、`▲ MCP: Warning`（Crimson）。
    - Tooltip 诊断悬停信息卡：实时显示服务器版本、命名管道地址、请求计数、当前运行工具与健康状态。
    - 工具集成：发布 Tool #30 `get_ui_indicator_status`，支持客户端及 IDE 相互感知状态。
    - 兼容性：100% 兼容 `--noUI` 模式（静默降级，不抛异常）。
    - 验收：77/77 项单元与协议测试全部 PASS，Live SoftPLC 实时运行验证全部 PASS。
  - [ ] **Phase 2: 交互菜单与运维弹窗 (规划中)**
    - 状态徽章点击事件、弹出运维菜单（「复制管道地址」、「重启 MCP 服务」、「导出系统日志」）。

#### 3. 调试与在线执行控制 (M2-B, 下一阶段)
- **断点控制**：实现 `set_breakpoint`、`clear_breakpoint`、`toggle_breakpoint`。
- **调用栈追踪**：实现 `get_call_stack`、`get_current_line`。
- **单步与执行控制**：实现 `step_into`、`step_over`、`step_out`、`resume`。

---

### 阶段二：工程构建与依赖管理 (Q2)
- **自动添加/解析设备拓扑**：动态插入 SoftMotion 虚拟轴、CanOpen 从站、EtherCAT 伺服驱动器。
- **库文件自动管理**：检测缺失库依赖并静默从中央仓库下载安装。

---

### 阶段三：云原生与协同平台 (Q3-Q4)
- **容器化 SoftPLC 矩阵**：在 Docker / Hyper-V 容器集群内自动化调度测试任务。
- **AI 智能代码审查器**：自动分析 IEC 61131-3 代码圈复杂度、非确定性循环风险及指针越界隐患。
