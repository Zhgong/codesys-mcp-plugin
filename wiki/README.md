# CODESYS MCP Engineering Wiki (内部工程知识库)

本知识库系统化总结了在 CODESYS 自动化软件中构建高性能、高可用 RESTful/MCP 协议接入层的核心工程实践与底层逆向成果。

---

## 知识库目录结构

| 章节 | 文档名 | 核心主题 |
| :--- | :--- | :--- |
| **第 00 章** | [`00_Product_Roadmap_2026.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/00_Product_Roadmap_2026.md) | 产品研发路线图与商业演进计划（断点调试、设备拓扑、集群测试） |
| **第 01 章** | [`01_Architecture_and_WinForms_Marshaling.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/01_Architecture_and_WinForms_Marshaling.md) | 线程架构、IronPython 快速退出机制、原生 WinForms UI 封送与热重载 |
| **第 02 章** | [`02_Codesys_Internals_and_Reflection.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/02_Codesys_Internals_and_Reflection.md) | 底层逆向破解：`Stack empty` 崩溃修复、反射构造与静默无弹窗登录 |
| **第 03 章** | [`03_IEC_Data_Types_and_Trace_Automation.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/03_IEC_Data_Types_and_Trace_Automation.md) | IEC 61131-3 数据类型归一化比对与动态 Trace 图表对象注入 |
| **第 04 章** | [`04_Testing_Strategy_and_Fast_Loop.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/04_Testing_Strategy_and_Fast_Loop.md) | 双循环测试体系：2.8 秒极速 SoftPLC 在线验证与 52 项核心单元测试 |
| **第 05 章** | [`05_Dual_Repo_and_Gatekeeper_Governance.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/05_Dual_Repo_and_Gatekeeper_Governance.md) | 双仓库治理与安全防泄漏门禁（Gatekeeper Hook & `.publicignore`） |
| **第 06 章** | [`06_Recipe_and_Complex_Data_Engine.md`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/wiki/06_Recipe_and_Complex_Data_Engine.md) | 配方与复杂数据引擎（两阶段原子提交、结构体/数组探测、三层测试矩阵） |

---

> [!NOTE]
> 本目录受 [`.public_gate_rules.json`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/.public_gate_rules.json) 安全门禁保护，永久专属于私有开发仓库（`Zhgong/codesys-mcp-plugin-private`），禁止推送到公开开源仓库。
