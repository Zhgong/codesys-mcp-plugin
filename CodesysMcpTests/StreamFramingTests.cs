using System;
using System.IO;
using System.Text;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class StreamFramingTests
    {
        [TestMethod]
        public void TestReadLineWithStandardLf()
        {
            byte[] data = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n");
            using (var ms = new MemoryStream(data))
            {
                string line = McpServer.ReadLineFromPipe(ms);
                Assert.AreEqual("{\"jsonrpc\":\"2.0\"}", line);
            }
        }

        [TestMethod]
        public void TestReadLineWithWindowsCrLf()
        {
            byte[] data = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\r\n");
            using (var ms = new MemoryStream(data))
            {
                string line = McpServer.ReadLineFromPipe(ms);
                Assert.AreEqual("{\"jsonrpc\":\"2.0\"}", line, "Carriage return \\r must be stripped");
            }
        }

        [TestMethod]
        public void TestReadLineWithUtf8ChineseCharacters()
        {
            string original = "{\"method\":\"write_pou\",\"comment\":\"// 测试中文注释与伺服压力机控制\"}";
            byte[] data = Encoding.UTF8.GetBytes(original + "\n");
            using (var ms = new MemoryStream(data))
            {
                string line = McpServer.ReadLineFromPipe(ms);
                Assert.AreEqual(original, line, "UTF-8 multi-byte characters must be preserved without corruption");
            }
        }

        [TestMethod]
        public void TestWriteLineToPipeAppendsNewline()
        {
            string payload = "{\"result\":\"ok\"}";
            using (var ms = new MemoryStream())
            {
                McpServer.WriteLineToPipe(ms, payload);
                ms.Position = 0;

                byte[] buffer = ms.ToArray();
                string output = Encoding.UTF8.GetString(buffer);
                Assert.AreEqual(payload + "\n", output);
            }
        }

        [TestMethod]
        public void TestEmptyStreamReturnsNull()
        {
            using (var ms = new MemoryStream(new byte[0]))
            {
                string line = McpServer.ReadLineFromPipe(ms);
                Assert.IsNull(line, "Reading from empty stream should return null");
            }
        }

        [TestMethod]
        public void TestMultipleSequentialLines()
        {
            string text = "Line 1\nLine 2\r\nLine 3\n";
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                Assert.AreEqual("Line 1", McpServer.ReadLineFromPipe(ms));
                Assert.AreEqual("Line 2", McpServer.ReadLineFromPipe(ms));
                Assert.AreEqual("Line 3", McpServer.ReadLineFromPipe(ms));
                Assert.IsNull(McpServer.ReadLineFromPipe(ms));
            }
        }
    }
}
