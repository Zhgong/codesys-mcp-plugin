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
    - 双仓库安全门禁"] --> M2["里程碑 M2: 调试与分析增强 (Q1)
    - 断点管理 Breakpoints
    - 单步执行 Step In/Over
    - CallStack 调用栈检查
    - Call Hierarchy 分析"]
    
    M2 --> M3["里程碑 M3: 复杂工程自动化 (Q2)
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

### 阶段一：深阶在线调试能力 (Q1)
- **断点控制**：实现 `set_breakpoint`、`clear_breakpoint`、`toggle_breakpoint`。
- **调用栈追踪**：实现 `get_call_stack`、`get_current_line`。
- **复杂结构体深度反序列化**：支持深层嵌套 STRUCT、ARRAY、UNION 的递归结构化提取。

### 阶段二：工程构建与依赖管理 (Q2)
- **自动添加/解析设备拓扑**：动态插入 SoftMotion 虚拟轴、CanOpen 从站、EtherCAT 伺服驱动器。
- **库文件自动管理**：检测缺失库依赖并静默从中央仓库下载安装。

### 阶段三：云原生与协同平台 (Q3-Q4)
- **容器化 SoftPLC 矩阵**：在 Docker / Hyper-V 容器集群内自动化调度测试任务。
- **AI 智能代码审查器**：自动分析 IEC 61131-3 代码圈复杂度、非确定性循环风险及指针越界隐患。
