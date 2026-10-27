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
        private static Thread _primaryThread;
        private static SynchronizationContext _syncContext;

        public static bool IsInitialized => Projects != null && System != null;

        public static void Initialize(object projectsObj, object systemObj)
        {
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
                throw new InvalidOperationException("CodesysContext is not initialized with System instance.");
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
    }
}
