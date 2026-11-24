using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using _3S.CoDeSys.ScriptEngine;
using _3S.CoDeSys.ScriptEngine.BasicFunctionality;

namespace CodesysMcp
{
    public class DeviceCredentials
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public string Source { get; set; }
    }

    public class OnlineStatusResult
    {
        public bool isLoggedIn { get; set; }
        public string applicationState { get; set; } = "Unknown";
        public string operationState { get; set; } = "Unknown";
        public bool isException { get; set; } = false;
        public string exceptionDetails { get; set; }
        public List<string> decodedOperationFlags { get; set; } = new List<string>();
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

    public class BatchWriteItemResult
    {
        public string target { get; set; }
        public string actual { get; set; }
        public bool verified { get; set; }
        public string error { get; set; }
    }

    public class BatchWriteResult
    {
        public bool success { get; set; }
        public int totalCount { get; set; }
        public int succeededCount { get; set; }
        public int failedCount { get; set; }
        public bool allVerified { get; set; }
        public Dictionary<string, BatchWriteItemResult> details { get; set; } = new Dictionary<string, BatchWriteItemResult>();
        public string message { get; set; }
    }

    public class ReadArrayResult
    {
        public bool success { get; set; }
        public string variable { get; set; }
        public int startIndex { get; set; }
        public int count { get; set; }
        public List<string> items { get; set; } = new List<string>();
        public string message { get; set; }
    }

    public class ReadStructResult
    {
        public bool success { get; set; }
        public string variable { get; set; }
        public string typeName { get; set; }
        public Dictionary<string, object> values { get; set; } = new Dictionary<string, object>();
        public string rawText { get; set; }
        public string message { get; set; }
    }

    public static partial class CodesysOnlineHelper
    {
        private static object _cachedOnlineApp;
        public static object GetCachedOnlineApp() => _cachedOnlineApp;
        public static void SetCachedOnlineAppForTesting(object mockOnlineApp)
        {
            ResetOnlineMethodCache();
            _cachedOnlineApp = mockOnlineApp;
        }

        public static string NormalizeIecValue(string raw)
        {
            if (raw == null) return string.Empty;
            string s = raw.Trim();
            int hashIdx = s.IndexOf('#');
            if (hashIdx >= 0)
            {
                s = s.Substring(hashIdx + 1).Trim();
            }
            return s;
        }

        public static bool ValuesMatch(string actual, string target)
        {
            string normActual = NormalizeIecValue(actual);
            string normTarget = NormalizeIecValue(target);

            if (string.Equals(normActual, normTarget, StringComparison.OrdinalIgnoreCase))
                return true;

            if (double.TryParse(normActual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d1) &&
                double.TryParse(normTarget, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d2))
            {
                return Math.Abs(d1 - d2) < 0.001;
            }

            if (bool.TryParse(normActual, out var b1) && bool.TryParse(normTarget, out var b2))
            {
                return b1 == b2;
            }

            return false;
        }

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
                ResetOnlineMethodCache();
                ClearDutMembersCache();
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
                _cachedOnlineApp = null;
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

                    // Decode operationState bitmask and detect IEC runtime exceptions (bit 0x0020 = 32)
                    if (int.TryParse(res.operationState, out int opCode))
                    {
                        if ((opCode & 0x0001) != 0) res.decodedOperationFlags.Add("ProgramLoaded");
                        if ((opCode & 0x0002) != 0) res.decodedOperationFlags.Add("DebugActive");
                        if ((opCode & 0x0004) != 0) res.decodedOperationFlags.Add("ForceActive");
                        if ((opCode & 0x0008) != 0) res.decodedOperationFlags.Add("FlowActive");
                        if ((opCode & 0x0010) != 0) res.decodedOperationFlags.Add("BreakpointHit");
                        if ((opCode & 0x0020) != 0)
                        {
                            res.decodedOperationFlags.Add("Exception");
                            res.isException = true;
                        }
                        if ((opCode & 0x1000) != 0) res.decodedOperationFlags.Add("OnlineConnected");
                    }
                    if (res.applicationState.IndexOf("exception", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        res.isException = true;
                    }

                    if (res.isException)
                    {
                        res.exceptionDetails = $"CRITICAL: CODESYS PLC Runtime Exception detected! (State: {res.applicationState}, Operation: {res.operationState} [{string.Join("|", res.decodedOperationFlags)}]). Program execution is halted.";
                    }

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

                    if (res.isException)
                    {
                        res.message = res.exceptionDetails;
                    }
                    else
                    {
                        res.message = res.isLoggedIn
                            ? $"Connected to PLC (State: {res.applicationState}, Operation: {res.operationState} [{string.Join("|", res.decodedOperationFlags)}])."
                            : "Online application session is disconnected.";
                    }
                }
                catch (Exception ex)
                {
                    res.isLoggedIn = false;
                    res.message = "Failed to query PLC state: " + ex.Message;
                }

                return res;
            });
        }

        public static DeviceCredentials ResolveDeviceCredentials(string username = null, string password = null)
        {
            if (!string.IsNullOrEmpty(username))
            {
                return new DeviceCredentials
                {
                    Username = username,
                    Password = password ?? string.Empty,
                    Source = "Explicit"
                };
            }

            string envUser = Environment.GetEnvironmentVariable("CODESYS_DEVICE_USER");
            string envPass = Environment.GetEnvironmentVariable("CODESYS_DEVICE_PASSWORD");
            if (!string.IsNullOrEmpty(envUser) || !string.IsNullOrEmpty(envPass))
            {
                return new DeviceCredentials
                {
                    Username = !string.IsNullOrEmpty(envUser) ? envUser : "admin",
                    Password = envPass ?? string.Empty,
                    Source = "Environment"
                };
            }

            // Default SoftPLC development credentials (password supplied via env or explicit args)
            return new DeviceCredentials
            {
                Username = "admin",
                Password = string.Empty,
                Source = "Default"
            };
        }

        public static OnlineStatusResult PlcLogin(string mode = "try_online_change", bool deleteForeignApps = true, string appName = null, string username = null, string password = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var creds = ResolveDeviceCredentials(username, password);
                if (!string.IsNullOrEmpty(creds.Username) && CodesysContext.Online != null)
                {
                    try
                    {
                        var setCredMethod = CodesysContext.Online.GetType().GetMethod("set_default_credentials", new Type[] { typeof(string), typeof(string) });
                        if (setCredMethod != null)
                        {
                            setCredMethod.Invoke(CodesysContext.Online, new object[] { creds.Username, creds.Password ?? string.Empty });
                            CodesysContext.LogToIde($"[INFO] plc_login: Applied device credentials for user '{creds.Username}' ({creds.Source}) via online.set_default_credentials.");
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
                    int win32ThreadId = DeviceLogonWatchdog.GetCurrentThreadId();
                    CodesysContext.LogToIde($"[STEP] plc_login: Calling login({changeOptionObj}, deleteForeignApps={deleteForeignApps}) on Thread={System.Threading.Thread.CurrentThread.ManagedThreadId} (Win32={win32ThreadId}, Apartment={System.Threading.Thread.CurrentThread.GetApartmentState()})...");

                    using (new DeviceLogonWatchdog(win32ThreadId, creds.Username, creds.Password))
                    {
                        loginMethod.Invoke(onlineApp, loginArgs);
                    }

                    TryEvictOnlineVarRefs(onlineApp, null);
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
                    string fullErr = inner.ToString();
                    CodesysContext.LogToIde($"[ERROR] plc_login failed: {fullErr}");
                    throw new Exception(fullErr);
                }
                catch (Exception ex)
                {
                    string fullErr = ex.ToString();
                    CodesysContext.LogToIde($"[ERROR] plc_login failed: {fullErr}");
                    throw new Exception(fullErr);
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
            }, 120000);
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
                    TryEvictOnlineVarRefs(onlineApp, null);
                    CodesysContext.LogToIde("plc_control: Reset PLC application");
                }
                else
                {
                    throw new NotSupportedException($"Action '{action}' is not supported. Use 'start', 'stop', or 'reset'.");
                }

                return GetOnlineStatus();
            }, 120000);
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
            }, 120000);
        }

        public static object InspectOnline(string targetName = "Device")
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var dict = new Dictionary<string, object>();
                
                if (CodesysContext.Online != null)
                {
                    dict["online_type"] = CodesysContext.Online.GetType().FullName;
                    var onlineMethods = new List<string>();
                    foreach (var m in CodesysContext.Online.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        onlineMethods.Add($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    dict["online_methods"] = onlineMethods;

                    try
                    {
                        var getGateways = CodesysContext.Online.GetType().GetMethod("get_gateways");
                        var gwObj = getGateways?.Invoke(CodesysContext.Online, null);
                        if (gwObj != null)
                        {
                            dict["gateways_type"] = gwObj.GetType().FullName;
                            var gwMethods = new List<string>();
                            foreach (var m in gwObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            {
                                gwMethods.Add($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                            }
                            dict["gateways_methods"] = gwMethods;

                            // Scan Gateway-1
                            try
                            {
                                if (gwObj is IEnumerable gwEnumScan)
                                {
                                    foreach (var g in gwEnumScan)
                                    {
                                        var scanMethod = g.GetType().GetMethod("perform_network_scan");
                                        if (scanMethod != null)
                                        {
                                            var scanRes = scanMethod.Invoke(g, null) as IEnumerable;
                                            var scannedList = new List<Dictionary<string, object>>();
                                            if (scanRes != null)
                                            {
                                                foreach (var item in scanRes)
                                                {
                                                    var idict = new Dictionary<string, object>();
                                                    idict["type"] = item.GetType().FullName;
                                                    var iprops = new List<string>();
                                                    foreach (var prop in item.GetType().GetProperties())
                                                    {
                                                        try { iprops.Add($"{prop.Name} = {prop.GetValue(item, null)}"); } catch { }
                                                    }
                                                    idict["properties"] = iprops;
                                                    scannedList.Add(idict);
                                                }
                                            }
                                            dict["scanned_devices"] = scannedList;
                                        }
                                        break;
                                    }
                                }
                            }
                            catch (Exception scEx)
                            {
                                dict["scan_error"] = scEx.ToString();
                            }

                            // If enumerable, list gateways
                            var gwList = new List<Dictionary<string, object>>();
                            if (gwObj is IEnumerable gwEnum)
                            {
                                foreach (var g in gwEnum)
                                {
                                    var gd = new Dictionary<string, object>();
                                    gd["type"] = g.GetType().FullName;
                                    var gp = new List<string>();
                                    foreach (var p in g.GetType().GetProperties())
                                    {
                                        try { gp.Add($"{p.Name} = {p.GetValue(g, null)}"); } catch { }
                                    }
                                    gd["properties"] = gp;
                                    var gm = new List<string>();
                                    foreach (var m in g.GetType().GetMethods())
                                    {
                                        gm.Add($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                                    }
                                    gd["methods"] = gm;
                                    gwList.Add(gd);
                                }
                            }
                            dict["gateways_list"] = gwList;
                        }
                    }
                    catch (Exception gwx)
                    {
                        dict["gateways_error"] = gwx.ToString();
                    }
                }

                var devObj = CodesysOperations.FindObjectByName(targetName);
                if (devObj != null)
                {
                    // Inspect message categories
                    try
                    {
                        var sys3 = CodesysContext.System as ISystem3;
                        if (sys3 != null)
                        {
                            var catList = new List<Dictionary<string, object>>();
                            foreach (var cat in sys3.get_message_categories())
                            {
                                var cd = new Dictionary<string, object>();
                                cd["guid"] = cat.ToString();
                                var msgs = sys3.get_message_objects(cat);
                                cd["count"] = msgs != null ? msgs.Count : 0;
                                var sample = new List<string>();
                                if (msgs != null)
                                {
                                    foreach (var m in msgs)
                                    {
                                        if (m is IScriptMessage sm)
                                        {
                                            sample.Add($"[{sm.severity}] {sm.prefix}{sm.number}: {sm.text}");
                                        }
                                    }
                                }
                                cd["messages"] = sample;
                                catList.Add(cd);
                            }
                            dict["message_categories"] = catList;
                        }
                    }
                    catch (Exception mex)
                    {
                        dict["message_categories_error"] = mex.ToString();
                    }
                    dict["target_type"] = devObj.GetType().FullName;
                    var extList = new List<string>();
                    var extProps = new Dictionary<string, object>();
                    var ext = devObj as IExtendedObject<IScriptObject>;
                    if (ext != null && ext.Extensions != null)
                    {
                        foreach (var e in ext.Extensions)
                        {
                            string eType = e.GetType().FullName;
                            extList.Add(eType);
                            var pList = new List<string>();
                            foreach (var prop in e.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                            {
                                try
                                {
                                    var val = prop.GetValue(e, null);
                                    pList.Add($"{prop.Name} = {val}");
                                }
                                catch (Exception px)
                                {
                                    pList.Add($"{prop.Name} = <ERR: {px.Message}>");
                                }
                            }
                            extProps[eType] = pList;
                        }
                    }
                    dict["extensions"] = extList;
                    dict["extension_properties"] = extProps;

                    // Inspect connectors
                    var connInfo = new List<Dictionary<string, object>>();
                    if (ext != null && ext.Extensions != null)
                    {
                        foreach (var e in ext.Extensions)
                    {
                        var connProp = e.GetType().GetProperty("connectors");
                        if (connProp != null)
                        {
                            var connSet = connProp.GetValue(e, null) as IEnumerable;
                            if (connSet != null)
                            {
                                foreach (var c in connSet)
                                {
                                    var cd = new Dictionary<string, object>();
                                    cd["type"] = c.GetType().FullName;
                                    var cProps = new List<string>();
                                    foreach (var cp in c.GetType().GetProperties())
                                    {
                                        try { cProps.Add($"{cp.Name} = {cp.GetValue(c, null)}"); }
                                        catch (Exception cpx) { cProps.Add($"{cp.Name} = <ERR: {cpx.Message}>"); }
                                    }
                                    cd["properties"] = cProps;

                                    var cMethods = new List<string>();
                                    foreach (var cm in c.GetType().GetMethods())
                                    {
                                        cMethods.Add($"{cm.ReturnType.Name} {cm.Name}({string.Join(", ", cm.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                                    }
                                    cd["methods"] = cMethods;
                                    connInfo.Add(cd);
                                }
                            }
                        }
                    }
                    }
                    dict["connectors"] = connInfo;

                    var methods = new List<string>();
                    foreach (var m in devObj.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        methods.Add($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                    }
                    dict["target_methods"] = methods;

                    if (CodesysContext.Online != null)
                    {
                        try
                        {
                            MethodInfo createDevMethod = null;
                            foreach (var cm in CodesysContext.Online.GetType().GetMethods())
                            {
                                if (cm.Name == "create_online_device")
                                {
                                    createDevMethod = cm;
                                    break;
                                }
                            }
                            if (createDevMethod != null)
                            {
                                var odev = createDevMethod.Invoke(CodesysContext.Online, new object[] { devObj });
                                if (odev != null)
                                {
                                    dict["online_device_type"] = odev.GetType().FullName;
                                    var odevMethods = new List<string>();
                                    foreach (var m in odev.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    {
                                        odevMethods.Add($"{m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})");
                                    }
                                    dict["online_device_methods"] = odevMethods;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            dict["create_online_device_error"] = ex.ToString();
                        }
                    }
                }

                return dict;
            });
        }
    }
}
