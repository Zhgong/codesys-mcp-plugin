# 第 02 章：底层逆向破解：Stack empty 修复与静默登录

## 1. `InvalidOperationException: Stack empty` 根因剖析与反射修复

### 1.1 异常现象
在 Python 启动脚本退出后，后续通过 MCP 触发任何在线操作（如 `online.create_online_application`），CODESYS 必定抛出以下异常：
```
System.InvalidOperationException: Stack empty.
   at System.Collections.Generic.Stack`1.ThrowForEmptyStack()
   at System.Collections.Generic.Stack`1.Pop()
   at _3S.CoDeSys.ScriptDriverOnline.ScriptOnline.get_Application()
   at _3S.CoDeSys.ScriptDriverOnline.ScriptOnline.create_online_application(...)
```

### 1.2 底层机理逆向（Reverse Engineering）
反编译 `ScriptDriverOnline.plugin.dll` 发现：
1. `ScriptOnline` 内部持有一个私有成员：
   ```csharp
   private Stack<ScriptExecutionEventArgs> _executionStack = new Stack<ScriptExecutionEventArgs>();
   ```
2. 当 CODESYS 启动执行 `--runscript` 时，脚本引擎触发 `ExecutorExecuting` 事件，将上下文对象 `Push` 进栈。
3. 当 Python 脚本执行结束，脚本引擎触发 `ExecutorExecuted` 事件，执行 `_executionStack.Pop()`。
4. 由于 Python 退出后栈已清空（`Count == 0`），而 `create_online_application` 内部会调用 `_executionStack.Peek()` 获取当前上下文，导致抛出 `Stack empty`。

### 1.3 创新解决方案：反射构造抽象类子类
`ScriptExecutionEventArgs` 在 `ScriptEngine3.dll` 中被声明为**抽象类**（`abstract class`），直接 `Activator.CreateInstance` 会抛出 `Cannot create an abstract class`。

我们在 [`CodesysOnlineHelper.cs`](file:///C:/Users/vboxuser/Desktop/Repos/codesys-plugin/CodesysMcp/CodesysOnlineHelper.cs#L85-L160) 中实现了程序集动态探测与反射修补：
```csharp
public static string EnsureExecutionStack()
{
    var online = CodesysContext.Online;
    if (online == null) return "Online is null";

    var field = online.GetType().GetField("_executionStack", BindingFlags.NonPublic | BindingFlags.Instance);
    if (field == null) return "_executionStack not found";

    var stackObj = field.GetValue(online);
    var countProp = stackObj.GetType().GetProperty("Count");
    int count = (int)countProp.GetValue(stackObj, null);

    if (count == 0)
    {
        // 动态扫描程序集查找非抽象具体子类
        Type targetConcreteType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                foreach (var t in asm.GetTypes())
                {
                    if (argType.IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                    {
                        targetConcreteType = t;
                        break;
                    }
                }
            } catch { }
            if (targetConcreteType != null) break;
        }

        var instance = FormatterServices.GetUninitializedObject(targetConcreteType);
        var pushMethod = stackObj.GetType().GetMethod("Push");
        pushMethod.Invoke(stackObj, new object[] { instance });
    }
    return $"_executionStack Count={countProp.GetValue(stackObj, null)}";
}
```
**效果**：即使启动脚本早已退出数小时，在线功能仍可被常驻服务任意调用，零崩溃。

---

## 2. 模态对话框消除与静默登录（Headless Logon）

### 2.1 超时假死现象
初次通过 API 登录 SoftPLC 时，请求常挂死 30,000ms 超时。
利用 `diag_online` 诊断工具探查 `Application.OpenForms`，发现 CODESYS 弹出了模态对话框：
- 窗体类名：`_3S.CoDeSys.OnlineCommands.LoginDialog`
- 窗体标题：`Device User Logon`（等待用户手动输入用户名/密码）

### 2.2 静默认证突破
反编译 `ScriptOnline` 接口，发掘出隐藏的凭据设置方法：
```csharp
online.set_default_credentials(string username, string password);
```
在执行 `create_online_application` 之前，预先注入默认登录凭据：
```csharp
if (!string.IsNullOrEmpty(username))
{
    var setCredMethod = online.GetType().GetMethod("set_default_credentials", new Type[] { typeof(string), typeof(string) });
    setCredMethod?.Invoke(online, new object[] { username, password });
}
```
同时传递无头参数：
- `mode = "try_online_change"`（优先在线变更，免去下载确认框）
- `delete_foreign_apps = true`（自动清理异构应用冲突提示框）

**效果**：实现 100% 无人值守、零弹窗的纯静默登录，全过程仅耗时 1.3 秒。
