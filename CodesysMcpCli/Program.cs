using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: AssemblyTitle("CodesysMcpCli")]
[assembly: AssemblyVersion("1.5.1.0")]
[assembly: AssemblyFileVersion("1.5.1.0")]

namespace CodesysMcpCli
{
    class Program
    {
        public const string DefaultPipeName = "codesys_mcp_pipe";
        private const string HeartbeatIdPrefix = "\"__cli_hb_";

        public static string ReadLineFromPipe(Stream stream)
        {
            var bytes = new List<byte>();
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            if (b == -1 && bytes.Count == 0) return null;
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static void WriteLineToPipe(Stream stream, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        public static bool TryExtractTopLevelJsonRpcId(string json, out string rawIdToken)
        {
            rawIdToken = null;
            if (string.IsNullOrWhiteSpace(json)) return false;

            int i = 0;
            int len = json.Length;
            SkipWhitespace(json, ref i);
            if (i >= len || json[i] != '{') return false;
            i++;

            while (i < len)
            {
                SkipWhitespace(json, ref i);
                if (i >= len || json[i] == '}') return false;
                if (json[i] == ',')
                {
                    i++;
                    continue;
                }
                if (json[i] != '"') return false;

                string key = ReadJsonStringLiteral(json, ref i);
                if (key == null) return false;

                SkipWhitespace(json, ref i);
                if (i >= len || json[i] != ':') return false;
                i++;
                SkipWhitespace(json, ref i);
                if (i >= len) return false;

                int valueStart = i;
                if (!SkipJsonValue(json, ref i)) return false;
                string token = json.Substring(valueStart, i - valueStart).Trim();

                if (string.Equals(key, "id", StringComparison.Ordinal))
                {
                    if (string.Equals(token, "null", StringComparison.Ordinal) || string.IsNullOrEmpty(token))
                    {
                        return false;
                    }
                    rawIdToken = token;
                    return true;
                }
            }

            return false;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }
        }

        private static bool SkipJsonString(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return false;
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '\\')
                {
                    if (i >= s.Length) return false;
                    i++;
                }
                else if (c == '"')
                {
                    return true;
                }
            }
            return false;
        }

        private static string ReadJsonStringLiteral(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') return null;
            int start = i + 1;
            if (!SkipJsonString(s, ref i)) return null;
            int rawLen = (i - 1) - start;
            string raw = rawLen > 0 ? s.Substring(start, rawLen) : string.Empty;
            return raw.IndexOf('\\') >= 0 ? raw.Replace("\\\"", "\"").Replace("\\\\", "\\") : raw;
        }

        private static bool SkipJsonValue(string s, ref int i)
        {
            if (i >= s.Length) return false;
            char first = s[i];
            if (first == '"')
            {
                return SkipJsonString(s, ref i);
            }
            if (first == '{' || first == '[')
            {
                int depth = 0;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '"')
                    {
                        if (!SkipJsonString(s, ref i)) return false;
                        continue;
                    }
                    if (c == '{' || c == '[')
                    {
                        depth++;
                    }
                    else if (c == '}' || c == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            i++;
                            return true;
                        }
                    }
                    i++;
                }
                return false;
            }

            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != ',' && s[i] != '}' && s[i] != ']')
            {
                i++;
            }
            return i > start;
        }

        static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            {
                Version ver = typeof(Program).Assembly.GetName().Version;
                string verStr = ver.Revision > 0 ? ver.ToString(4) : ver.ToString(3);
                Console.WriteLine("CodesysMcpCli v" + verStr);
                return 0;
            }

            string pipeName = ResolvePipeName(args);
            int maxAttempts = ResolveIntOption(args, "--attempts", "-a", 15, 1);
            int reconnectWaitMs = ResolveIntOption(args, "--reconnect-wait-ms", null, 3000, 0);
            int drainTimeoutMs = ResolveIntOption(args, "--drain-timeout-ms", null, 15000, 0);
            int heartbeatMs = ResolveHeartbeatMs(args);

            // Initial startup contract: fail fast with exit code 1 if CODESYS pipe is unreachable.
            NamedPipeClientStream pipeClient = ConnectToCodesysPipe(pipeName, maxAttempts);
            if (pipeClient == null)
            {
                Console.Error.WriteLine("[CodesysMcpCli] Error: Could not connect to CODESYS pipe: " + pipeName);
                return 1;
            }

            try
            {
                using (var session = new PipeBridgeSession(pipeName, pipeClient, reconnectWaitMs, heartbeatMs, drainTimeoutMs))
                {
                    session.Run();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[CodesysMcpCli] Pipe communication error: " + ex.Message);
                return 1;
            }

            return 0;
        }

        private sealed class PipeBridgeSession : IDisposable
        {
            private readonly string _pipeName;
            private readonly int _reconnectWaitMs;
            private readonly int _heartbeatMs;
            private readonly int _drainTimeoutMs;

            private readonly object _stateLock = new object();
            private readonly object _pendingLock = new object();
            private readonly object _stdoutLock = new object();
            private readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);

            private readonly Dictionary<string, int> _pendingRequestCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            private readonly ManualResetEventSlim _allDrainedEvent = new ManualResetEventSlim(true);
            private readonly ManualResetEventSlim _connectedEvent = new ManualResetEventSlim(true);
            private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

            private NamedPipeClientStream _activePipe;
            private CancellationTokenSource _connCts;
            private DateTime _lastActivityUtc;
            private string _pendingHeartbeatId;
            private DateTime _pendingHeartbeatSentUtc;
            private long _heartbeatCounter;
            private volatile bool _stdinEof;

            public PipeBridgeSession(
                string pipeName,
                NamedPipeClientStream initialPipe,
                int reconnectWaitMs,
                int heartbeatMs,
                int drainTimeoutMs)
            {
                _pipeName = pipeName;
                _activePipe = initialPipe;
                _connCts = new CancellationTokenSource();
                _reconnectWaitMs = reconnectWaitMs;
                _heartbeatMs = heartbeatMs;
                _drainTimeoutMs = drainTimeoutMs;
                _lastActivityUtc = DateTime.UtcNow;
            }

            public void Run()
            {
                Task supervisorTask = Task.Run(() => PipeSupervisorLoop());
                Task heartbeatTask = _heartbeatMs > 0
                    ? Task.Run(() => HeartbeatLoop())
                    : Task.FromResult(0);

                // Block on stdin until EOF, then wait for all in-flight request IDs to drain.
                RunStdinLoop();

                _shutdownCts.Cancel();
                ForceDisconnectCurrentPipe(quiet: true, failPendingRequests: false, reason: "Session closing");

                try { Task.WaitAll(new[] { supervisorTask, heartbeatTask }, 2000); }
                catch { }
            }

            private void RunStdinLoop()
            {
                try
                {
                    string inputLine;
                    while (!_shutdownCts.IsCancellationRequested && (inputLine = Console.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(inputLine)) continue;

                        string requestIdToken;
                        bool hasId = TryExtractTopLevelJsonRpcId(inputLine, out requestIdToken);
                        if (hasId)
                        {
                            TrackPendingRequest(requestIdToken);
                        }

                        if (!ForwardRequestWithReconnectWait(inputLine, requestIdToken))
                        {
                            if (hasId && CompletePendingRequest(requestIdToken))
                            {
                                WriteDisconnectErrorStdout(requestIdToken);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[CodesysMcpCli] Stdin reader error: " + ex.Message);
                }
                finally
                {
                    _stdinEof = true;
                    if (_drainTimeoutMs > 0)
                    {
                        try
                        {
                            _allDrainedEvent.Wait(_drainTimeoutMs, _shutdownCts.Token);
                        }
                        catch (OperationCanceledException) { }
                    }
                }
            }

            private bool ForwardRequestWithReconnectWait(string line, string retryingIdToken)
            {
                int waitMs = Math.Max(0, _reconnectWaitMs);
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMs);

                while (!_shutdownCts.IsCancellationRequested)
                {
                    if (TryWriteLineToActivePipe(line, updateActivity: true, retryingIdToken: retryingIdToken))
                    {
                        return true;
                    }

                    int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (remainingMs <= 0)
                    {
                        return false;
                    }

                    try
                    {
                        if (!_connectedEvent.Wait(remainingMs, _shutdownCts.Token))
                        {
                            return false;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return false;
                    }

                    if (TryWriteLineToActivePipe(line, updateActivity: true, retryingIdToken: retryingIdToken))
                    {
                        return true;
                    }

                    Thread.Sleep(25);
                }

                return false;
            }

            private bool TryWriteLineToActivePipe(string line, bool updateActivity, NamedPipeClientStream expectedPipe = null, string retryingIdToken = null)
            {
                NamedPipeClientStream pipe;
                lock (_stateLock)
                {
                    pipe = _activePipe;
                    if (pipe == null || (expectedPipe != null && !ReferenceEquals(pipe, expectedPipe)))
                    {
                        return false;
                    }
                }

                byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
                _writeSemaphore.Wait();
                try
                {
                    lock (_stateLock)
                    {
                        if (!ReferenceEquals(_activePipe, pipe))
                        {
                            return false;
                        }
                    }

                    pipe.Write(bytes, 0, bytes.Length);
                    pipe.Flush();

                    if (updateActivity)
                    {
                        lock (_stateLock)
                        {
                            if (ReferenceEquals(_activePipe, pipe))
                            {
                                _lastActivityUtc = DateTime.UtcNow;
                            }
                        }
                    }
                    return true;
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException)
                {
                    ForceDisconnectPipe(pipe, quiet: _stdinEof, failPendingRequests: !_shutdownCts.IsCancellationRequested, reason: "Pipe write failed: " + ex.Message, excludePendingId: retryingIdToken);
                    return false;
                }
                finally
                {
                    _writeSemaphore.Release();
                }
            }

            private void PipeSupervisorLoop()
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    NamedPipeClientStream pipe;
                    CancellationToken connToken;
                    lock (_stateLock)
                    {
                        pipe = _activePipe;
                        connToken = _connCts != null ? _connCts.Token : CancellationToken.None;
                    }

                    if (pipe != null)
                    {
                        PumpConnection(pipe, connToken);
                        bool exitingCleanly = _stdinEof && !HasPendingRequests();
                        ForceDisconnectPipe(pipe, quiet: exitingCleanly, failPendingRequests: !_shutdownCts.IsCancellationRequested, reason: "Named Pipe disconnected");
                    }

                    if (_shutdownCts.IsCancellationRequested || (_stdinEof && !HasPendingRequests()))
                    {
                        break;
                    }

                    if (!ReconnectWithBackoff())
                    {
                        break;
                    }
                }
            }

            private void PumpConnection(NamedPipeClientStream pipe, CancellationToken connToken)
            {
                byte[] buffer = new byte[4096];
                var lineBytes = new List<byte>();

                try
                {
                    while (!connToken.IsCancellationRequested && !_shutdownCts.IsCancellationRequested)
                    {
                        int bytesRead = pipe.ReadAsync(buffer, 0, buffer.Length, connToken).GetAwaiter().GetResult();
                        if (bytesRead == 0)
                        {
                            if (lineBytes.Count > 0)
                            {
                                string tail = Encoding.UTF8.GetString(lineBytes.ToArray());
                                lineBytes.Clear();
                                HandleServerLine(tail);
                            }
                            break;
                        }

                        for (int i = 0; i < bytesRead; i++)
                        {
                            byte b = buffer[i];
                            if (b == (byte)'\n')
                            {
                                string line = Encoding.UTF8.GetString(lineBytes.ToArray());
                                lineBytes.Clear();
                                HandleServerLine(line);
                            }
                            else if (b != (byte)'\r')
                            {
                                lineBytes.Add(b);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
                catch (Exception ex)
                {
                    if (!_shutdownCts.IsCancellationRequested)
                    {
                        Console.Error.WriteLine("[CodesysMcpCli] Pipe read error: " + ex.Message);
                    }
                }
            }

            private void HandleServerLine(string line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                lock (_stateLock)
                {
                    _lastActivityUtc = DateTime.UtcNow;
                }

                string idToken;
                bool hasId = TryExtractTopLevelJsonRpcId(line, out idToken);
                if (hasId && idToken.StartsWith(HeartbeatIdPrefix, StringComparison.Ordinal))
                {
                    lock (_stateLock)
                    {
                        if (string.Equals(_pendingHeartbeatId, idToken, StringComparison.Ordinal))
                        {
                            _pendingHeartbeatId = null;
                        }
                    }
                    return; // Intercept synthetic heartbeat pong before stdout
                }

                lock (_stdoutLock)
                {
                    Console.WriteLine(line);
                    Console.Out.Flush();
                }

                if (hasId)
                {
                    CompletePendingRequest(idToken);
                }
            }

            private bool ReconnectWithBackoff()
            {
                int[] backoffs = { 100, 150, 250, 500, 1000, 1500, 2000 };
                int idx = 0;

                while (!_shutdownCts.IsCancellationRequested && !(_stdinEof && !HasPendingRequests()))
                {
                    try
                    {
                        var candidate = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        candidate.Connect(100);

                        lock (_stateLock)
                        {
                            if (_shutdownCts.IsCancellationRequested)
                            {
                                try { candidate.Dispose(); } catch { }
                                return false;
                            }

                            _activePipe = candidate;
                            _connCts?.Dispose();
                            _connCts = new CancellationTokenSource();
                            _pendingHeartbeatId = null;
                            _lastActivityUtc = DateTime.UtcNow;
                            _connectedEvent.Set();
                        }

                        Console.Error.WriteLine("[CodesysMcpCli] Reconnected to CODESYS pipe: " + _pipeName);
                        return true;
                    }
                    catch
                    {
                        int delay = backoffs[Math.Min(idx, backoffs.Length - 1)];
                        idx++;
                        if (_shutdownCts.Token.WaitHandle.WaitOne(delay))
                        {
                            return false;
                        }
                    }
                }

                return false;
            }

            private void HeartbeatLoop()
            {
                int tickMs = Math.Max(50, Math.Min(500, _heartbeatMs / 2));
                TimeSpan idleInterval = TimeSpan.FromMilliseconds(_heartbeatMs);
                TimeSpan responseTimeout = TimeSpan.FromMilliseconds(Math.Max(400, Math.Min(3000, _heartbeatMs * 2)));

                while (!_shutdownCts.IsCancellationRequested && !_stdinEof)
                {
                    if (_shutdownCts.Token.WaitHandle.WaitOne(tickMs))
                    {
                        break;
                    }
                    if (_stdinEof) break;

                    NamedPipeClientStream pipeToPing = null;
                    NamedPipeClientStream stalledPipe = null;
                    string timedOutHbId = null;
                    string hbId = null;

                    lock (_stateLock)
                    {
                        if (_activePipe == null) continue;
                        DateTime now = DateTime.UtcNow;

                        if (_pendingHeartbeatId != null)
                        {
                            if (now - _pendingHeartbeatSentUtc >= responseTimeout)
                            {
                                stalledPipe = _activePipe;
                                timedOutHbId = _pendingHeartbeatId;
                            }
                        }
                        else if (now - _lastActivityUtc >= idleInterval && !HasPendingRequests())
                        {
                            long seq = Interlocked.Increment(ref _heartbeatCounter);
                            hbId = "__cli_hb_" + seq;
                            _pendingHeartbeatId = "\"" + hbId + "\"";
                            _pendingHeartbeatSentUtc = now;
                            pipeToPing = _activePipe;
                        }
                    }

                    if (stalledPipe != null)
                    {
                        ForceDisconnectPipe(stalledPipe, quiet: false, failPendingRequests: false, reason: "Heartbeat timeout (" + timedOutHbId + ")");
                        continue;
                    }

                    if (pipeToPing != null && hbId != null)
                    {
                        string hbJson = "{\"jsonrpc\":\"2.0\",\"id\":\"" + hbId + "\",\"method\":\"ping\"}";
                        if (!TryWriteLineToActivePipe(hbJson, updateActivity: false, expectedPipe: pipeToPing))
                        {
                            ForceDisconnectPipe(pipeToPing, quiet: _stdinEof, failPendingRequests: false, reason: "Heartbeat send failed");
                        }
                    }
                }
            }

            private void ForceDisconnectCurrentPipe(bool quiet, bool failPendingRequests, string reason)
            {
                NamedPipeClientStream pipe;
                lock (_stateLock)
                {
                    pipe = _activePipe;
                }
                if (pipe != null)
                {
                    ForceDisconnectPipe(pipe, quiet, failPendingRequests, reason);
                }
            }

            private void ForceDisconnectPipe(NamedPipeClientStream targetPipe, bool quiet, bool failPendingRequests, string reason, string excludePendingId = null)
            {
                if (targetPipe == null) return;

                CancellationTokenSource ctsToCancel = null;
                lock (_stateLock)
                {
                    if (!ReferenceEquals(_activePipe, targetPipe))
                    {
                        return;
                    }
                    _activePipe = null;
                    _pendingHeartbeatId = null;
                    _connectedEvent.Reset();
                    ctsToCancel = _connCts;
                    _connCts = null;
                }

                if (!quiet)
                {
                    Console.Error.WriteLine("[CodesysMcpCli] " + reason + "; reconnecting in background...");
                }

                if (failPendingRequests)
                {
                    DrainAllPendingAsDisconnected(excludePendingId);
                }

                try { ctsToCancel?.Cancel(); } catch { }
                try { targetPipe.Dispose(); } catch { }
                try { ctsToCancel?.Dispose(); } catch { }
            }

            private void TrackPendingRequest(string idToken)
            {
                lock (_pendingLock)
                {
                    int current;
                    _pendingRequestCounts.TryGetValue(idToken, out current);
                    _pendingRequestCounts[idToken] = current + 1;
                    _allDrainedEvent.Reset();
                }
            }

            private bool CompletePendingRequest(string idToken)
            {
                lock (_pendingLock)
                {
                    int current;
                    if (!_pendingRequestCounts.TryGetValue(idToken, out current))
                    {
                        return false;
                    }
                    if (current <= 1)
                    {
                        _pendingRequestCounts.Remove(idToken);
                    }
                    else
                    {
                        _pendingRequestCounts[idToken] = current - 1;
                    }
                    if (_pendingRequestCounts.Count == 0)
                    {
                        _allDrainedEvent.Set();
                    }
                    return true;
                }
            }

            private bool HasPendingRequests()
            {
                lock (_pendingLock)
                {
                    return _pendingRequestCounts.Count > 0;
                }
            }

            private void DrainAllPendingAsDisconnected(string excludePendingId = null)
            {
                var staleIds = new List<string>();
                lock (_pendingLock)
                {
                    if (_pendingRequestCounts.Count == 0) return;
                    foreach (var kv in _pendingRequestCounts)
                    {
                        if (!string.IsNullOrEmpty(excludePendingId) && string.Equals(kv.Key, excludePendingId, StringComparison.Ordinal))
                        {
                            continue;
                        }
                        staleIds.Add(kv.Key);
                    }
                    foreach (string k in staleIds)
                    {
                        _pendingRequestCounts.Remove(k);
                    }
                    if (_pendingRequestCounts.Count == 0)
                    {
                        _allDrainedEvent.Set();
                    }
                }

                foreach (string idToken in staleIds)
                {
                    WriteDisconnectErrorStdout(idToken);
                }
            }

            private void WriteDisconnectErrorStdout(string idToken)
            {
                string errJson = "{\"jsonrpc\":\"2.0\",\"id\":" + idToken +
                    ",\"error\":{\"code\":-32002,\"message\":\"CODESYS IDE Named Pipe disconnected (reconnecting in background). Please retry once CODESYS is ready.\"}}";
                lock (_stdoutLock)
                {
                    Console.WriteLine(errJson);
                    Console.Out.Flush();
                }
            }

            public void Dispose()
            {
                ForceDisconnectCurrentPipe(quiet: true, failPendingRequests: false, reason: "Disposed");
                _shutdownCts.Dispose();
                _connectedEvent.Dispose();
                _allDrainedEvent.Dispose();
                _writeSemaphore.Dispose();
            }
        }

        private static string ResolvePipeName(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--pipe" || args[i] == "-p")
                    {
                        return args[i + 1];
                    }
                }
            }

            string env = Environment.GetEnvironmentVariable("CODESYS_MCP_PIPE");
            if (!string.IsNullOrEmpty(env))
            {
                return env.Trim();
            }

            return DefaultPipeName;
        }

        private static int ResolveIntOption(string[] args, string longName, string shortName, int defaultValue, int minValue)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == longName || (shortName != null && args[i] == shortName))
                    {
                        int val;
                        if (int.TryParse(args[i + 1], out val) && val >= minValue)
                        {
                            return val;
                        }
                    }
                }
            }
            return defaultValue;
        }

        private static int ResolveHeartbeatMs(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--heartbeat-ms")
                    {
                        int val;
                        if (int.TryParse(args[i + 1], out val) && val >= 0)
                        {
                            return val;
                        }
                    }
                }
            }

            string env = Environment.GetEnvironmentVariable("CODESYS_MCP_HEARTBEAT_MS");
            int envVal;
            if (!string.IsNullOrEmpty(env) && int.TryParse(env.Trim(), out envVal) && envVal >= 0)
            {
                return envVal;
            }

            return 15000;
        }

        private static NamedPipeClientStream ConnectToCodesysPipe(string pipeName, int maxAttempts = 15)
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    client.Connect(1000);
                    return client;
                }
                catch
                {
                    if (attempt < maxAttempts)
                    {
                        Thread.Sleep(500);
                    }
                }
            }
            return null;
        }
    }
}
