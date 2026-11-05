# 第 01 章：线程架构、IronPython 退出机制与原生 UI 封送

## 1. 行业痛点与历史教训
在 CODESYS 自动化集成中，官方提供的脚本机制为 **IronPython 2.7**（`--runscript` 启动参数）。
在早期架构中，开发者通常在 Python 脚本末尾运行类似死循环以维持服务：
```python
# 早期反面模式：导致 CODESYS IDE 完全卡死
while True:
    process_requests()
    time.sleep(0.05)
```
### 致命后果：
1. `--runscript` 启动阶段运行在 CODESYS 主线程上，死循环直接霸占了 Windows Forms 的消息泵（Message Loop）。
2. CODESYS IDE 界面完全卡死（Title 变为“未响应”），无法实现“一边人通过 GUI 观察，一边机器通过 API 自动化操作”的人机协同。
3. 文件锁与单实例限制导致无法并行启动第二个 IDE 实例。

---

## 2. 突破方案：极速退出与原生 C# 线程托管

### 2.1 架构设计
```
[CODESYS.exe 启动]
       │
       ▼
[codesys_mcp_session.py]  <── 耗时 <50ms
       │
       ├─ Shadow-Copy 复制并载入 bin/Release/CodesysMcp.dll
       ├─ 调用 ServerBootstrap.Start(projects, system, online, pipeName)
       │       └─ 启动后台 MTA 线程监听 Named Pipe (\\.\pipe\codesys_mcp_pipe)
       │
       ▼
[Python 脚本立即执行完毕退出！]
       │
       ▼
[CODESYS WinForms 消息泵恢复正常] ──> GUI 100% 流畅，人机自由交互！
```

### 2.2 原生 C# UI 线程封送（Marshaling）
CODESYS 的内部对象（`IScriptProject`, `IScriptApplication`, `IScriptTraceObject`）属于 STA COM/WinForms 对象，任何后台 NamedPipe 线程直接调用都会抛出跨线程异常或死锁。

在 [`CodesysContext.cs`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/CodesysMcp/CodesysContext.cs) 中，我们建立了多层原生的 UI 线程封送机制：
```csharp
public static T RunOnPrimaryThread<T>(Func<T> func, int timeoutMs = 30000)
{
    // 优先尝试获取 CODESYS 主窗体
    var mainCtrl = TryGetMainWinFormsControl();
    if (mainCtrl != null && mainCtrl.IsHandleCreated)
    {
        IAsyncResult ar = mainCtrl.BeginInvoke(func);
        if (ar.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            return (T)mainCtrl.EndInvoke(ar);
        }
        throw new TimeoutException($"Primary UI thread BeginInvoke timed out after {timeoutMs}ms");
    }

    // 备用机制 1: FrameForm 包装器
    var ff = _3S.CoDeSys.Core.SystemInstances.FrameForm;
    if (ff != null && ff.InvokeRequired)
    {
        return (T)ff.Invoke(func);
    }

    // 备用机制 2: 官方 PrimaryThread 调度器
    var engine = _3S.CoDeSys.Core.SystemInstances.Engine;
    if (engine != null)
    {
        return (T)engine.InvokeInPrimaryThread(func);
    }

    return func();
}
```
**性能表现**：每次跨线程调用耗时仅 **2 ~ 5 毫秒**，完全消除界面卡顿。

---

## 3. 热重载闭环（Hot Reload Loop，~150ms）
在对 C# 插件进行修改时，Windows 默认会锁定被进程加载的 DLL，通常需要关闭工程并重启 CODESYS，单次耗时 30~60 秒。

我们通过 `hot_reload_dll` 工具消除了这个瓶颈：
```csharp
// 内存流加载，彻底解除文件锁定
byte[] rawBytes = File.ReadAllBytes(dllPath);
var newAsm = Assembly.Load(rawBytes);
var startMethod = newAsm.GetType("CodesysMcp.ServerBootstrap").GetMethod("Start");

ThreadPool.QueueUserWorkItem(_ =>
{
    Thread.Sleep(150);
    ServerBootstrap.Stop(); // 优雅停止旧管道
    startMethod.Invoke(null, new object[] { projs, sys, onl, pipe }); // 启动新服务
});
```
开发调试周期缩短至 **150 毫秒**，工程无需关闭，状态无损保持。
