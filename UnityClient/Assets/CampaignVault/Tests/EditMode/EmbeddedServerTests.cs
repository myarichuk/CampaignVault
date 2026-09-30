using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>N5: ports, pidfile, payload copy, build guards, version handshake and the license file. Temp folders only.</summary>
    public class EmbeddedServerTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cv-embedded-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        [Test]
        public void Ports_BusyPortIsDetected_AndPickFallsBack()
        {
            var holder = new TcpListener(IPAddress.Loopback, 0);
            holder.Start();
            try
            {
                int busy = ((IPEndPoint)holder.LocalEndpoint).Port;
                Assert.IsFalse(EmbeddedServerSupport.IsPortFree(busy));
                Assert.AreEqual(4242, EmbeddedServerSupport.PickPort(busy, EmbeddedServerSupport.IsPortFree, delegate { return 4242; }));
            }
            finally { holder.Stop(); }
            int free = EmbeddedServerSupport.FreePort();
            Assert.IsTrue(EmbeddedServerSupport.IsPortFree(free));
            Assert.AreEqual(free, EmbeddedServerSupport.PickPort(free, EmbeddedServerSupport.IsPortFree, delegate { return 4242; }));
        }

        [Test]
        public void PidFile_RoundTrips_AndRejectsJunk()
        {
            int pid, port;
            string exe;
            Assert.IsTrue(EmbeddedServerSupport.TryParsePidFile(EmbeddedServerSupport.FormatPidFile(123, 5275, "/a b/CampaignVault"), out pid, out port, out exe));
            Assert.AreEqual(123, pid);
            Assert.AreEqual(5275, port);
            Assert.AreEqual("/a b/CampaignVault", exe);
            Assert.IsFalse(EmbeddedServerSupport.TryParsePidFile("abc\n1\n/x", out pid, out port, out exe));
            Assert.IsFalse(EmbeddedServerSupport.TryParsePidFile("12\n", out pid, out port, out exe));
            Assert.IsFalse(EmbeddedServerSupport.TryParsePidFile(null, out pid, out port, out exe));
        }

        [Test]
        public void KillOrphan_DeadPid_JustRemovesThePidFile()
        {
            string pidFile = Path.Combine(_dir, EmbeddedServerSupport.PidFileName);
            File.WriteAllText(pidFile, EmbeddedServerSupport.FormatPidFile(int.MaxValue - 7, 5275, "/nowhere/CampaignVault"));
            Assert.IsNull(EmbeddedServerSupport.KillOrphan(pidFile));
            Assert.IsFalse(File.Exists(pidFile));
            Assert.IsNull(EmbeddedServerSupport.KillOrphan(pidFile), "no pidfile: nothing to do");
        }

        [Test]
        public void KillOrphan_OnlyKillsOurBinary()
        {
            Assume.That(File.Exists("/bin/sleep"), "needs /bin/sleep (macOS/Linux)");
            string pidFile = Path.Combine(_dir, EmbeddedServerSupport.PidFileName);
            using (Process stranger = Process.Start(new ProcessStartInfo("/bin/sleep", "30") { UseShellExecute = false }))
            {
                try
                {
                    // Process.Start returns after fork: until exec the child still looks like Unity.
                    for (int i = 0; i < 40 && !IsNamed(stranger.Id, "sleep"); i++) { System.Threading.Thread.Sleep(50); }
                    Assume.That(IsNamed(stranger.Id, "sleep"), "child never showed up as sleep");

                    // A live process under a reused pid that isn't the server binary survives.
                    File.WriteAllText(pidFile, EmbeddedServerSupport.FormatPidFile(stranger.Id, 5275, "/nowhere/CampaignVault"));
                    Assert.IsNull(EmbeddedServerSupport.KillOrphan(pidFile));
                    Assert.IsFalse(stranger.HasExited);

                    File.WriteAllText(pidFile, EmbeddedServerSupport.FormatPidFile(stranger.Id, 5275, "/bin/sleep"));
                    StringAssert.Contains("Stopped a server", EmbeddedServerSupport.KillOrphan(pidFile));
                    Assert.IsTrue(stranger.WaitForExit(5000));
                    Assert.IsFalse(File.Exists(pidFile));
                }
                finally { if (!stranger.HasExited) { stranger.Kill(); } }
            }
        }

        private static bool IsNamed(int pid, string name)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid)) { return p.ProcessName == name; }
            }
            catch (Exception) { return false; }
        }

        [Test]
        public void CopyTree_CopiesNestedFiles_AndReportsProgressToTheTotal()
        {
            string src = Path.Combine(_dir, "src");
            Directory.CreateDirectory(Path.Combine(src, "models", "embedding"));
            File.WriteAllBytes(Path.Combine(src, "CampaignVault"), new byte[300]);
            File.WriteAllBytes(Path.Combine(src, "models", "embedding", "model.onnx"), new byte[700]);
            long total = EmbeddedServerSupport.DirectorySize(src);
            Assert.AreEqual(1000, total);
            long last = 0;
            int calls = 0;
            EmbeddedServerSupport.CopyTree(src, Path.Combine(_dir, "out"), total, delegate (long copied, long all) { Assert.GreaterOrEqual(copied, last); last = copied; calls++; Assert.AreEqual(1000, all); });
            Assert.AreEqual(1000, last);
            Assert.AreEqual(2, calls);
            Assert.AreEqual(700, new FileInfo(Path.Combine(_dir, "out", "models", "embedding", "model.onnx")).Length);
            long free = EmbeddedServerSupport.FreeBytes(_dir);
            Assert.IsTrue(free == -1 || free > 0);
        }

        [Test]
        public void IsLfsPointer_TellsStubFromModel()
        {
            string stub = Path.Combine(_dir, "stub.onnx");
            File.WriteAllText(stub, "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 90000000\n");
            string real = Path.Combine(_dir, "real.onnx");
            File.WriteAllBytes(real, new byte[] { 0x08, 0x07, 0x12, 0x00 });
            Assert.IsTrue(EmbeddedServerSupport.IsLfsPointer(stub));
            Assert.IsFalse(EmbeddedServerSupport.IsLfsPointer(real));
            Assert.IsFalse(EmbeddedServerSupport.IsLfsPointer(Path.Combine(_dir, "missing.onnx")));
        }

        [Test]
        public void EngineVersion_ParsedFromTheRealSource()
        {
            Assert.AreEqual("1.2.3", EmbeddedServerSupport.ParseEngineVersion("public const string Current = \"1.2.3\";"));
            Assert.IsNull(EmbeddedServerSupport.ParseEngineVersion("nothing here"));
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "src", "CampaignVault", "Plugins", "EngineVersion.cs"));
            Assume.That(File.Exists(path), path + " not in this checkout");
            StringAssert.IsMatch("^\\d+\\.\\d+\\.\\d+", EmbeddedServerSupport.ParseEngineVersion(File.ReadAllText(path)));
        }

        [Test]
        public void Health_ParsesVersion_AndWarnsOnMismatch()
        {
            string version;
            Assert.IsTrue(EmbeddedServerSupport.ParseHealth("{\"status\":\"healthy\",\"version\":\"0.11.0\"}", out version));
            Assert.AreEqual("0.11.0", version);
            Assert.IsTrue(EmbeddedServerSupport.ParseHealth("{\"status\":\"healthy\"}", out version));
            Assert.AreEqual(string.Empty, version);
            Assert.IsFalse(EmbeddedServerSupport.ParseHealth("{\"status\":\"starting\"}", out version));
            Assert.IsFalse(EmbeddedServerSupport.ParseHealth("<html>", out version));

            Assert.IsNull(EmbeddedServerSupport.VersionWarning("0.11.0", "0.11.0"));
            Assert.IsNull(EmbeddedServerSupport.VersionWarning(null, "0.9.0"), "a client that doesn't know its version never warns");
            StringAssert.Contains("0.10.0", EmbeddedServerSupport.VersionWarning("0.11.0", "0.10.0"));
            StringAssert.Contains("doesn't report", EmbeddedServerSupport.VersionWarning("0.11.0", string.Empty));
        }

        [Test]
        public void ValidateLicense_ChecksShape()
        {
            string name, error;
            Assert.IsTrue(EmbeddedServerSupport.ValidateLicense("{\"Id\":\"a1\",\"Name\":\"Scratch Table\",\"Keys\":[\"k1\",\"k2\"]}", out name, out error));
            Assert.AreEqual("Scratch Table", name);
            Assert.IsFalse(EmbeddedServerSupport.ValidateLicense("not json", out name, out error));
            StringAssert.Contains("braces", error);
            Assert.IsFalse(EmbeddedServerSupport.ValidateLicense("{\"Id\":\"a1\",\"Keys\":[]}", out name, out error));
            Assert.IsFalse(EmbeddedServerSupport.ValidateLicense("[1,2]", out name, out error));
        }

        [Test]
        public void Controller_SavesImportsAndRemovesTheLicense_UnderDataRoot()
        {
            var go = new GameObject("EmbeddedLicenseTest");
            try
            {
                var boot = go.AddComponent<VaultBootstrap>();
                boot.Initialize(new MemoryPrefs());
                boot.State.Server.DataRoot = _dir;
                string licensePath = Path.Combine(_dir, EmbeddedServerSupport.LicenseFileName);
                Assert.AreEqual(licensePath, boot.State.Server.LicensePath);
                Assert.IsNull(boot.Controller.RavenLicenseHolder());

                Assert.IsFalse(boot.Controller.SaveRavenLicense("{\"nope\":1}"));
                Assert.IsFalse(File.Exists(licensePath));

                Assert.IsTrue(boot.Controller.SaveRavenLicense("  {\"Id\":\"a1\",\"Name\":\"Scratch Table\",\"Keys\":[\"k\"]}  "));
                Assert.AreEqual("Scratch Table", boot.Controller.RavenLicenseHolder());

                string file = Path.Combine(_dir, "downloaded license.json");
                File.WriteAllText(file, "{\"Id\":\"b2\",\"Name\":\"From File\",\"Keys\":[\"k\"]}");
                Assert.IsTrue(boot.Controller.SaveRavenLicense("\"" + file + "\""));
                Assert.AreEqual("From File", boot.Controller.RavenLicenseHolder());

                boot.Controller.RemoveRavenLicense();
                Assert.IsFalse(File.Exists(licensePath));
                Assert.IsNull(boot.Controller.RavenLicenseHolder());
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
