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

#### 2. IDE 视觉感知与状态指示器 (M2-C, 新增规划 - 2026/11)
- **需求痛点**：当前 MCP Server 在后台静默运行，工程师在 CODESYS IDE 界面中无法直观感知服务是否已启动、命名管道是否正常、是否有 AI 客户端（如 Cursor / Claude）正在接入或下发指令。
- **产品与架构设计**：
  - **原生状态栏嵌入 (Status Bar Indicator)**：通过 `CodesysContext.RunOnPrimaryThread` 在主窗口底部的 `StatusStrip` 注入专用指示器项（绿灯 `● MCP: Ready` / 黄灯 `● MCP: Busy` / 灰灯 `○ MCP: Offline`）。
  - **悬停 Tooltip 诊断卡片**：鼠标悬停显示状态看板（管道名称、已处理请求数、最近调用工具、最后报错信息）。
  - **快捷上下文菜单 (Context Menu)**：右键/左键点击弹出运维选项（「复制管道路径」、「查看最近 IDE 日志」、「重启 MCP 服务」）。
  - **零侵入与 Headless 兼容**：若在 `--noUI` 模式下启动，自动静默跳过 UI 控件挂载，确保 CI/CD 自动化不产生任何阻碍。

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
