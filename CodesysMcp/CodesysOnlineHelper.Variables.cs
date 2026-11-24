using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace CodesysMcp
{
    public static partial class CodesysOnlineHelper
    {
        private static readonly object _onlineCacheLock = new object();
        private static Type _cachedMethodAppType;
        private static MethodInfo _cachedReadValueMethod;
        private static MethodInfo _cachedReadValuesArrayMethod;
        private static MethodInfo _cachedSetPreparedValueMethod;
        private static MethodInfo _cachedWritePreparedValuesMethod;
        private static MethodInfo _cachedForcePreparedValuesMethod;
        private static MethodInfo _cachedUnforceAllValuesMethod;
        private static MethodInfo _cachedSetUnforceValueMethod;
        private static FieldInfo _cachedOnlineVarRefsField;

        private static readonly Dictionary<string, List<string>> _dutMembersCache =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, Dictionary<string, int>> _enumValuesCache =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, int> _globalEnumMemberCache =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static int BatchVerifyRetryDelayMs { get; set; } = 50;
        public static int BatchVerifyMaxRetries { get; set; } = 3;

        private static void ResetOnlineMethodCache()
        {
            lock (_onlineCacheLock)
            {
                _cachedMethodAppType = null;
                _cachedReadValueMethod = null;
                _cachedReadValuesArrayMethod = null;
                _cachedSetPreparedValueMethod = null;
                _cachedWritePreparedValuesMethod = null;
                _cachedForcePreparedValuesMethod = null;
                _cachedUnforceAllValuesMethod = null;
                _cachedSetUnforceValueMethod = null;
                _cachedOnlineVarRefsField = null;
            }
        }

        private static void EnsureOnlineMethodsCached(object onlineApp)
        {
            if (onlineApp == null) return;
            Type appType = onlineApp.GetType();
            lock (_onlineCacheLock)
            {
                if (_cachedMethodAppType == appType) return;
                _cachedMethodAppType = appType;
                _cachedReadValueMethod = appType.GetMethod("read_value", new Type[] { typeof(string) });
                _cachedReadValuesArrayMethod = appType.GetMethod("read_values", new Type[] { typeof(string[]) });
                _cachedSetPreparedValueMethod = appType.GetMethod("set_prepared_value", new Type[] { typeof(string), typeof(string) });
                _cachedWritePreparedValuesMethod = appType.GetMethod("write_prepared_values");
                _cachedForcePreparedValuesMethod = appType.GetMethod("force_prepared_values");
                _cachedUnforceAllValuesMethod = appType.GetMethod("unforce_all_values");
                _cachedSetUnforceValueMethod = appType.GetMethod("set_unforce_value", new Type[] { typeof(string) });
                _cachedOnlineVarRefsField = appType.GetField("_onlineVarRefs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
        }

        public static void ClearDutMembersCache()
        {
            lock (_onlineCacheLock)
            {
                _dutMembersCache.Clear();
                _enumValuesCache.Clear();
                _globalEnumMemberCache.Clear();
            }
        }

        public static void ClearEnumCache()
        {
            lock (_onlineCacheLock)
            {
                _enumValuesCache.Clear();
                _globalEnumMemberCache.Clear();
            }
        }

        /// <summary>
        /// Parses an IEC 61131-3 TYPE ... : ( ... ); END_TYPE enumeration declaration into member -> integer value pairs.
        /// </summary>
        public static Dictionary<string, int> ParseEnumDeclaration(string declaration)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(declaration)) return result;

            // Strip (* ... *) block comments and // ... line comments
            string cleaned = Regex.Replace(declaration, @"\(\*[\s\S]*?\*\)", " ");
            cleaned = Regex.Replace(cleaned, @"//[^\r\n]*", " ");

            var bodyMatch = Regex.Match(cleaned, @"\(([\s\S]*?)\)");
            if (!bodyMatch.Success) return result;

            string body = bodyMatch.Groups[1].Value;
            string[] items = body.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            int nextVal = 0;

            foreach (string rawItem in items)
            {
                string item = rawItem.Trim();
                if (string.IsNullOrEmpty(item)) continue;

                var assignMatch = Regex.Match(item, @"^([A-Za-z_][A-Za-z0-9_]*)\s*:=\s*(-?\d+)$");
                if (assignMatch.Success)
                {
                    string member = assignMatch.Groups[1].Value.Trim();
                    int val = int.Parse(assignMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    result[member] = val;
                    nextVal = val + 1;
                    continue;
                }

                var identMatch = Regex.Match(item, @"^([A-Za-z_][A-Za-z0-9_]*)$");
                if (identMatch.Success)
                {
                    string member = identMatch.Groups[1].Value.Trim();
                    result[member] = nextVal;
                    nextVal++;
                }
            }

            return result;
        }

        public static void SeedEnumDeclarationForTesting(string enumTypeName, string declaration)
        {
            if (string.IsNullOrWhiteSpace(enumTypeName)) return;
            var parsed = ParseEnumDeclaration(declaration);
            if (parsed.Count == 0) return;

            lock (_onlineCacheLock)
            {
                _enumValuesCache[enumTypeName.Trim()] = parsed;
                foreach (var kvp in parsed)
                {
                    _globalEnumMemberCache[kvp.Key] = kvp.Value;
                }
            }
        }

        private static Dictionary<string, int> GetOrLoadEnumMembers(string enumTypeName)
        {
            if (string.IsNullOrWhiteSpace(enumTypeName)) return null;
            string key = enumTypeName.Trim();

            lock (_onlineCacheLock)
            {
                if (_enumValuesCache.TryGetValue(key, out var cached))
                    return cached;
            }

            try
            {
                var obj = CodesysOperations.FindObjectByName(key);
                if (obj != null)
                {
                    var content = CodesysOperations.ReadPou(key, includeLiveEditorOverlay: true);
                    var parsed = ParseEnumDeclaration(content?.declaration);
                    if (parsed.Count > 0)
                    {
                        lock (_onlineCacheLock)
                        {
                            _enumValuesCache[key] = parsed;
                            foreach (var kvp in parsed)
                            {
                                _globalEnumMemberCache[kvp.Key] = kvp.Value;
                            }
                        }
                        return parsed;
                    }
                }
            }
            catch { }

            return null;
        }

        private static string TryResolveExpressionType(string varExpression)
        {
            if (string.IsNullOrWhiteSpace(varExpression)) return null;
            string cleanExpr = Regex.Replace(varExpression.Trim(), @"\[.*\]$", "");
            string[] parts = cleanExpr.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            string currentContainer = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                try
                {
                    var obj = CodesysOperations.FindObjectByName(currentContainer);
                    if (obj == null) return null;
                    var content = CodesysOperations.ReadPou(currentContainer, includeLiveEditorOverlay: true);
                    string resolved = TryResolveVariableTypeFromDeclaration(content?.declaration, parts[i]);
                    if (string.IsNullOrEmpty(resolved)) return null;
                    currentContainer = resolved;
                }
                catch
                {
                    return null;
                }
            }
            return currentContainer;
        }

        public static string ResolveEnumSymbolToIntString(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return null;
            string s = symbol.Trim();

            int dotIdx = s.IndexOf('.');
            if (dotIdx > 0 && dotIdx < s.Length - 1)
            {
                string typeName = s.Substring(0, dotIdx).Trim();
                string memberName = s.Substring(dotIdx + 1).Trim();
                var map = GetOrLoadEnumMembers(typeName);
                if (map != null && map.TryGetValue(memberName, out int intVal))
                {
                    return intVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                return null;
            }

            if (Regex.IsMatch(s, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                lock (_onlineCacheLock)
                {
                    if (_globalEnumMemberCache.TryGetValue(s, out int intVal))
                    {
                        return intVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                    foreach (var map in _enumValuesCache.Values)
                    {
                        if (map.TryGetValue(s, out int val))
                        {
                            return val.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                }
            }

            return null;
        }

        public static string ResolveEnumValueForWrite(string varExpression, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue)) return rawValue;
            string trimmed = rawValue.Trim();

            if (string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, "FALSE", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("'", StringComparison.Ordinal) ||
                trimmed.Contains("#") ||
                double.TryParse(trimmed, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                return rawValue;
            }

            // 1. Qualified EnumType.Member
            if (trimmed.Contains("."))
            {
                string resolved = ResolveEnumSymbolToIntString(trimmed);
                if (!string.IsNullOrEmpty(resolved)) return resolved;
            }

            // 2. Unqualified identifier: try variable's declared type first
            if (Regex.IsMatch(trimmed, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                string declType = TryResolveExpressionType(varExpression);
                if (!string.IsNullOrEmpty(declType))
                {
                    var map = GetOrLoadEnumMembers(declType);
                    if (map != null && map.TryGetValue(trimmed, out int intVal))
                    {
                        return intVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                string globalResolved = ResolveEnumSymbolToIntString(trimmed);
                if (!string.IsNullOrEmpty(globalResolved)) return globalResolved;
            }

            return rawValue;
        }

        public static int GetDutMembersCacheCountForTesting()
        {
            lock (_onlineCacheLock)
            {
                return _dutMembersCache.Count;
            }
        }

        public static void SeedDutMembersCacheForTesting(string key, List<string> members)
        {
            if (string.IsNullOrWhiteSpace(key) || members == null) return;
            lock (_onlineCacheLock)
            {
                _dutMembersCache[key.Trim()] = new List<string>(members);
            }
        }

        public static bool IsStaleVarRefError(string val)
        {
            if (string.IsNullOrEmpty(val)) return false;
            return val.IndexOf("无效的变量引用", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   val.IndexOf("Invalid variable reference", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Releases and evicts cached IOnlineVarRef entries from ScriptDriverOnline.OnlineApplication._onlineVarRefs.
        /// Passing null clears all cached handles (used after login/reset); passing expressions evicts only matching stale entries.
        /// </summary>
        public static int TryEvictOnlineVarRefs(object onlineApp, IEnumerable<string> expressions = null)
        {
            if (onlineApp == null) return 0;
            EnsureOnlineMethodsCached(onlineApp);
            var field = _cachedOnlineVarRefsField;
            if (field == null) return 0;

            var dict = field.GetValue(onlineApp) as IDictionary;
            if (dict == null || dict.Count == 0) return 0;

            var keysToEvict = new List<object>();
            if (expressions == null)
            {
                foreach (var k in dict.Keys)
                {
                    if (k != null) keysToEvict.Add(k);
                }
            }
            else
            {
                var targetSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var expr in expressions)
                {
                    if (!string.IsNullOrWhiteSpace(expr))
                    {
                        targetSet.Add(expr.Trim());
                    }
                }
                if (targetSet.Count == 0) return 0;

                foreach (var k in dict.Keys)
                {
                    if (k != null && targetSet.Contains(k.ToString().Trim()))
                    {
                        keysToEvict.Add(k);
                    }
                }
            }

            int evicted = 0;
            foreach (var key in keysToEvict)
            {
                try
                {
                    var varRef = dict[key];
                    varRef?.GetType().GetMethod("Release")?.Invoke(varRef, null);
                }
                catch { }
                try
                {
                    dict.Remove(key);
                    evicted++;
                }
                catch { }
            }
            return evicted;
        }

        private static bool TryBatchReadRawValues(object onlineApp, List<string> cleanExprs, out List<string> values)
        {
            values = null;
            var batchMethod = _cachedReadValuesArrayMethod;
            if (batchMethod == null || cleanExprs == null || cleanExprs.Count <= 1)
            {
                return false;
            }

            try
            {
                var rawResult = batchMethod.Invoke(onlineApp, new object[] { cleanExprs.ToArray() }) as IEnumerable;
                if (rawResult == null) return false;

                var list = new List<string>(cleanExprs.Count);
                foreach (var item in rawResult)
                {
                    list.Add(item?.ToString() ?? string.Empty);
                }

                if (list.Count == cleanExprs.Count)
                {
                    values = list;
                    return true;
                }
            }
            catch
            {
                // Fall back to per-expression reads if the batch contains an invalid expression
            }

            return false;
        }

        private static void ReadRawValuesCore(object onlineApp, List<string> rawKeys, List<string> cleanExprs, Dictionary<string, string> output)
        {
            if (TryBatchReadRawValues(onlineApp, cleanExprs, out var batchVals))
            {
                for (int i = 0; i < rawKeys.Count; i++)
                {
                    output[rawKeys[i]] = batchVals[i];
                }
                return;
            }

            var readValMethod = _cachedReadValueMethod;
            for (int i = 0; i < rawKeys.Count; i++)
            {
                try
                {
                    var val = readValMethod?.Invoke(onlineApp, new object[] { cleanExprs[i] })?.ToString();
                    output[rawKeys[i]] = val ?? string.Empty;
                }
                catch (Exception ex)
                {
                    output[rawKeys[i]] = "<Error: " + (ex.InnerException?.Message ?? ex.Message) + ">";
                }
            }
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
                EnsureOnlineMethodsCached(onlineApp);

                var rawKeys = new List<string>(expressions.Count);
                var cleanExprs = new List<string>(expressions.Count);
                foreach (var expr in expressions)
                {
                    if (string.IsNullOrWhiteSpace(expr)) continue;
                    rawKeys.Add(expr);
                    cleanExprs.Add(expr.Trim());
                }

                if (cleanExprs.Count == 0)
                {
                    res.success = true;
                    res.message = "No expressions provided to read.";
                    return res;
                }

                ReadRawValuesCore(onlineApp, rawKeys, cleanExprs, res.values);

                var staleRawKeys = new List<string>();
                var staleCleanExprs = new List<string>();
                for (int i = 0; i < rawKeys.Count; i++)
                {
                    if (res.values.TryGetValue(rawKeys[i], out string val) && IsStaleVarRefError(val))
                    {
                        staleRawKeys.Add(rawKeys[i]);
                        staleCleanExprs.Add(cleanExprs[i]);
                    }
                }

                if (staleCleanExprs.Count > 0 && TryEvictOnlineVarRefs(onlineApp, staleCleanExprs) > 0)
                {
                    ReadRawValuesCore(onlineApp, staleRawKeys, staleCleanExprs, res.values);
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
                EnsureOnlineMethodsCached(onlineApp);
                var setPrep = _cachedSetPreparedValueMethod;
                if (setPrep == null)
                    throw new InvalidOperationException("Method 'set_prepared_value' not found.");

                string effectiveValue = ResolveEnumValueForWrite(name, value ?? string.Empty);
                setPrep.Invoke(onlineApp, new object[] { name, effectiveValue });

                if (force)
                {
                    _cachedForcePreparedValuesMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"[FORCE] write_variable: Forced '{name}' := '{effectiveValue}'");
                }
                else
                {
                    _cachedWritePreparedValuesMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"[WRITE] write_variable: Wrote '{name}' := '{effectiveValue}'");
                }

                string actual = null;
                bool verified = false;
                try
                {
                    actual = _cachedReadValueMethod?.Invoke(onlineApp, new object[] { name })?.ToString();
                    if (actual != null)
                    {
                        verified = ValuesMatch(actual, effectiveValue) || ValuesMatch(actual, value);
                    }
                }
                catch { }

                return new WriteVariableResult
                {
                    success = true,
                    name = name,
                    value = effectiveValue,
                    actualValue = actual,
                    verified = verified,
                    isForced = force,
                    message = force
                        ? $"Variable '{name}' was forced to '{effectiveValue}' (Readback: '{actual}', Verified: {verified})."
                        : $"Variable '{name}' was written with value '{effectiveValue}' (Readback: '{actual}', Verified: {verified})."
                };
            });
        }

        public static PulseVariableResult PulseVariable(
            string name,
            int pulseMs = 100,
            string activeValue = "TRUE",
            string inactiveValue = "FALSE",
            bool preClear = false,
            bool force = false)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Parameter 'name' is required.");

            int clampedPulseMs = Math.Max(10, Math.Min(10000, pulseMs));
            string actVal = string.IsNullOrEmpty(activeValue) ? "TRUE" : activeValue;
            string inactVal = string.IsNullOrEmpty(inactiveValue) ? "FALSE" : inactiveValue;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            if (preClear)
            {
                WriteVariable(name, inactVal, force);
            }

            WriteVariable(name, actVal, force);
            Thread.Sleep(clampedPulseMs);
            var resetRes = WriteVariable(name, inactVal, force);
            sw.Stop();

            return new PulseVariableResult
            {
                success = resetRes.success,
                name = name,
                activeValue = actVal,
                inactiveValue = inactVal,
                pulseMs = clampedPulseMs,
                elapsedMs = sw.ElapsedMilliseconds,
                finalValue = resetRes.actualValue,
                verified = resetRes.verified,
                message = $"Pulsed '{name}' ({actVal} -> {inactVal} after {clampedPulseMs}ms, elapsed={sw.ElapsedMilliseconds}ms, verified={resetRes.verified})."
            };
        }

        private static bool EvaluateWaitCondition(string actual, string target, string op)
        {
            string normOp = (op ?? "==").Trim().ToLowerInvariant();
            if (normOp == "==" || normOp == "=" || normOp == "eq")
            {
                return ValuesMatch(actual, target);
            }
            if (normOp == "!=" || normOp == "<>" || normOp == "ne")
            {
                return !ValuesMatch(actual, target);
            }
            if (normOp == "contains")
            {
                return (actual ?? string.Empty).IndexOf(target ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            string normActual = NormalizeIecValue(actual);
            string normTarget = NormalizeIecValue(target);
            if (double.TryParse(normActual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dActual) &&
                double.TryParse(normTarget, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double dTarget))
            {
                if (normOp == ">" || normOp == "gt") return dActual > dTarget;
                if (normOp == ">=" || normOp == "ge") return dActual >= dTarget;
                if (normOp == "<" || normOp == "lt") return dActual < dTarget;
                if (normOp == "<=" || normOp == "le") return dActual <= dTarget;
            }

            throw new ArgumentException($"Unsupported wait operator '{op}' or non-numeric comparison ('{actual}' {op} '{target}').");
        }

        public static WaitForVariableResult WaitForVariable(
            string name,
            string targetValue,
            string op = "==",
            int timeoutMs = 5000,
            int pollMs = 50)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Parameter 'name' is required.");
            if (targetValue == null)
                throw new ArgumentException("Parameter 'target_value' is required.");

            int clampedTimeout = Math.Max(0, Math.Min(120000, timeoutMs));
            int clampedPoll = Math.Max(5, Math.Min(2000, pollMs));
            string normalizedOp = string.IsNullOrWhiteSpace(op) ? "==" : op.Trim();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int samples = 0;
            string lastVal = null;

            while (true)
            {
                var readRes = ReadVariables(new List<string> { name });
                samples++;
                if (readRes.values != null && readRes.values.TryGetValue(name, out string val))
                {
                    lastVal = val;
                    if (!IsStaleVarRefError(val) && EvaluateWaitCondition(val, targetValue, normalizedOp))
                    {
                        sw.Stop();
                        return new WaitForVariableResult
                        {
                            success = true,
                            matched = true,
                            timedOut = false,
                            name = name,
                            targetValue = targetValue,
                            op = normalizedOp,
                            lastValue = lastVal,
                            elapsedMs = sw.ElapsedMilliseconds,
                            samplesCount = samples,
                            message = $"Condition '{name} {normalizedOp} {targetValue}' matched in {sw.ElapsedMilliseconds}ms ({samples} sample(s), value='{lastVal}')."
                        };
                    }
                }

                if (sw.ElapsedMilliseconds >= clampedTimeout)
                {
                    sw.Stop();
                    return new WaitForVariableResult
                    {
                        success = false,
                        matched = false,
                        timedOut = true,
                        name = name,
                        targetValue = targetValue,
                        op = normalizedOp,
                        lastValue = lastVal,
                        elapsedMs = sw.ElapsedMilliseconds,
                        samplesCount = samples,
                        message = $"Timed out after {sw.ElapsedMilliseconds}ms waiting for '{name} {normalizedOp} {targetValue}' (last value='{lastVal}', samples={samples})."
                    };
                }

                Thread.Sleep(clampedPoll);
            }
        }

        public static OnlineStatusResult UnforceVariables(List<string> expressions = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var onlineApp = GetOrCreateOnlineApp();
                EnsureOnlineMethodsCached(onlineApp);
                if (expressions == null || expressions.Count == 0)
                {
                    _cachedUnforceAllValuesMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde("unforce_variables: Unforced all active forced expressions");
                }
                else
                {
                    var setUnforce = _cachedSetUnforceValueMethod;
                    foreach (var expr in expressions)
                    {
                        setUnforce?.Invoke(onlineApp, new object[] { expr });
                    }
                    _cachedForcePreparedValuesMethod?.Invoke(onlineApp, null);
                    CodesysContext.LogToIde($"unforce_variables: Unforced {expressions.Count} expression(s)");
                }

                return GetOnlineStatus();
            }, 120000);
        }

        public static void FlattenObject(string prefix, object obj, Dictionary<string, string> output)
        {
            if (obj == null) return;

            if (obj is IDictionary dict)
            {
                foreach (DictionaryEntry kv in dict)
                {
                    string key = kv.Key?.ToString();
                    if (string.IsNullOrEmpty(key)) continue;
                    string fullKey = string.IsNullOrEmpty(prefix) ? key : prefix + "." + key;
                    FlattenObject(fullKey, kv.Value, output);
                }
            }
            else if (obj is IList list && !(obj is string))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    string fullKey = $"{prefix}[{i}]";
                    FlattenObject(fullKey, list[i], output);
                }
            }
            else if (obj is bool b)
            {
                output[prefix] = b ? "TRUE" : "FALSE";
            }
            else
            {
                output[prefix] = obj.ToString();
            }
        }

        public static Dictionary<string, object> ParseIecStructString(string raw)
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(raw)) return dict;
            string s = raw.Trim();
            if (s.StartsWith("(") && s.EndsWith(")"))
            {
                s = s.Substring(1, s.Length - 2).Trim();
            }

            int depth = 0;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                else if (s[i] == ',' && depth == 0)
                {
                    AddIecField(s.Substring(start, i - start), dict);
                    start = i + 1;
                }
            }
            if (start < s.Length)
            {
                AddIecField(s.Substring(start), dict);
            }
            return dict;
        }

        private static void AddIecField(string fieldStr, Dictionary<string, object> dict)
        {
            if (string.IsNullOrWhiteSpace(fieldStr)) return;
            int assignIdx = fieldStr.IndexOf(":=");
            if (assignIdx > 0)
            {
                string key = fieldStr.Substring(0, assignIdx).Trim();
                string val = fieldStr.Substring(assignIdx + 2).Trim();
                dict[key] = NormalizeIecValue(val);
            }
        }

        private static string TryResolveVariableTypeFromDeclaration(string decl, string varName)
        {
            if (string.IsNullOrEmpty(decl) || string.IsNullOrWhiteSpace(varName)) return null;
            string cleanName = Regex.Replace(varName.Trim(), @"\[.*\]$", "");
            var m = Regex.Match(
                decl,
                $@"(?i)\b{Regex.Escape(cleanName)}\s*:\s*(?:ARRAY\s*\[.*?\]\s*OF\s+)?([a-zA-Z_][a-zA-Z0-9_]*)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public static List<string> GetDutMembers(string typeOrVarName)
        {
            var members = new List<string>();
            if (string.IsNullOrWhiteSpace(typeOrVarName)) return members;

            string key = typeOrVarName.Trim();
            lock (_onlineCacheLock)
            {
                if (_dutMembersCache.TryGetValue(key, out var cached))
                {
                    return new List<string>(cached);
                }
            }

            try
            {
                string targetType = Regex.Replace(key, @"\[.*\]$", "");
                if (targetType.Contains("."))
                {
                    string[] parts = targetType.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                    string currentContainer = parts[0];
                    for (int i = 1; i < parts.Length; i++)
                    {
                        var containerObj = CodesysOperations.FindObjectByName(currentContainer);
                        if (containerObj == null)
                        {
                            currentContainer = null;
                            break;
                        }
                        var content = CodesysOperations.ReadPou(currentContainer);
                        string resolvedType = TryResolveVariableTypeFromDeclaration(content?.declaration, parts[i]);
                        if (string.IsNullOrEmpty(resolvedType))
                        {
                            currentContainer = null;
                            break;
                        }
                        currentContainer = resolvedType;
                    }
                    if (!string.IsNullOrEmpty(currentContainer))
                    {
                        targetType = currentContainer;
                    }
                }

                var dutObj = CodesysOperations.FindObjectByName(targetType);
                if (dutObj != null)
                {
                    var dutContent = CodesysOperations.ReadPou(targetType);
                    string decl = dutContent.declaration ?? "";
                    var structMatch = Regex.Match(decl, @"(?is)\bSTRUCT\b(.*?)\bEND_STRUCT\b");
                    if (structMatch.Success)
                    {
                        string body = structMatch.Groups[1].Value;
                        var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var rawLine in lines)
                        {
                            string line = rawLine.Trim();
                            if (line.StartsWith("//") || line.StartsWith("(*")) continue;
                            int commentIdx = line.IndexOf("//");
                            if (commentIdx >= 0) line = line.Substring(0, commentIdx).Trim();

                            var m = Regex.Match(line, @"^\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*:");
                            if (m.Success)
                            {
                                string memberName = m.Groups[1].Value.Trim();
                                if (!members.Contains(memberName))
                                {
                                    members.Add(memberName);
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            if (members.Count > 0)
            {
                lock (_onlineCacheLock)
                {
                    _dutMembersCache[key] = new List<string>(members);
                }
            }

            return members;
        }

        public static BatchWriteResult BatchWriteVariables(Dictionary<string, string> vars, bool verify = true, bool force = false)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var result = new BatchWriteResult();
                if (vars == null || vars.Count == 0)
                {
                    result.success = true;
                    result.message = "No variables provided for batch write.";
                    return result;
                }

                result.totalCount = vars.Count;
                var onlineApp = GetOrCreateOnlineApp();
                EnsureOnlineMethodsCached(onlineApp);
                var setPrep = _cachedSetPreparedValueMethod;
                if (setPrep == null)
                    throw new InvalidOperationException("Method 'set_prepared_value' not found on online application.");

                var resolvedVars = new Dictionary<string, string>(vars.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in vars)
                {
                    resolvedVars[kvp.Key] = ResolveEnumValueForWrite(kvp.Key, kvp.Value ?? string.Empty);
                }

                // Phase 1: Set prepared values in memory for single-cycle atomic write
                foreach (var kvp in resolvedVars)
                {
                    var itemRes = new BatchWriteItemResult { target = kvp.Value };
                    result.details[kvp.Key] = itemRes;
                    try
                    {
                        setPrep.Invoke(onlineApp, new object[] { kvp.Key, kvp.Value ?? string.Empty });
                    }
                    catch (Exception ex)
                    {
                        itemRes.error = ex.InnerException?.Message ?? ex.Message;
                        result.failedCount++;
                    }
                }

                // Phase 2: Atomic commit to PLC cycle
                try
                {
                    if (force)
                    {
                        _cachedForcePreparedValuesMethod?.Invoke(onlineApp, null);
                        CodesysContext.LogToIde($"[FORCE] batch_write_variables: Forced {resolvedVars.Count} prepared values.");
                    }
                    else
                    {
                        _cachedWritePreparedValuesMethod?.Invoke(onlineApp, null);
                        CodesysContext.LogToIde($"[WRITE] batch_write_variables: Wrote {resolvedVars.Count} prepared values atomically.");
                    }
                }
                catch (Exception ex)
                {
                    result.success = false;
                    result.message = "Failed to commit prepared values: " + (ex.InnerException?.Message ?? ex.Message);
                    return result;
                }

                // Phase 3: Round-based batch verification using batch read_values when available
                var readValMethod = _cachedReadValueMethod;
                var readValuesMethod = _cachedReadValuesArrayMethod;
                var pendingKeys = new List<string>();
                foreach (var kvp in resolvedVars)
                {
                    var item = result.details[kvp.Key];
                    if (item.error != null) continue;

                    if (verify && (readValuesMethod != null || readValMethod != null))
                    {
                        pendingKeys.Add(kvp.Key);
                    }
                    else
                    {
                        item.verified = true;
                        result.succeededCount++;
                    }
                }

                if (verify && (readValuesMethod != null || readValMethod != null) && pendingKeys.Count > 0)
                {
                    int maxRounds = 1 + Math.Max(0, BatchVerifyMaxRetries);
                    for (int round = 0; round < maxRounds && pendingKeys.Count > 0; round++)
                    {
                        if (round > 0 && BatchVerifyRetryDelayMs > 0)
                        {
                            Thread.Sleep(BatchVerifyRetryDelayMs);
                        }

                        var nextPending = new List<string>();
                        var staleInRound = new List<string>();
                        if (TryBatchReadRawValues(onlineApp, pendingKeys, out var batchVals))
                        {
                            for (int i = 0; i < pendingKeys.Count; i++)
                            {
                                string key = pendingKeys[i];
                                var item = result.details[key];
                                string actual = batchVals[i];
                                item.actual = actual;
                                item.verified = ValuesMatch(actual, resolvedVars[key]);
                                item.error = null;
                                if (!item.verified)
                                {
                                    nextPending.Add(key);
                                    if (IsStaleVarRefError(actual)) staleInRound.Add(key);
                                }
                            }
                        }
                        else
                        {
                            foreach (var key in pendingKeys)
                            {
                                var item = result.details[key];
                                string targetVal = resolvedVars[key];
                                try
                                {
                                    var actual = readValMethod.Invoke(onlineApp, new object[] { key })?.ToString();
                                    item.actual = actual;
                                    item.verified = ValuesMatch(actual, targetVal);
                                    item.error = null;
                                    if (!item.verified)
                                    {
                                        nextPending.Add(key);
                                        if (IsStaleVarRefError(actual)) staleInRound.Add(key);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    item.error = "Readback failed: " + (ex.InnerException?.Message ?? ex.Message);
                                    nextPending.Add(key);
                                    if (IsStaleVarRefError(item.error)) staleInRound.Add(key);
                                }
                            }
                        }
                        if (staleInRound.Count > 0 && round + 1 < maxRounds)
                        {
                            TryEvictOnlineVarRefs(onlineApp, staleInRound);
                        }
                        pendingKeys = nextPending;
                    }

                    foreach (var kvp in resolvedVars)
                    {
                        var item = result.details[kvp.Key];
                        if (item.error == null && item.verified)
                        {
                            result.succeededCount++;
                        }
                        else if (item.error == null && !item.verified)
                        {
                            result.failedCount++;
                        }
                        else if (item.error != null && item.error.StartsWith("Readback failed:", StringComparison.Ordinal))
                        {
                            result.failedCount++;
                        }
                    }
                }

                result.allVerified = (result.failedCount == 0);
                result.success = (result.failedCount == 0);
                result.message = $"Batch write complete: {result.succeededCount}/{result.totalCount} succeeded, {result.failedCount} failed.";
                return result;
            });
        }

        public static BatchWriteResult WriteStruct(string structName, object values, bool verify = true, bool force = false)
        {
            if (string.IsNullOrEmpty(structName))
                throw new ArgumentException("Parameter 'structName' is required.");

            var flatMap = new Dictionary<string, string>();
            FlattenObject(structName, values, flatMap);

            if (flatMap.Count == 0)
            {
                return new BatchWriteResult
                {
                    success = true,
                    message = $"No member variables extracted from '{structName}'."
                };
            }

            return BatchWriteVariables(flatMap, verify, force);
        }

        public static ReadArrayResult ReadArray(string arrayName, int startIndex, int count)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var result = new ReadArrayResult
                {
                    variable = arrayName,
                    startIndex = startIndex,
                    count = count
                };

                if (string.IsNullOrEmpty(arrayName))
                {
                    result.success = false;
                    result.message = "Parameter 'arrayName' is required.";
                    return result;
                }

                if (count <= 0)
                {
                    result.success = true;
                    result.message = "Count is 0 or negative, returning empty array.";
                    return result;
                }

                if (count > 1000)
                {
                    count = 1000;
                    result.count = count;
                }

                var expressions = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    expressions.Add($"{arrayName}[{startIndex + i}]");
                }

                var readResult = ReadVariables(expressions);
                result.success = readResult.success;
                result.items = new List<string>(count);

                for (int i = 0; i < count; i++)
                {
                    string expr = expressions[i];
                    string val = null;
                    if (readResult.values != null && readResult.values.TryGetValue(expr, out val))
                    {
                        result.items.Add(NormalizeIecValue(val));
                    }
                    else
                    {
                        result.items.Add(string.Empty);
                    }
                }

                result.message = $"Successfully read {result.items.Count} array element(s) from '{arrayName}'.";
                return result;
            });
        }

        public static ReadStructResult ReadStruct(string structName, List<string> memberNames = null)
        {
            return CodesysContext.RunOnPrimaryThread(() =>
            {
                var result = new ReadStructResult
                {
                    variable = structName
                };

                if (string.IsNullOrEmpty(structName))
                {
                    result.success = false;
                    result.message = "Parameter 'structName' is required.";
                    return result;
                }

                var onlineApp = GetOrCreateOnlineApp();
                EnsureOnlineMethodsCached(onlineApp);

                // 1. Discover DUT members first and batch-read in one roundtrip (avoids slow Invalid pointer size error on struct read_value)
                if (memberNames == null || memberNames.Count == 0)
                {
                    memberNames = GetDutMembers(structName);
                }

                if (memberNames != null && memberNames.Count > 0)
                {
                    var exprs = new List<string>(memberNames.Count);
                    foreach (var m in memberNames)
                    {
                        exprs.Add($"{structName}.{m}");
                    }

                    var readRes = ReadVariables(exprs);
                    foreach (var m in memberNames)
                    {
                        string expr = $"{structName}.{m}";
                        if (readRes.values != null && readRes.values.TryGetValue(expr, out string val))
                        {
                            result.values[m] = NormalizeIecValue(val);
                        }
                    }

                    result.success = true;
                    result.message = $"Successfully read struct '{structName}' with {result.values.Count} member(s).";
                    return result;
                }

                // 2. Fallback: direct read_value tuple parsing only when DUT members are unavailable
                var readValMethod = _cachedReadValueMethod;
                if (readValMethod != null)
                {
                    try
                    {
                        var rawVal = readValMethod.Invoke(onlineApp, new object[] { structName })?.ToString();
                        result.rawText = rawVal;
                        if (!string.IsNullOrEmpty(rawVal) && rawVal.Trim().StartsWith("(") && rawVal.Trim().EndsWith(")"))
                        {
                            var parsed = ParseIecStructString(rawVal);
                            if (parsed.Count > 0)
                            {
                                result.values = parsed;
                                result.success = true;
                                result.message = $"Successfully read struct '{structName}' with {parsed.Count} field(s) via direct tuple.";
                                return result;
                            }
                        }
                    }
                    catch { }
                }

                result.success = false;
                result.message = $"Could not determine members for struct '{structName}'. Please provide memberNames or ensure DUT exists in project.";
                return result;
            });
        }
    }
}
