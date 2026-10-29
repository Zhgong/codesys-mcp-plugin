using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
        public string actualValue { get; set; }
        public bool verified { get; set; }
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

        private static object GetMemberValue(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var prop = type.GetProperty(name);
            if (prop != null)
            {
                try { return prop.GetValue(target, null); } catch { }
            }
            var method = type.GetMethod(name, Type.EmptyTypes);
            if (method != null)
            {
                try { return method.Invoke(target, null); } catch { }
            }
            return null;
        }

        public static string EnsureExecutionStack()
        {
            if (CodesysContext.Online == null) return "Online object is null";
            try
            {
                var onlineType = CodesysContext.Online.GetType();
                var stackField = onlineType.GetField("_executionStack", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (stackField == null) return "No _executionStack field on " + onlineType.FullName;

                var stackObj = stackField.GetValue(CodesysContext.Online);
                if (stackObj == null) return "_executionStack value is null";

                var countProp = stackObj.GetType().GetProperty("Count");
                int count = countProp != null ? Convert.ToInt32(countProp.GetValue(stackObj, null)) : -1;
                if (count > 0)
                {
                    try
                    {
                        var peekMethod = stackObj.GetType().GetMethod("Peek");
                        var currentTop = peekMethod?.Invoke(stackObj, null);
                        if (currentTop != null)
                        {
                            AppDomain.CurrentDomain.SetData("CodesysMcp_ExecArgs", currentTop);
                        }
                    }
                    catch { }
                    return $"_executionStack Count={count}";
                }

                if (count == 0)
                {
                    var pushMethod = stackObj.GetType().GetMethod("Push");
                    if (pushMethod != null)
                    {
                        var argType = pushMethod.GetParameters()[0].ParameterType;
                        object dummyArgs = AppDomain.CurrentDomain.GetData("CodesysMcp_ExecArgs");

                        if (dummyArgs == null)
                        {
                            // Find concrete subclass of ScriptExecutionEventArgs in loaded assemblies
                            Type concreteType = null;
                            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    foreach (var t in asm.GetTypes())
                                    {
                                        if (!t.IsAbstract && !t.IsInterface && argType.IsAssignableFrom(t))
                                        {
                                            concreteType = t;
                                            break;
                                        }
                                    }
                                    if (concreteType != null) break;
                                }
                                catch { }
                            }

                            if (concreteType != null)
                            {
                                try
                                {
                                    dummyArgs = Activator.CreateInstance(concreteType, true);
                                }
                                catch
                                {
                                    dummyArgs = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(concreteType);
                                }
                            }
                        }

                        if (dummyArgs != null)
                        {
                            AppDomain.CurrentDomain.SetData("CodesysMcp_ExecArgs", dummyArgs);
                            pushMethod.Invoke(stackObj, new object[] { dummyArgs });
                            return $"Pushed {dummyArgs.GetType().FullName} onto _executionStack (Count=1)";
                        }
                        return $"Could not find concrete type for {argType.FullName}";
                    }
                }
                return $"_executionStack Count={count}";
            }
            catch (Exception ex)
            {
                return "EnsureExecutionStack error: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        private static object GetOrCreateOnlineApp(string appName = null)
        {
            EnsureExecutionStack();

            if (_cachedOnlineApp != null)
            {
                try
                {
                    var isLoggedVal = GetMemberValue(_cachedOnlineApp, "is_logged_in");
                    bool loggedIn = isLoggedVal != null && Convert.ToBoolean(isLoggedVal);
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

            object appTarget = null;
            if (!string.IsNullOrEmpty(appName))
            {
                appTarget = CodesysOperations.FindObjectByName(appName);
                if (appTarget == null)
                {
                    throw new FileNotFoundException($"Application object '{appName}' not found in active project.");
                }
            }
            else
            {
                try
                {
                    var activeAppProp = proj.GetType().GetProperty("active_application");
                    appTarget = activeAppProp?.GetValue(proj, null);
                }
                catch { }
            }

            MethodInfo createZeroArg = null;
            MethodInfo createOneArg = null;
            foreach (var m in CodesysContext.Online.GetType().GetMethods())
            {
                if (m.Name == "create_online_application")
                {
                    int len = m.GetParameters().Length;
                    if (len == 0) createZeroArg = m;
                    else if (len == 1) createOneArg = m;
                }
            }

            try
            {
                if (appTarget != null && createOneArg != null)
                {
                    _cachedOnlineApp = createOneArg.Invoke(CodesysContext.Online, new object[] { appTarget });
                }
                else if (createZeroArg != null)
                {
                    _cachedOnlineApp = createZeroArg.Invoke(CodesysContext.Online, new object[0]);
                }
                else if (createOneArg != null)
                {
                    _cachedOnlineApp = createOneArg.Invoke(CodesysContext.Online, new object[] { appTarget });
                }
                else
                {
                    throw new InvalidOperationException("Method 'create_online_application' not found on CODESYS online object.");
                }
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
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
                    var isLoggedVal = GetMemberValue(_cachedOnlineApp, "is_logged_in");
                    res.isLoggedIn = isLoggedVal != null && Convert.ToBoolean(isLoggedVal);

                    var appStateVal = GetMemberValue(_cachedOnlineApp, "application_state");
                    res.applicationState = appStateVal?.ToString() ?? "Unknown";

                    var opStateVal = GetMemberValue(_cachedOnlineApp, "operation_state");
                    res.operationState = opStateVal?.ToString() ?? "Unknown";

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

        public static OnlineStatusResult PlcLogin(string mode = "try_online_change", bool deleteForeignApps = true, string appName = null, string username = null, string password = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                string user = !string.IsNullOrEmpty(username) ? username : Environment.GetEnvironmentVariable("CODESYS_DEVICE_USER");
                string pass = password ?? Environment.GetEnvironmentVariable("CODESYS_DEVICE_PASSWORD");
                if (!string.IsNullOrEmpty(user) && CodesysContext.Online != null)
                {
                    try
                    {
                        var setCredMethod = CodesysContext.Online.GetType().GetMethod("set_default_credentials", new Type[] { typeof(string), typeof(string) });
                        if (setCredMethod != null)
                        {
                            setCredMethod.Invoke(CodesysContext.Online, new object[] { user, pass ?? string.Empty });
                            CodesysContext.LogToIde($"[INFO] plc_login: Applied device credentials for user '{user}' via online.set_default_credentials.");
                        }
                    }
                    catch (Exception ex)
                    {
                        CodesysContext.LogToIde($"[WARNING] plc_login: set_default_credentials failed: {ex.Message}");
                    }
                }

                var onlineApp = GetOrCreateOnlineApp(appName);
                var loginMethod = onlineApp.GetType().GetMethod("login");
                if (loginMethod == null)
                {
                    throw new InvalidOperationException("Method 'login' not found on online application.");
                }

                var param0Type = loginMethod.GetParameters()[0].ParameterType;

                // OnlineChangeOption: Never = 0 (Download), Try = 1 (Online Change with Download fallback), Force = 2, Keep = 3
                int optionVal = 1; // Default to Try
                string m = (mode ?? "").Trim().ToLowerInvariant();
                if (m == "download" || m == "never")
                {
                    optionVal = 0;
                }
                else if (m == "try" || m == "try_online_change")
                {
                    optionVal = 1;
                }
                else if (m == "force" || m == "force_online_change")
                {
                    optionVal = 2;
                }
                else if (m == "keep" || m == "login_only")
                {
                    optionVal = 3;
                }

                object changeOptionObj = Enum.ToObject(param0Type, optionVal);
                object[] loginArgs = new object[] { changeOptionObj, deleteForeignApps };

                try
                {
                    CodesysContext.LogToIde($"[STEP] plc_login: Calling login({changeOptionObj}, deleteForeignApps={deleteForeignApps}) on Thread={System.Threading.Thread.CurrentThread.ManagedThreadId} (Apartment={System.Threading.Thread.CurrentThread.GetApartmentState()})...");
                    loginMethod.Invoke(onlineApp, loginArgs);
                    CodesysContext.LogToIde($"[STEP] plc_login: loginMethod.Invoke returned! Querying status...");
                    var status = GetOnlineStatus();
                    if (status.isLoggedIn)
                    {
                        CodesysContext.LogToIde($"[SUCCESS] plc_login: Connected to SoftPLC (State: {status.applicationState}, Operation: {status.operationState})");
                    }
                    else
                    {
                        CodesysContext.LogToIde($"[WARNING] plc_login: Session login returned false.");
                    }
                    return status;
                }
                catch (TargetInvocationException ex)
                {
                    var inner = ex.InnerException ?? ex;
                    CodesysContext.LogToIde($"[ERROR] plc_login failed: {inner.Message}");
                    throw inner;
                }
                catch (Exception ex)
                {
                    CodesysContext.LogToIde($"[ERROR] plc_login failed: {ex.Message}");
                    throw;
                }
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
                    var curState = GetMemberValue(onlineApp, "application_state")?.ToString() ?? "";
                    if (!string.Equals(curState, "run", StringComparison.OrdinalIgnoreCase))
                    {
                        var startMethod = onlineApp.GetType().GetMethod("start");
                        startMethod?.Invoke(onlineApp, null);
                    }
                    CodesysContext.LogToIde("plc_control: Started PLC execution (RUN)");
                }
                else if (act == "stop")
                {
                    var curState = GetMemberValue(onlineApp, "application_state")?.ToString() ?? "";
                    if (!string.Equals(curState, "stop", StringComparison.OrdinalIgnoreCase))
                    {
                        var stopMethod = onlineApp.GetType().GetMethod("stop");
                        stopMethod?.Invoke(onlineApp, null);
                    }
                    CodesysContext.LogToIde("plc_control: Stopped PLC execution (STOP)");
                }
                else if (act == "reset")
                {
                    var resetMethod = onlineApp.GetType().GetMethod("reset");
                    if (resetMethod != null)
                    {
                        var p = resetMethod.GetParameters();
                        if (p.Length == 0)
                        {
                            resetMethod.Invoke(onlineApp, null);
                        }
                        else if (p.Length == 1)
                        {
                            object resetOpt = Enum.ToObject(p[0].ParameterType, 0);
                            resetMethod.Invoke(onlineApp, new object[] { resetOpt });
                        }
                        else if (p.Length == 2)
                        {
                            object resetOpt = Enum.ToObject(p[0].ParameterType, 0);
                            resetMethod.Invoke(onlineApp, new object[] { resetOpt, false });
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
                    CodesysContext.LogToIde($"[FORCE] write_variable: Forced '{name}' := '{value}'");
                }
                else
                {
                    var writeMethod = onlineApp.GetType().GetMethod("write_prepared_values");
                    writeMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"[WRITE] write_variable: Wrote '{name}' := '{value}'");
                }

                string actual = null;
                bool verified = false;
                try
                {
                    var readValMethod = onlineApp.GetType().GetMethod("read_value", new Type[] { typeof(string) });
                    actual = readValMethod?.Invoke(onlineApp, new object[] { name })?.ToString();
                    if (actual != null)
                    {
                        string normActual = actual.Trim();
                        if (normActual.Contains("#"))
                        {
                            normActual = normActual.Substring(normActual.IndexOf('#') + 1).Trim();
                        }
                        string normTarget = (value ?? string.Empty).Trim();
                        if (normTarget.Contains("#"))
                        {
                            normTarget = normTarget.Substring(normTarget.IndexOf('#') + 1).Trim();
                        }

                        verified = string.Equals(normActual, normTarget, StringComparison.OrdinalIgnoreCase) ||
                                   (double.TryParse(normActual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d1) &&
                                    double.TryParse(normTarget, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2) &&
                                    Math.Abs(d1 - d2) < 0.001) ||
                                   (bool.TryParse(normActual, out var b1) && bool.TryParse(normTarget, out var b2) && b1 == b2);
                    }
                }
                catch { }

                return new WriteVariableResult
                {
                    success = true,
                    name = name,
                    value = value,
                    actualValue = actual,
                    verified = verified,
                    isForced = force,
                    message = force
                        ? $"Variable '{name}' was forced to '{value}' (Readback: '{actual}', Verified: {verified})."
                        : $"Variable '{name}' was written with value '{value}' (Readback: '{actual}', Verified: {verified})."
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
                    try
                    {
                        var prop = proj.GetType().GetProperty("active_application");
                        var activeAppExt = prop?.GetValue(proj, null);
                        if (activeAppExt != null)
                        {
                            app = CodesysOperations.GetExtension<_3S.CoDeSys.ScriptEngine.BasicFunctionality.IScriptApplication>(activeAppExt);
                        }
                    }
                    catch { }
                }

                if (app == null) throw new InvalidOperationException("Could not resolve active application to create boot project.");

                app.create_boot_application(null);
                CodesysContext.LogToIde("create_boot_application: Successfully created boot project for application");
                return "Boot application created successfully.";
            });
        }
    }
}
