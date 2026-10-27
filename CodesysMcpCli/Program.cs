using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("CodesysMcpCli")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace CodesysMcpCli
{
    class Program
    {
        private const string PipeName = "codesys_mcp_pipe";

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

            // Auto-detect or connect to CODESYS Named Pipe
            NamedPipeClientStream pipeClient = ConnectToCodesysPipe();
            if (pipeClient == null)
            {
                Console.Error.WriteLine("[CodesysMcpCli] Error: Could not connect to CODESYS pipe: " + PipeName);
                return 1;
            }

            try
            {
                using (pipeClient)
                {
                    string inputLine;
                    while ((inputLine = Console.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(inputLine)) continue;

                        // Forward request line to CODESYS pipe
                        WriteLineToPipe(pipeClient, inputLine);

                        // Read response line from CODESYS pipe
                        string responseLine = ReadLineFromPipe(pipeClient);
                        if (responseLine != null)
                        {
                            Console.WriteLine(responseLine);
                            Console.Out.Flush();
                        }
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

        private static NamedPipeClientStream ConnectToCodesysPipe()
        {
            for (int attempt = 1; attempt <= 15; attempt++)
            {
                try
                {
                    var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                    client.Connect(1000);
                    return client;
                }
                catch
                {
                    Thread.Sleep(500);
                }
            }
            return null;
        }
    }
}
