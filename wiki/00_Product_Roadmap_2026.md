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
    
    M2B --> M3["里程碑 M3: 复杂工程自动化 (Q2)
    - 库管理器 (LibManager) 安装
    - 设备拓扑自动重构
    - 批量 ST 代码重构与格式化
    - CI/CD Headless 编译管道"]

    M3 --> M4["里程碑 M4: 企业版商业化套件 (Q3-Q4)
    - 多 PLC 集群并行调度
    - 工业网络通信协议模拟 (EtherCAT/Profinet)
    - 智能代码静态安全与性能分析器"]
```

---

## 2. 核心特性拆解

### 阶段一：高阶在线配方与调试能力 (Q1)
- **复杂结构体与配方引擎 (M2-A, 已达成 - 2026/10)**：
  - `batch_write_variables`：双阶段单周期原子提交（零撕裂保证）。
  - `write_struct`：JSON 递归树自动扁平化注入。
  - `read_struct`：DUT 语法树 AST 成员自动探测与 Tuple 双模提取。
  - `read_array`：IEC 数组区间切片批量读取与类型剥离。
- **断点控制 (M2-B, 下一阶段)**：实现 `set_breakpoint`、`clear_breakpoint`、`toggle_breakpoint`。
- **调用栈追踪 (M2-B)**：实现 `get_call_stack`、`get_current_line`。

### 阶段二：工程构建与依赖管理 (Q2)
- **自动添加/解析设备拓扑**：动态插入 SoftMotion 虚拟轴、CanOpen 从站、EtherCAT 伺服驱动器。
- **库文件自动管理**：检测缺失库依赖并静默从中央仓库下载安装。

### 阶段三：云原生与协同平台 (Q3-Q4)
- **容器化 SoftPLC 矩阵**：在 Docker / Hyper-V 容器集群内自动化调度测试任务。
- **AI 智能代码审查器**：自动分析 IEC 61131-3 代码圈复杂度、非确定性循环风险及指针越界隐患。
