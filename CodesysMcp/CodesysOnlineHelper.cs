using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace CodesysMcp
{
    public class OnlineStatusResult
    {
        public bool isLoggedIn { get; set; }
        public string applicationState { get; set; } = "Unknown";
        public string operationState { get; set; } = "Unknown";
        public string activeApplication { get; set; }
        public List<string> forcedExpressions { get; set; } = new List<string>();
        public string message { get; set; }
    }

    public class ReadVariablesResult
    {
        public bool success { get; set; }
        public Dictionary<string, string> values { get; set; } = new Dictionary<string, string>();
        public string message { get; set; }
    }

    public class WriteVariableResult
    {
        public bool success { get; set; }
        public string name { get; set; }
        public string value { get; set; }
        public bool isForced { get; set; }
        public string message { get; set; }
    }

    public static class CodesysOnlineHelper
    {
        private static object _cachedOnlineApp;

        public static void ClearSession()
        {
            try
            {
                if (_cachedOnlineApp != null)
                {
                    var logoutMethod = _cachedOnlineApp.GetType().GetMethod("logout");
                    logoutMethod?.Invoke(_cachedOnlineApp, null);

                    var disp = _cachedOnlineApp as IDisposable;
                    disp?.Dispose();
                }
            }
            catch { }
            finally
            {
                _cachedOnlineApp = null;
            }
        }

        private static object GetOrCreateOnlineApp(string appName = null)
        {
            if (_cachedOnlineApp != null)
            {
                try
                {
                    var isLoggedProp = _cachedOnlineApp.GetType().GetProperty("is_logged_in");
                    bool loggedIn = Convert.ToBoolean(isLoggedProp?.GetValue(_cachedOnlineApp, null));
                    if (loggedIn)
                    {
                        return _cachedOnlineApp;
                    }
                }
                catch
                {
                    _cachedOnlineApp = null;
                }
            }

            if (CodesysContext.Online == null)
            {
                throw new InvalidOperationException("CODESYS Online engine is not available. Please ensure online module is passed during session startup.");
            }

            var proj = CodesysContext.PrimaryProject;
            if (proj == null)
            {
                throw new InvalidOperationException("No active CODESYS project is currently open.");
            }

            object appObj = null;
            if (!string.IsNullOrEmpty(appName))
            {
                var appExt = CodesysOperations.FindObjectByName(appName);
                if (appExt != null)
                {
                    appObj = CodesysOperations.GetExtension<_3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication>(appExt);
                }
            }

            if (appObj == null)
            {
                var activeAppExt = proj.active_application;
                if (activeAppExt != null)
                {
                    appObj = CodesysOperations.GetExtension<_3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication>(activeAppExt);
                }
            }

            var createOnlineAppMethod = CodesysContext.Online.GetType().GetMethod("create_online_application", new Type[] { appObj?.GetType() ?? typeof(object) })
                ?? CodesysContext.Online.GetType().GetMethod("create_online_application");

            if (createOnlineAppMethod == null)
            {
                foreach (var m in CodesysContext.Online.GetType().GetMethods())
                {
                    if (m.Name == "create_online_application")
                    {
                        createOnlineAppMethod = m;
                        break;
                    }
                }
            }

            if (createOnlineAppMethod == null)
            {
                throw new InvalidOperationException("Method 'create_online_application' not found on CODESYS online object.");
            }

            var pCount = createOnlineAppMethod.GetParameters().Length;
            object[] callArgs = pCount == 1 ? new object[] { appObj } : new object[0];

            _cachedOnlineApp = createOnlineAppMethod.Invoke(CodesysContext.Online, callArgs);
            return _cachedOnlineApp;
        }

        public static OnlineStatusResult GetOnlineStatus()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var res = new OnlineStatusResult();
                if (_cachedOnlineApp == null)
                {
                    res.isLoggedIn = false;
                    res.applicationState = "Offline";
                    res.message = "Not logged in to any PLC or SoftPLC instance.";
                    return res;
                }

                try
                {
                    var isLoggedProp = _cachedOnlineApp.GetType().GetProperty("is_logged_in");
                    res.isLoggedIn = Convert.ToBoolean(isLoggedProp?.GetValue(_cachedOnlineApp, null));

                    var appStateProp = _cachedOnlineApp.GetType().GetProperty("application_state");
                    res.applicationState = appStateProp?.GetValue(_cachedOnlineApp, null)?.ToString() ?? "Unknown";

                    var opStateProp = _cachedOnlineApp.GetType().GetProperty("operation_state");
                    res.operationState = opStateProp?.GetValue(_cachedOnlineApp, null)?.ToString() ?? "Unknown";

                    var forcedProp = _cachedOnlineApp.GetType().GetMethod("get_forced_expressions");
                    if (forcedProp != null)
                    {
                        var forcedList = forcedProp.Invoke(_cachedOnlineApp, null) as IEnumerable;
                        if (forcedList != null)
                        {
                            foreach (var item in forcedList)
                            {
                                if (item != null) res.forcedExpressions.Add(item.ToString());
                            }
                        }
                    }

                    res.message = res.isLoggedIn
                        ? $"Connected to PLC (State: {res.applicationState}, Operation: {res.operationState})."
                        : "Online application session is disconnected.";
                }
                catch (Exception ex)
                {
                    res.isLoggedIn = false;
                    res.message = "Failed to query PLC state: " + ex.Message;
                }

                return res;
            });
        }

        public static OnlineStatusResult PlcLogin(string mode = "try_online_change", string appName = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var onlineApp = GetOrCreateOnlineApp(appName);
                var loginMethod = onlineApp.GetType().GetMethod("login");
                if (loginMethod == null)
                {
                    throw new InvalidOperationException("Method 'login' not found on online application.");
                }

                var param0Type = loginMethod.GetParameters()[0].ParameterType;

                // OnlineChangeOption: Never = 0, Try = 1, Force = 2, Keep = 3
                int optionVal = 1; // Default to Try
                if (string.Equals(mode, "login_only", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mode, "never", StringComparison.OrdinalIgnoreCase))
                {
                    optionVal = 0;
                }
                else if (string.Equals(mode, "force", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(mode, "download", StringComparison.OrdinalIgnoreCase))
                {
                    optionVal = 2;
                }

                object changeOptionObj = Enum.ToObject(param0Type, optionVal);
                object[] loginArgs = new object[] { changeOptionObj, false };

                try
                {
                    loginMethod.Invoke(onlineApp, loginArgs);
                    CodesysContext.LogToIde($"plc_login: Logged in successfully with mode '{mode}'");
                }
                catch (TargetInvocationException ex)
                {
                    throw ex.InnerException ?? ex;
                }

                return GetOnlineStatus();
            });
        }

        public static OnlineStatusResult PlcLogout()
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                ClearSession();
                CodesysContext.LogToIde("plc_logout: Disconnected and logged out from PLC");
                return GetOnlineStatus();
            });
        }

        public static OnlineStatusResult PlcControl(string action)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var onlineApp = GetOrCreateOnlineApp();
                if (string.IsNullOrEmpty(action))
                    throw new ArgumentException("Parameter 'action' is required (e.g. 'start', 'stop', 'reset').");

                string act = action.Trim().ToLowerInvariant();
                if (act == "start")
                {
                    var startMethod = onlineApp.GetType().GetMethod("start");
                    startMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde("plc_control: Started PLC execution (RUN)");
                }
                else if (act == "stop")
                {
                    var stopMethod = onlineApp.GetType().GetMethod("stop");
                    stopMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde("plc_control: Stopped PLC execution (STOP)");
                }
                else if (act == "reset")
                {
                    var resetMethod = onlineApp.GetType().GetMethod("reset");
                    if (resetMethod != null)
                    {
                        var p = resetMethod.GetParameters();
                        if (p.Length == 0) resetMethod.Invoke(onlineApp, null);
                        else
                        {
                            object resetOpt = Enum.ToObject(p[0].ParameterType, 0);
                            resetMethod.Invoke(onlineApp, new object[] { resetOpt });
                        }
                    }
                    CodesysContext.LogToIde("plc_control: Reset PLC application");
                }
                else
                {
                    throw new NotSupportedException($"Action '{action}' is not supported. Use 'start', 'stop', or 'reset'.");
                }

                return GetOnlineStatus();
            });
        }

        public static ReadVariablesResult ReadVariables(List<string> expressions)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var res = new ReadVariablesResult();
                if (expressions == null || expressions.Count == 0)
                {
                    res.success = true;
                    res.message = "No expressions provided to read.";
                    return res;
                }

                var onlineApp = GetOrCreateOnlineApp();
                var readValMethod = onlineApp.GetType().GetMethod("read_value", new Type[] { typeof(string) });

                foreach (var expr in expressions)
                {
                    if (string.IsNullOrWhiteSpace(expr)) continue;
                    try
                    {
                        var val = readValMethod?.Invoke(onlineApp, new object[] { expr.Trim() })?.ToString();
                        res.values[expr] = val ?? string.Empty;
                    }
                    catch (Exception ex)
                    {
                        res.values[expr] = "<Error: " + (ex.InnerException?.Message ?? ex.Message) + ">";
                    }
                }

                res.success = true;
                res.message = $"Successfully read {res.values.Count} variable(s).";
                return res;
            });
        }

        public static WriteVariableResult WriteVariable(string name, string value, bool force = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                if (string.IsNullOrEmpty(name))
                    throw new ArgumentException("Parameter 'name' is required.");

                var onlineApp = GetOrCreateOnlineApp();
                var setPrep = onlineApp.GetType().GetMethod("set_prepared_value", new Type[] { typeof(string), typeof(string) });
                if (setPrep == null)
                    throw new InvalidOperationException("Method 'set_prepared_value' not found.");

                setPrep.Invoke(onlineApp, new object[] { name, value ?? string.Empty });

                if (force)
                {
                    var forceMethod = onlineApp.GetType().GetMethod("force_prepared_values");
                    forceMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"write_variable: Forced '{name}' := '{value}'");
                }
                else
                {
                    var writeMethod = onlineApp.GetType().GetMethod("write_prepared_values");
                    writeMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"write_variable: Wrote '{name}' := '{value}'");
                }

                return new WriteVariableResult
                {
                    success = true,
                    name = name,
                    value = value,
                    isForced = force,
                    message = force
                        ? $"Variable '{name}' was forced to '{value}'."
                        : $"Variable '{name}' was written with value '{value}'."
                };
            });
        }

        public static OnlineStatusResult UnforceVariables(List<string> expressions = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var onlineApp = GetOrCreateOnlineApp();
                if (expressions == null || expressions.Count == 0)
                {
                    var unforceAll = onlineApp.GetType().GetMethod("unforce_all_values");
                    unforceAll?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde("unforce_variables: Unforced all active forced expressions");
                }
                else
                {
                    var setUnforce = onlineApp.GetType().GetMethod("set_unforce_value", new Type[] { typeof(string) });
                    foreach (var expr in expressions)
                    {
                        setUnforce?.Invoke(onlineApp, new object[] { expr });
                    }
                    var forceMethod = onlineApp.GetType().GetMethod("force_prepared_values");
                    forceMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"unforce_variables: Unforced {expressions.Count} expression(s)");
                }

                return GetOnlineStatus();
            });
        }

        public static string CreateBootApplication(string appName = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var proj = CodesysContext.PrimaryProject;
                if (proj == null) throw new InvalidOperationException("No active project open.");

                _3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication app = null;
                if (!string.IsNullOrEmpty(appName))
                {
                    var ext = CodesysOperations.FindObjectByName(appName);
                    if (ext != null)
                    {
                        app = CodesysOperations.GetExtension<_3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication>(ext);
                    }
                }

                if (app == null)
                {
                    var activeAppExt = proj.active_application;
                    if (activeAppExt != null)
                    {
                        app = CodesysOperations.GetExtension<_3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication>(activeAppExt);
                    }
                }

                if (app == null) throw new InvalidOperationException("Could not resolve active application to create boot project.");

                app.create_boot_application();
                CodesysContext.LogToIde("create_boot_application: Successfully created boot project for application");
                return "Boot application created successfully.";
            });
        }
    }
}
