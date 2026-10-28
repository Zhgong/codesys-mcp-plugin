using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using _3S.CoDeSys.ScriptEngine;
using _3S.CoDeSys.ScriptEngine.BasicFunctionality;

namespace CodesysMcp
{
    public static class CodesysContext
    {
        public static IScriptProjects Projects { get; private set; }
        public static ISystem System { get; private set; }
        private static object _rawSystem;
        private static Thread _primaryThread;
        private static SynchronizationContext _syncContext;

        public static bool HasSystem => _rawSystem != null;
        public static bool IsInitialized => Projects != null && System != null;

        public static void Initialize(object projectsObj, object systemObj)
        {
            _rawSystem = systemObj;
            Projects = (IScriptProjects)projectsObj;
            System = (ISystem)systemObj;
            _primaryThread = Thread.CurrentThread;
            _syncContext = SynchronizationContext.Current;
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

        public static T RunOnPrimaryThread<T>(Func<T> func)
        {
            if (System == null)
            {
                return func();
            }

            // If running in interactive CODESYS IDE (UI mode) and we have the UI SynchronizationContext,
            // marshal calls from background Named Pipe threads onto the main UI thread.
            if (System.ui_present && _syncContext != null && Thread.CurrentThread != _primaryThread)
            {
                T result = default(T);
                Exception capturedException = null;
                _syncContext.Send(_ =>
                {
                    try
                    {
                        result = func();
                    }
                    catch (Exception ex)
                    {
                        capturedException = ex;
                    }
                }, null);

                if (capturedException != null)
                {
                    ExceptionDispatchInfo.Capture(capturedException).Throw();
                }
                return result;
            }

            return func();
        }

        public static void RunOnPrimaryThread(Action action)
        {
            RunOnPrimaryThread<object>(() =>
            {
                action();
                return null;
            });
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
