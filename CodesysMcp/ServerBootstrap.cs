using System;
using System.Threading;

namespace CodesysMcp
{
    public static class ServerBootstrap
    {
        private static McpServer _server;

        public static string BuildStartupBanner(string pipeName = null)
        {
            string ver = CodesysIdeUiHelper.GetServerVersion();
            int toolCount = McpServer.RegisteredToolCount;
            return $"[CODESYS MCP] Native Stdio/Pipe MCP Server v{ver} ready on \\\\.\\pipe\\{pipeName ?? McpServer.DefaultPipeName} ({toolCount} Tools Available)";
        }

        public static void Start(object projects, object system, string pipeName = McpServer.DefaultPipeName)
        {
            Start(projects, system, null, pipeName);
        }

        public static void Start(object projects, object system, object online, string pipeName = McpServer.DefaultPipeName)
        {
            try
            {
                Console.WriteLine("[CodesysMcp] Initializing context with projects, system, and online...");
                CodesysContext.Initialize(projects, system, online);

                if (_server != null)
                {
                    _server.Stop();
                }

                _server = new McpServer(pipeName ?? McpServer.DefaultPipeName);
                _server.Start();
                string banner = BuildStartupBanner(pipeName);
                Console.WriteLine(banner);
                CodesysContext.LogToIde(banner);

                try
                {
                    CodesysIdeUiHelper.InstallStatusIndicator(pipeName ?? McpServer.DefaultPipeName);
                }
                catch { }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CodesysMcp] Failed to start Pipe MCP Server: {ex}");
                throw;
            }
        }

        public static void Stop()
        {
            try
            {
                CodesysIdeUiHelper.RemoveStatusIndicator();
            }
            catch { }

            if (_server != null)
            {
                _server.Stop();
                _server = null;
            }
        }
    }
}
