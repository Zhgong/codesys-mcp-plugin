using System;
using CodesysMcp;

namespace CodesysMcpTests
{
    public class DeviceLogonCredentialTests
    {
        [TestMethod]
        public void TestResolveDeviceCredentials_DefaultsToAdminEmptyPassword()
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
                Assert.AreEqual(string.Empty, creds.Password, "Default password should be empty when not configured");
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

            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmPromptDialog("#32770", "CODESYS"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmPromptDialog("WindowsForms10.Window.8.app.0", "CODESYS"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmPromptDialog("#32770", "Other App"));
        }

        [TestMethod]
        public void TestWatchdogConfirmButtonCaptionMatching()
        {
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("是(&Y)"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("&Yes"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("Yes"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("确定"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("确定(&O)"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("OK"));
            Assert.IsTrue(DeviceLogonWatchdog.IsConfirmButtonCaption("&OK"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption("否(&N)"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption("No"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption("Cancel"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption("取消"));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption(""));
            Assert.IsFalse(DeviceLogonWatchdog.IsConfirmButtonCaption(null));
        }
    }
}
