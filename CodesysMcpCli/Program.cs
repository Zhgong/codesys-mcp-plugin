using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: AssemblyTitle("CodesysMcpCli")]
[assembly: AssemblyVersion("1.2.5.0")]
[assembly: AssemblyFileVersion("1.2.5.0")]

namespace CodesysMcpCli
{
    class Program
    {
        public const string DefaultPipeName = "codesys_mcp_pipe";

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

        static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
            {
                Console.WriteLine("CodesysMcpCli v" + typeof(Program).Assembly.GetName().Version.ToString(3));
                return 0;
            }

            string pipeName = ResolvePipeName(args);
            int maxAttempts = ResolveMaxAttempts(args);

            // Auto-detect or connect to CODESYS Named Pipe
            NamedPipeClientStream pipeClient = ConnectToCodesysPipe(pipeName, maxAttempts);
            if (pipeClient == null)
            {
                Console.Error.WriteLine("[CodesysMcpCli] Error: Could not connect to CODESYS pipe: " + pipeName);
                return 1;
            }

            try
            {
                using (pipeClient)
                using (var cts = new CancellationTokenSource())
                {
                    Task stdinTask = RunStdinToPipeAsync(pipeClient, cts.Token);
                    Task pipeTask = PumpPipeToStdoutAsync(pipeClient, cts.Token);

                    Task completed = Task.WhenAny(stdinTask, pipeTask).GetAwaiter().GetResult();
                    cts.Cancel();

                    // Propagate unexpected pipe errors if the completed task faulted
                    if (completed.IsFaulted && completed.Exception != null)
                    {
                        throw completed.Exception.GetBaseException();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[CodesysMcpCli] Pipe communication error: " + ex.Message);
                return 1;
            }

            return 0;
        }

        private static Task RunStdinToPipeAsync(NamedPipeClientStream pipeClient, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<object>();
            var thread = new Thread(() =>
            {
                try
                {
                    string inputLine;
                    while (!token.IsCancellationRequested && (inputLine = Console.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(inputLine)) continue;
                        byte[] bytes = Encoding.UTF8.GetBytes(inputLine + "\n");
                        pipeClient.WriteAsync(bytes, 0, bytes.Length, token).GetAwaiter().GetResult();
                        pipeClient.Flush();
                    }
                    tcs.TrySetResult(null);
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(null);
                }
                catch (ObjectDisposedException)
                {
                    tcs.TrySetResult(null);
                }
                catch (IOException)
                {
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            thread.IsBackground = true;
            thread.Start();
            return tcs.Task;
        }

        private static async Task PumpPipeToStdoutAsync(NamedPipeClientStream pipeClient, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            var lineBytes = new List<byte>();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await pipeClient.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        // Flush any trailing partial line before exiting on pipe EOF
                        if (lineBytes.Count > 0)
                        {
                            string tail = Encoding.UTF8.GetString(lineBytes.ToArray());
                            Console.WriteLine(tail);
                            Console.Out.Flush();
                            lineBytes.Clear();
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
                            Console.WriteLine(line);
                            Console.Out.Flush();
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

        private static int ResolveMaxAttempts(string[] args)
        {
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--attempts" || args[i] == "-a")
                    {
                        int val;
                        if (int.TryParse(args[i + 1], out val) && val > 0)
                        {
                            return val;
                        }
                    }
                }
            }
            return 15;
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
