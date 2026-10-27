# CODESYS Native C# Stdio MCP Plugin (`codesys-plugin`)

本项目通过 **C# / .NET 进程内原生组件 (`CodesysMcp.dll`) + 纯 Stdio 命令行桥接器 (`CodesysMcpCli.exe`)**，为 CODESYS V3.5 提供零 HTTP 端口、零磁盘文件轮询的原生 **MCP (Model Context Protocol)** 服务。

---

## 📁 目录结构

```text
codesys-plugin/
├── bin/Release/
│   ├── CodesysMcp.dll              # 注入 CODESYS 进程内部的 C# 核心服务程序集
│   └── CodesysMcpCli.exe           # 纯 Stdio 协议桥接器（供 Claude / Cursor / AI 客户端直接调用）
├── CodesysMcp/                     # C# 进程内插件源码
│   ├── CodesysContext.cs           # CODESYS 上下文与 UI 主线程安全调度 (SynchronizationContext)
│   ├── CodesysOperations.cs        # 工程、POU、编译操作封装（基于 IExtendedObject.Extensions 强类型接口）
│   ├── McpServer.cs                # Windows 命名管道 (\\.\pipe\codesys_mcp_pipe) JSON-RPC 2.0 服务端
│   └── ServerBootstrap.cs          # 供 IronPython 调用的静态启动入口
├── CodesysMcpCli/
│   └── Program.cs                  # 标准输入输出 (stdin/stdout) 到命名管道的无缓冲转发器
├── codesys_mcp_session.py          # CODESYS 启动挂载脚本（自动识别 GUI IDE 模式与无头 --noUI 模式）
├── start_codesys_ide_with_mcp.bat  # 一键启动 CODESYS IDE (带界面) 并自动挂载 MCP 插件
├── run_codesys_mcp.ps1             # PowerShell 启动器（支持 GUI IDE 模式与 -NoUI 后台模式）
├── launch_mcp.py                   # 后台无头模式守护启动脚本（带命名管道就绪探测）
├── build.ps1                       # Roslyn 一键编译脚本
└── test_cli_stdio.py               # 纯 Stdio 全链路自动化回归测试脚本
```

---

## 🖥️ 如何安装与挂载到 CODESYS IDE 里面？

CODESYS 的第三方原生插件如果走官方 `.package` 方式，需要向 3S-Smart Software Solutions 申请商业插件签名证书（否则会被 `ComponentManager` 拦截）。本项目采用 **CLR 原生注入 (`clr.AddReferenceToFileAndPath`) + 后台命名管道线程 + UI `SynchronizationContext` 线程安全派发** 方案，无需签名即可在 CODESYS IDE 内部原生运行。

你有 **3 种方式** 将其安装/挂载到 **CODESYS IDE** 中：

### 方式一：一键启动带插件的 CODESYS IDE（推荐，最省心）
直接双击运行本目录下的 [`start_codesys_ide_with_mcp.bat`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/start_codesys_ide_with_mcp.bat)，或修改你桌面上的 CODESYS 快捷方式“目标(Target)”为：
```cmd
"C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe" --profile="CODESYS V3.5 SP20 Patch 5" --runscript="C:\Users\vboxuser\Desktop\Repos\codesys-plugin\codesys_mcp_session.py"
```
- **效果**：CODESYS IDE 正常打开图形界面，启动完成后自动将 `CodesysMcp.dll` 挂载到 IDE 后台，并在底部的“消息(Messages)”窗口输出：
  `[CODESYS MCP] Native Stdio/Pipe MCP Server started on \\.\pipe\codesys_mcp_pipe`
- 挂载脚本识别到 `system.ui_present == True` 后会**立即退出脚本前台占用**，C# 命名管道服务在 IDE 后台线程持续监听，**完全不阻塞你在 IDE 中的任何正常操作**。

### 方式二：在已打开的 CODESYS IDE 中手动加载
如果你已经打开了 CODESYS IDE 正在编写工程，无需重启 IDE：
1. 在 CODESYS 顶部菜单栏点击 **`Tools` (工具) -> `Scripting` (脚本) -> `Execute Script File...` (执行脚本文件...)**。
2. 选择 `C:\Users\vboxuser\Desktop\Repos\codesys-plugin\codesys_mcp_session.py`。
3. 后台命名管道立即激活，AI 客户端即可实时读写你当前在 IDE 中打开的工程。

### 方式三：固定到 CODESYS IDE 顶部工具栏 / 脚本目录（做成 IDE 按钮）
如果你想在 CODESYS IDE 界面里拥有一个专属启动按钮：
1. 打开 CODESYS IDE，点击菜单 **`Tools` (工具) -> `Customize...` (自定义...)**。
2. 切换到 **`Toolbars` (工具栏)** 或 **`Menu` (菜单)** 选项卡，启用 **`Scripting` (脚本)** 工具栏，或者添加 **`Execute Script File`** 命令按钮并指向 `codesys_mcp_session.py`。
3. 之后在 IDE 工具栏点一下图标即可启动 MCP 服务。

---

## 🔌 配置 AI 客户端（纯 Stdio 模式，无需任何 HTTP Server）

无论你使用的是 **Claude Desktop**、**Cursor**、**Windsurf** 还是其他 MCP 客户端，全部统一通过 **Stdio** 调用 `CodesysMcpCli.exe`，无需开启任何端口：

```json
{
  "mcpServers": {
    "codesys": {
      "command": "C:\\Users\\vboxuser\\Desktop\\Repos\\codesys-plugin\\bin\\Release\\CodesysMcpCli.exe",
      "args": []
    }
  }
}
```

---

## 🛠️ 支持的 MCP 工具列表 (`tools/list`)

| 工具名称 | 参数说明 | 功能描述 |
| :--- | :--- | :--- |
| `get_project_info` | 无 | 获取当前打开工程的名称、路径、修改状态 (`isDirty`)、活动应用名称 |
| `open_project` | `path: string` | 在 CODESYS 中打开指定的 `.project` 工程文件 |
| `close_project` | 无 | 关闭当前打开的工程 |
| `save_project` | 无 | 保存当前打开的工程 |
| `get_project_tree` | `full?: bool` | 递归获取工程对象树（所有设备、应用、POU、GVL、任务及是否有声明/实现代码） |
| `read_pou` | `name: string` | 读取指定 POU 的声明区 (`VAR...END_VAR`) 和实现区 (`ST` 代码) |
| `write_pou` | `name`, `declaration`, `implementation`, `type` | 创建新 POU 或更新已有 POU 的声明区和实现区代码（支持 `Program`、`FunctionBlock`、`Function`） |
| `delete_pou` | `name: string` | 从工程中删除指定的 POU 对象 |
| `compile_project` | `clean?: bool`, `rebuild?: bool` | 编译当前工程并直接返回所有编译报错、警告明细 |
| `get_compiler_messages` | 无 | 不重新触发编译，直接读取当前编译器消息列表 |

---

## 🧪 编译与自测命令

1. **重新编译 C# 插件与 Stdio CLI**：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Users\vboxuser\Desktop\Repos\codesys-plugin\build.ps1
   ```

2. **运行无头模式后台守护进程（若未打开 IDE）**：
   ```powershell
   python C:\Users\vboxuser\Desktop\Repos\codesys-plugin\launch_mcp.py
   ```

3. **运行纯 Stdio 全流程测试（模拟 AI 客户端通过 stdin/stdout 读写 POU 并编译）**：
   ```powershell
   python C:\Users\vboxuser\Desktop\Repos\codesys-plugin\test_cli_stdio.py
   ```
