using System;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class DeviceLogonCredentialTests
    {
        [TestMethod]
        public void TestResolveDeviceCredentials_DefaultsToAdmin1111()
        {
            string origUser = Environment.GetEnvironmentVariable("CODESYS_DEVICE_USER");
            string origPass = Environment.GetEnvironmentVariable("CODESYS_DEVICE_PASSWORD");
            Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", null);
            Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", null);

            try
            {
                var creds = CodesysOnlineHelper.ResolveDeviceCredentials(null, null);
                Assert.IsNotNull(creds);
                Assert.AreEqual("admin", creds.Username, "Default username should be 'admin'");
                Assert.AreEqual("1111", creds.Password, "Default password should be '1111'");
                Assert.AreEqual("Default", creds.Source);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", origUser);
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", origPass);
            }
        }

        [TestMethod]
        public void TestResolveDeviceCredentials_RespectsEnvironmentVariables()
        {
            string origUser = Environment.GetEnvironmentVariable("CODESYS_DEVICE_USER");
            string origPass = Environment.GetEnvironmentVariable("CODESYS_DEVICE_PASSWORD");

            try
            {
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", "operator_env");
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", "Pass999");

                var creds = CodesysOnlineHelper.ResolveDeviceCredentials(null, null);
                Assert.IsNotNull(creds);
                Assert.AreEqual("operator_env", creds.Username);
                Assert.AreEqual("Pass999", creds.Password);
                Assert.AreEqual("Environment", creds.Source);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", origUser);
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", origPass);
            }
        }

        [TestMethod]
        public void TestResolveDeviceCredentials_ExplicitArgumentsOverride()
        {
            string origUser = Environment.GetEnvironmentVariable("CODESYS_DEVICE_USER");
            string origPass = Environment.GetEnvironmentVariable("CODESYS_DEVICE_PASSWORD");

            try
            {
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", "env_user");
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", "env_pass");

                var creds = CodesysOnlineHelper.ResolveDeviceCredentials("explicit_user", "explicit_pass");
                Assert.IsNotNull(creds);
                Assert.AreEqual("explicit_user", creds.Username);
                Assert.AreEqual("explicit_pass", creds.Password);
                Assert.AreEqual("Explicit", creds.Source);
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_USER", origUser);
                Environment.SetEnvironmentVariable("CODESYS_DEVICE_PASSWORD", origPass);
            }
        }

        [TestMethod]
        public void TestWatchdogMatchesDeviceLogonDialog()
        {
            Assert.IsTrue(DeviceLogonWatchdog.IsDeviceLogonTitle("Device User Logon"));
            Assert.IsTrue(DeviceLogonWatchdog.IsDeviceLogonTitle("device user logon"));
            Assert.IsTrue(DeviceLogonWatchdog.IsDeviceLogonTitle("Device Logon"));
            Assert.IsTrue(DeviceLogonWatchdog.IsDeviceLogonTitle("User Logon (Device)"));
            Assert.IsFalse(DeviceLogonWatchdog.IsDeviceLogonTitle("CODESYS MCP Server Diagnostics"));
            Assert.IsFalse(DeviceLogonWatchdog.IsDeviceLogonTitle("MainForm"));
            Assert.IsFalse(DeviceLogonWatchdog.IsDeviceLogonTitle(""));
            Assert.IsFalse(DeviceLogonWatchdog.IsDeviceLogonTitle(null));
        }
    }
}
