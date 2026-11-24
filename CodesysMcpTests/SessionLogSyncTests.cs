using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class SessionLogSyncTests
    {
        [TestMethod]
        public void TestLogToIdeWritesToSessionLog()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), "codesys_mcp_test_" + Guid.NewGuid().ToString("N") + ".log");
            Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", tempLog);

            try
            {
                string testMsg = "[REQUEST] MCP Tool 'get_online_status' invoked";
                CodesysContext.LogToIde(testMsg);

                Assert.IsTrue(File.Exists(tempLog), "Log file should be created by LogToIde");
                string content = File.ReadAllText(tempLog);
                Assert.IsTrue(content.Contains("[CodesysMcp] " + testMsg), "Log content should contain prefixed test message");
                Assert.IsTrue(content.Contains(DateTime.Now.ToString("yyyy-MM-dd")), "Log entry should contain today's date timestamp");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", null);
                if (File.Exists(tempLog))
                {
                    try { File.Delete(tempLog); } catch { }
                }
            }
        }

        [TestMethod]
        public void TestLogToIdeMultiThreadedConcurrency()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), "codesys_mcp_concurrent_" + Guid.NewGuid().ToString("N") + ".log");
            Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", tempLog);

            try
            {
                int threadCount = 10;
                int messagesPerThread = 20;
                var threads = new List<Thread>();
                var exceptions = new List<Exception>();

                for (int t = 0; t < threadCount; t++)
                {
                    int threadId = t;
                    var th = new Thread(() =>
                    {
                        try
                        {
                            for (int m = 0; m < messagesPerThread; m++)
                            {
                                CodesysContext.LogToIde($"[CONCURRENT] Thread {threadId} message {m}");
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (exceptions) { exceptions.Add(ex); }
                        }
                    });
                    threads.Add(th);
                }

                foreach (var th in threads) th.Start();
                foreach (var th in threads) th.Join();

                Assert.AreEqual(0, exceptions.Count, "No exceptions should occur during concurrent logging");
                Assert.IsTrue(File.Exists(tempLog), "Concurrent log file should exist");

                string[] lines = File.ReadAllLines(tempLog);
                Assert.AreEqual(threadCount * messagesPerThread, lines.Length, $"Log file must have exact line count {threadCount * messagesPerThread}");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", null);
                if (File.Exists(tempLog))
                {
                    try { File.Delete(tempLog); } catch { }
                }
            }
        }

        [TestMethod]
        public void TestLogToIdeHandlesUnwritablePathGracefully()
        {
            // Invalid path with illegal characters
            Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", ":::INVALID:::path*?.log");

            try
            {
                // Must not throw any exception
                CodesysContext.LogToIde("[TEST] Should not throw despite invalid log path");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", null);
            }
        }

        [TestMethod]
        public void TestLogFileRollOverAtSizeLimit()
        {
            string tempLog = Path.Combine(Path.GetTempPath(), "codesys_mcp_rollover_" + Guid.NewGuid().ToString("N") + ".log");
            string oldLog = tempLog + ".old";
            Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", tempLog);

            try
            {
                // Create a pre-existing log file slightly exceeding 10MB limit
                using (var fs = new FileStream(tempLog, FileMode.Create, FileAccess.Write))
                {
                    fs.SetLength(10 * 1024 * 1024 + 100); // 10MB + 100 bytes
                }

                CodesysContext.LogToIde("[NEW_SESSION] Message after rollover");

                Assert.IsTrue(File.Exists(oldLog), "Rollover should create .old archived file");
                Assert.IsTrue(File.Exists(tempLog), "Active log file should still exist");

                var oldInfo = new FileInfo(oldLog);
                Assert.IsTrue(oldInfo.Length >= 10 * 1024 * 1024, "Old log should retain archived data");

                var newInfo = new FileInfo(tempLog);
                Assert.IsTrue(newInfo.Length < 1024 * 100, "New active log should be small and reset");

                string newContent = File.ReadAllText(tempLog);
                Assert.IsTrue(newContent.Contains("[NEW_SESSION] Message after rollover"), "New log should contain the new message");
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_MCP_LOG_FILE", null);
                if (File.Exists(tempLog)) { try { File.Delete(tempLog); } catch { } }
                if (File.Exists(oldLog)) { try { File.Delete(oldLog); } catch { } }
            }
        }
    }
}
