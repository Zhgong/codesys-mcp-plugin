using System;
using System.Threading;

namespace CodesysMcp
{
    public static class ServerBootstrap
    {
        private static McpServer _server;

        public static void Start(object projects, object system, string pipeName = McpServer.DefaultPipeName)
        {
            try
            {
                Console.WriteLine("[CodesysMcp] Initializing context with projects and system...");
                CodesysContext.Initialize(projects, system);

                if (_server != null)
                {
                    _server.Stop();
                }

                _server = new McpServer(pipeName ?? McpServer.DefaultPipeName);
                _server.Start();
                Console.WriteLine($"[CodesysMcp] Stdio MCP Named Pipe server ready on: {pipeName ?? McpServer.DefaultPipeName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CodesysMcp] Failed to start Pipe MCP Server: {ex}");
                throw;
            }
        }

        public static void Stop()
        {
            if (_server != null)
            {
                _server.Stop();
                _server = null;
            }
        }
    }
}
