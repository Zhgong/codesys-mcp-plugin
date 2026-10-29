using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using _3S.CoDeSys.Core;
using _3S.CoDeSys.ScriptEngine;
using _3S.CoDeSys.ScriptEngine.BasicFunctionality;

namespace CodesysMcp
{
    public static class CodesysContext
    {
        public static IScriptProjects Projects { get; private set; }
        public static ISystem System { get; private set; }
        public static object Online { get; private set; }
        public static object RawSystem => _rawSystem;
        private static object _rawSystem;
        private static Thread _primaryThread;
        private static SynchronizationContext _syncContext;

        public static bool HasSystem => _rawSystem != null;
        public static bool HasOnline => Online != null;
        public static bool IsInitialized => Projects != null && System != null;

        public static void Initialize(object projectsObj, object systemObj, object onlineObj = null)
        {
            _rawSystem = systemObj;
            Projects = (IScriptProjects)projectsObj;
            System = (ISystem)systemObj;
            Online = onlineObj;
            _primaryThread = Thread.CurrentThread;
            _syncContext = SynchronizationContext.Current;
            CodesysOnlineHelper.EnsureExecutionStack();
        }

        public static IExtendedObject<IScriptProject> PrimaryProjectExtended
        {
            get
            {
                if (Projects == null) return null;
                return Projects.primary;
            }
        }

        public static IScriptProject PrimaryProject
        {
            get
            {
                var prim = PrimaryProjectExtended;
                if (prim == null) return null;
                return prim.BaseObject;
            }
        }

        private static readonly object _queueLock = new object();
        private static readonly System.Collections.Generic.Queue<Action> _workQueue = new System.Collections.Generic.Queue<Action>();

        public static void ProcessQueue(int maxItems = 20)
        {
            _primaryThread = Thread.CurrentThread;
            for (int i = 0; i < maxItems; i++)
            {
                Action act = null;
                lock (_queueLock)
                {
                    if (_workQueue.Count > 0)
                    {
                        act = _workQueue.Dequeue();
                    }
                }
                if (act == null) break;
                try
                {
                    act();
                }
                catch { }
            }
        }

        public static int QueueCount
        {
            get
            {
                lock (_queueLock) { return _workQueue.Count; }
            }
        }

        private static Control TryGetMainWinFormsControl()
        {
            try
            {
                var openForms = Application.OpenForms;
                if (openForms != null && openForms.Count > 0)
                {
                    for (int i = 0; i < openForms.Count; i++)
                    {
                        var f = openForms[i];
                        if (f != null && !f.IsDisposed && f.IsHandleCreated)
                        {
                            return f;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (SystemInstances.FrameForm is Control ctrl && !ctrl.IsDisposed && ctrl.IsHandleCreated)
                {
                    return ctrl;
                }
            }
            catch { }

            return null;
        }

        public static T RunOnPrimaryThread<T>(Func<T> func, int timeoutMs = 30000)
        {
            if (System == null || Thread.CurrentThread == _primaryThread)
            {
                return func();
            }

            // Check if already on the main WinForms UI thread
            var mainCtrl = TryGetMainWinFormsControl();
            if (mainCtrl != null && !mainCtrl.InvokeRequired)
            {
                _primaryThread = Thread.CurrentThread;
                return func();
            }

            try
            {
                if (SystemInstances.FrameForm != null && !SystemInstances.FrameForm.InvokeRequired)
                {
                    _primaryThread = Thread.CurrentThread;
                    return func();
                }
            }
            catch { }

            T result = default(T);
            Exception capturedException = null;
            using (var done = new ManualResetEvent(false))
            {
                Action wrapper = () =>
                {
                    _primaryThread = Thread.CurrentThread;
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                    finally
                    {
                        try { done.Set(); } catch { }
                    }
                };

                bool dispatched = false;

                // 1. Primary Native C# UI Dispatch: WinForms Control.BeginInvoke on Main IDE Window
                if (mainCtrl != null)
                {
                    try
                    {
                        mainCtrl.BeginInvoke(wrapper);
                        dispatched = true;
                    }
                    catch { }
                }

                // 2. Secondary Native C# UI Dispatch: SystemInstances.FrameForm.Invoke or Engine.InvokeInPrimaryThread
                if (!dispatched)
                {
                    try
                    {
                        var frameForm = SystemInstances.FrameForm;
                        if (frameForm != null)
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    frameForm.Invoke(wrapper, null);
                                }
                                catch (Exception ex)
                                {
                                    capturedException = ex;
                                    try { done.Set(); } catch { }
                                }
                            });
                            dispatched = true;
                        }
                    }
                    catch { }
                }

                if (!dispatched)
                {
                    try
                    {
                        var engine = SystemInstances.Engine;
                        if (engine != null && System != null && System.ui_present)
                        {
                            ThreadPool.QueueUserWorkItem(_ =>
                            {
                                try
                                {
                                    engine.InvokeInPrimaryThread(wrapper, null, false);
                                }
                                catch (Exception ex)
                                {
                                    capturedException = ex;
                                    try { done.Set(); } catch { }
                                }
                            });
                            dispatched = true;
                        }
                    }
                    catch { }
                }

                // 3. Fallback for --noUI headless mode (or early startup before FrameForm handle is created)
                if (!dispatched)
                {
                    lock (_queueLock)
                    {
                        _workQueue.Enqueue(wrapper);
                    }

                    // While waiting, if the main window finishes initializing (e.g. right after --runscript exits),
                    // also wake up and dispatch via mainCtrl so requests never get stuck in _workQueue!
                    int waited = 0;
                    const int stepMs = 100;
                    while (waited < timeoutMs)
                    {
                        if (done.WaitOne(stepMs))
                        {
                            break;
                        }
                        waited += stepMs;

                        var lateCtrl = TryGetMainWinFormsControl();
                        if (lateCtrl != null)
                        {
                            Action dequeued = null;
                            lock (_queueLock)
                            {
                                if (_workQueue.Count > 0)
                                {
                                    dequeued = _workQueue.Dequeue();
                                }
                            }
                            if (dequeued != null)
                            {
                                try
                                {
                                    lateCtrl.BeginInvoke(dequeued);
                                    if (done.WaitOne(Math.Max(1000, timeoutMs - waited)))
                                    {
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }
                    }

                    if (!done.WaitOne(0))
                    {
                        throw new TimeoutException($"Primary thread dispatch timed out after {timeoutMs}ms (UI Present: {System?.ui_present}, Pending Queue: {QueueCount}).");
                    }
                }
                else
                {
                    if (!done.WaitOne(timeoutMs))
                    {
                        throw new TimeoutException($"Primary UI thread Invoke timed out after {timeoutMs}ms.");
                    }
                }
            }

            if (capturedException != null)
            {
                ExceptionDispatchInfo.Capture(capturedException).Throw();
            }
            return result;
        }

        public static void RunOnPrimaryThread(Action action, int timeoutMs = 30000)
        {
            RunOnPrimaryThread<object>(() =>
            {
                action();
                return null;
            }, timeoutMs);
        }

        private static System.Reflection.MethodInfo _writeMessageMethod;
        private static object _infoSeverityObj;

        public static void LogToIde(string message, int severity = 8)
        {
            if (!HasSystem) return;
            try
            {
                RunOnPrimaryThread(() =>
                {
                    try
                    {
                        if (_writeMessageMethod == null && _rawSystem != null)
                        {
                            foreach (var m in _rawSystem.GetType().GetMethods())
                            {
                                if (m.Name == "write_message" && m.GetParameters().Length == 5)
                                {
                                    _writeMessageMethod = m;
                                    var sevType = m.GetParameters()[0].ParameterType;
                                    try
                                    {
                                        _infoSeverityObj = Enum.ToObject(sevType, (uint)severity);
                                    }
                                    catch
                                    {
                                        _infoSeverityObj = Enum.ToObject(sevType, severity);
                                    }
                                    break;
                                }
                            }
                        }

                        if (_writeMessageMethod != null)
                        {
                            var args = new object[] { _infoSeverityObj, "[CodesysMcp] " + message, null, 0L, (short)0 };
                            _writeMessageMethod.Invoke(_rawSystem, args);
                        }
                    }
                    catch
                    {
                    }
                });
            }
            catch { }
        }

        public static System.Collections.Generic.List<string> GetIdeMessages(string category = null)
        {
            var list = new System.Collections.Generic.List<string>();
            if (!HasSystem) return list;
            return RunOnPrimaryThread(() =>
            {
                try
                {
                    string catGuid = string.IsNullOrEmpty(category) || category.IndexOf("script", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "{194B48A9-AB51-43ae-B9A9-51D3EDAADDF3}"
                        : category;

                    var m = _rawSystem.GetType().GetMethod("get_messages", new Type[] { typeof(string) });
                    if (m != null)
                    {
                        var res = m.Invoke(_rawSystem, new object[] { catGuid }) as System.Collections.IEnumerable;
                        if (res != null)
                        {
                            foreach (var item in res)
                            {
                                if (item != null) list.Add(item.ToString());
                            }
                        }
                    }
                }
                catch { }
                return list;
            });
        }
    }
}
