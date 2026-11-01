using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;

namespace JVLinkServerTray.Tests
{
    // The tray EXE (bin\JVLinkServerTray.exe, built before the tests) must carry the application manifest:
    // a wrong namespace or a missing manifest makes Windows ignore the DPI setting without any error.
    public static class ManifestTests
    {
        const uint LoadAsDatafile = 0x2, LoadAsImageResource = 0x20;
        static readonly IntPtr RtManifest = new IntPtr(24), ProcessManifestId = new IntPtr(1);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll")]
        static extern bool FreeLibrary(IntPtr module);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LoadResource(IntPtr module, IntPtr res);
        [DllImport("kernel32.dll")]
        static extern IntPtr LockResource(IntPtr data);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint SizeofResource(IntPtr module, IntPtr res);

        static string TrayExe()
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Assembly.GetExecutingAssembly().Location)), "JVLinkServerTray.exe");
        }

        // The EXE's RT_MANIFEST #1, or null.
        static string Manifest(string exe)
        {
            IntPtr m = LoadLibraryEx(exe, IntPtr.Zero, LoadAsDatafile | LoadAsImageResource);
            if (m == IntPtr.Zero) throw new InvalidOperationException("cannot load " + exe + " (" + Marshal.GetLastWin32Error() + ")");
            try
            {
                IntPtr res = FindResource(m, ProcessManifestId, RtManifest);
                if (res == IntPtr.Zero) return null;
                uint size = SizeofResource(m, res);
                IntPtr data = LockResource(LoadResource(m, res));
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, (int)size);
                return new UTF8Encoding(false).GetString(bytes).TrimStart('﻿');
            }
            finally
            {
                FreeLibrary(m);
            }
        }

        static string Text(XmlDocument doc, XmlNamespaceManager ns, string xpath)
        {
            XmlNode n = doc.SelectSingleNode(xpath, ns);
            return n == null ? null : n.InnerText.Trim();
        }

        [Test] public static void Manifest_trayExe_isPerMonitorDpiAware() {
            string exe = TrayExe();
            Assert.True(File.Exists(exe), "built tray EXE " + exe);
            string text = Manifest(exe);
            Assert.NotNull(text, "the EXE has a manifest");
            var doc = new XmlDocument();
            doc.LoadXml(text);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("v1", "urn:schemas-microsoft-com:asm.v1");
            ns.AddNamespace("v3", "urn:schemas-microsoft-com:asm.v3");
            ns.AddNamespace("ws2005", "http://schemas.microsoft.com/SMI/2005/WindowsSettings");
            ns.AddNamespace("ws2016", "http://schemas.microsoft.com/SMI/2016/WindowsSettings");
            ns.AddNamespace("compat", "urn:schemas-microsoft-com:compatibility.v1");
            ns.AddNamespace("v2", "urn:schemas-microsoft-com:asm.v2");
            Assert.Equal("true/pm", Text(doc, ns, "/v1:assembly/v3:application/v3:windowsSettings/ws2005:dpiAware"), "dpiAware (2005 namespace)");
            Assert.Equal("PerMonitorV2, PerMonitor", Text(doc, ns, "/v1:assembly/v3:application/v3:windowsSettings/ws2016:dpiAwareness"), "dpiAwareness (2016 namespace)");
            XmlNode cc = doc.SelectSingleNode("/v1:assembly/v1:dependency/v1:dependentAssembly/v1:assemblyIdentity[@name='Microsoft.Windows.Common-Controls']", ns);
            Assert.NotNull(cc, "Common Controls dependency");
            Assert.Equal("6.0.0.0", cc.Attributes["version"].Value, "Common Controls version");
            Assert.NotNull(doc.SelectSingleNode("/v1:assembly/compat:compatibility/compat:application/compat:supportedOS[@Id='{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}']", ns), "supportedOS Windows 10");
            XmlNode level = doc.SelectSingleNode("//v2:requestedExecutionLevel | //v3:requestedExecutionLevel", ns);
            Assert.NotNull(level, "requestedExecutionLevel");
            Assert.Equal("asInvoker", level.Attributes["level"].Value, "runs as the invoker");
        }
    }
}
