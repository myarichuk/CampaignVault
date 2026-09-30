using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>N6: plugin zips (layout, zip-slip, code vs data, id clashes), install/uninstall, the disabled list and the /plugins listing. Temp folders only.</summary>
    public class PluginPackagesTests
    {
        private const string Manifest = "{\"id\":\"com.example.lanterns\",\"displayName\":\"Lanterns\",\"version\":\"1.2.0\",\"author\":\"Scratch Author\",\"description\":\"Adds a lantern.\"}";
        private string _dir;
        private string _user;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cv-plugins-" + Guid.NewGuid().ToString("N"));
            _user = Path.Combine(_dir, "Plugins");
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        private string Zip(string name, params string[] pathsAndContents)
        {
            string path = Path.Combine(_dir, name);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                for (int i = 0; i < pathsAndContents.Length; i += 2)
                {
                    var entry = zip.CreateEntry(pathsAndContents[i]);
                    using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false))) { w.Write(pathsAndContents[i + 1]); }
                }
            }
            return path;
        }

        private string DataZip()
        {
            return Zip("lanterns.zip",
                "Lanterns/plugin.json", Manifest,
                "Lanterns/RulesetData/dnd5e/items/lantern.yaml", "name: Lantern of Tests\n",
                "__MACOSX/Lanterns/._plugin.json", "junk",
                "Lanterns/.DS_Store", "junk");
        }

        private static PluginZipInfo Inspect(string zip)
        {
            PluginZipInfo info;
            string error;
            Assert.IsTrue(PluginPackages.Inspect(zip, out info, out error), error);
            return info;
        }

        private static string InspectError(string zip)
        {
            PluginZipInfo info;
            string error;
            Assert.IsFalse(PluginPackages.Inspect(zip, out info, out error));
            Assert.IsNull(info);
            return error;
        }

        [Test]
        public void DataZip_InAFolder_IsDataOnly_AndReadsTheManifest()
        {
            var info = Inspect(DataZip());
            Assert.AreEqual("com.example.lanterns", info.Id);
            Assert.AreEqual("Lanterns", info.Name);
            Assert.AreEqual("1.2.0", info.Version);
            Assert.AreEqual("Scratch Author", info.Author);
            Assert.AreEqual("Lanterns/", info.Prefix);
            Assert.IsFalse(info.IsCode);
            Assert.AreEqual(2, info.FileCount, "macOS junk isn't counted");
        }

        [Test]
        public void ManifestAtTheTop_AndADll_IsACodePlugin_WithoutTheSdkCopy()
        {
            var info = Inspect(Zip("code.zip",
                "plugin.json", Manifest,
                "Lanterns.dll", "MZ",
                "CampaignVault.PluginSdk.dll", "MZ"));
            Assert.AreEqual(string.Empty, info.Prefix);
            Assert.IsTrue(info.IsCode);
            CollectionAssert.AreEqual(new[] { "Lanterns.dll" }, info.Dlls);
        }

        [Test]
        public void ZipSlip_AndAbsolutePaths_AreRefused()
        {
            string[] evil = { "../evil.txt", "Lanterns/../../evil.txt", "/etc/evil", "C:/evil.txt", "..\\evil.txt", "Lanterns\\..\\..\\evil.txt" };
            int i = 0;
            foreach (string name in evil)
            {
                string error = InspectError(Zip("slip" + (i++) + ".zip", "Lanterns/plugin.json", Manifest, name, "x"));
                StringAssert.Contains("unsafe path", error, name);
            }
            string relative;
            Assert.IsTrue(PluginPackages.TryNormalizeEntry("./Lanterns//RulesetData/./a.yaml", out relative));
            Assert.AreEqual("Lanterns/RulesetData/a.yaml", relative);
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(_dir), "evil.txt")));
        }

        [Test]
        public void BadLayouts_AreRefused_WithAReason()
        {
            StringAssert.Contains("No plugin.json", InspectError(Zip("none.zip", "Lanterns/readme.txt", "x")));
            StringAssert.Contains("No plugin.json", InspectError(Zip("deep.zip", "a/b/plugin.json", Manifest)));
            StringAssert.Contains("more than one", InspectError(Zip("two.zip", "A/plugin.json", Manifest, "B/plugin.json", Manifest)));
            StringAssert.Contains("outside the plugin folder", InspectError(Zip("stray.zip", "Lanterns/plugin.json", Manifest, "other/x.yaml", "x")));
            StringAssert.Contains("no \"id\"", InspectError(Zip("noid.zip", "plugin.json", "{\"version\":\"1.0.0\"}")));
            StringAssert.Contains("can only use", InspectError(Zip("badid.zip", "plugin.json", "{\"id\":\"../escape\"}")));
            StringAssert.Contains("valid JSON", InspectError(Zip("badjson.zip", "plugin.json", "{ nope")));
            File.WriteAllText(Path.Combine(_dir, "notazip.zip"), "hello");
            StringAssert.Contains("isn't a zip", InspectError(Path.Combine(_dir, "notazip.zip")));
            StringAssert.Contains("No file", InspectError(Path.Combine(_dir, "missing.zip")));
        }

        [Test]
        public void InstallBlocker_CatchesIdClashes_AndTooNewPlugins()
        {
            var info = Inspect(DataZip());
            Assert.IsNull(PluginPackages.InstallBlocker(info, new[] { "com.campaignvault.crafting" }, "0.11.0"));
            StringAssert.Contains("already installed", PluginPackages.InstallBlocker(info, new[] { "COM.EXAMPLE.LANTERNS" }, "0.11.0"));
            info.MinEngineVersion = "0.12.0";
            StringAssert.Contains("needs server 0.12.0", PluginPackages.InstallBlocker(info, new string[0], "0.11.0"));
            Assert.IsNull(PluginPackages.InstallBlocker(info, new string[0], "0.12.0"));
            Assert.IsNull(PluginPackages.InstallBlocker(info, new string[0], string.Empty), "unknown server version: the server decides");
        }

        [Test]
        public void Install_ExtractsIntoAFolderNamedById_ThenUninstallRemovesIt()
        {
            var info = Inspect(DataZip());
            string installed = PluginPackages.Install(info, _user);
            Assert.AreEqual(Path.Combine(_user, "com.example.lanterns"), installed);
            Assert.IsTrue(File.Exists(Path.Combine(installed, "plugin.json")));
            Assert.IsTrue(File.Exists(Path.Combine(installed, "RulesetData", "dnd5e", "items", "lantern.yaml")));
            Assert.IsFalse(File.Exists(Path.Combine(installed, ".DS_Store")));
            Assert.AreEqual(1, Directory.GetDirectories(_user).Length, "no staging folder left behind");

            var scanned = PluginPackages.ScanInstalled(_user);
            Assert.AreEqual(installed, scanned["com.example.lanterns"]);
            Assert.Throws<IOException>(delegate { PluginPackages.Install(info, _user); });

            string error;
            Assert.IsFalse(PluginPackages.Uninstall(_user, "com.example.other", out error));
            Assert.IsTrue(PluginPackages.Uninstall(_user, "com.example.lanterns", out error), error);
            Assert.IsFalse(Directory.Exists(installed));
        }

        [Test]
        public void DisabledFile_RoundTrips_AndFeedsTheServerEnv()
        {
            string file = Path.Combine(_dir, PluginPackages.DisabledFileName);
            Assert.AreEqual(string.Empty, PluginPackages.DisabledEnv(file));
            Assert.IsTrue(PluginPackages.SetDisabled(file, "b.two", true));
            Assert.IsTrue(PluginPackages.SetDisabled(file, "a.one", true));
            Assert.IsFalse(PluginPackages.SetDisabled(file, "A.ONE", true), "ids compare case-insensitively");
            Assert.AreEqual("a.one,b.two", PluginPackages.DisabledEnv(file));
            Assert.IsTrue(PluginPackages.SetDisabled(file, "a.one", false));
            CollectionAssert.AreEquivalent(new[] { "b.two" }, new List<string>(PluginPackages.ReadDisabled(file)));
        }

        [Test]
        public void Listing_ParsesTheServerPayload()
        {
            const string body = "{\"engineVersion\":\"0.11.0\",\"plugins\":[" +
                "{\"id\":\"com.example.lanterns\",\"name\":\"Lanterns\",\"version\":\"1.2.0\",\"author\":null,\"description\":\"Adds a lantern.\",\"kind\":\"data\",\"source\":\"user\",\"enabled\":true,\"loaded\":true,\"directory\":\"/x\",\"minEngineVersion\":null,\"systems\":[\"dnd5e\"],\"modeIds\":[],\"campaignOptions\":[{\"key\":\"lanternFuel\"}],\"errors\":[]}," +
                "{\"id\":\"com.example.future\",\"name\":\"\",\"version\":\"9.0.0\",\"kind\":\"code\",\"source\":\"bundled\",\"enabled\":false,\"loaded\":false,\"errors\":[\"Requires server 99.0.0\"]}]}";
            List<PluginEntry> plugins;
            string engine;
            Assert.IsTrue(PluginPackages.TryParseListing(body, out plugins, out engine));
            Assert.AreEqual("0.11.0", engine);
            Assert.AreEqual(2, plugins.Count);
            Assert.IsTrue(plugins[0].IsUser);
            Assert.IsFalse(plugins[0].IsCode);
            Assert.IsTrue(plugins[0].Loaded);
            Assert.AreEqual(string.Empty, plugins[0].Author);
            CollectionAssert.AreEqual(new[] { "dnd5e" }, plugins[0].Systems);
            CollectionAssert.AreEqual(new[] { "lanternFuel" }, plugins[0].OptionKeys);
            Assert.AreEqual("com.example.future", plugins[1].Name, "a blank name falls back to the id");
            Assert.IsTrue(plugins[1].IsCode);
            Assert.IsFalse(plugins[1].Enabled);
            Assert.AreEqual(1, plugins[1].Errors.Count);
            Assert.IsFalse(PluginPackages.TryParseListing("{\"status\":\"healthy\"}", out plugins, out engine));
        }

        [Test]
        public void Controller_InstallsTogglesAndUninstalls_UnderDataRoot()
        {
            var go = new GameObject("PluginControllerTest");
            try
            {
                var boot = go.AddComponent<VaultBootstrap>();
                boot.Initialize(new MemoryPrefs());
                boot.State.Server.DataRoot = _dir;
                boot.State.Config.ServerUrl = "http://127.0.0.1:5275";
                var controller = boot.Controller;
                Assert.IsTrue(controller.CanManagePlugins);
                Assert.AreEqual(Path.Combine(_dir, "Plugins"), boot.State.Server.UserPluginsDir);

                Assert.IsNull(controller.CheckPluginZip(Path.Combine(_dir, "missing.zip")));
                string zip = DataZip();
                var info = controller.CheckPluginZip("\"" + zip + "\"");
                Assert.IsNotNull(info);
                Assert.IsTrue(controller.InstallPlugin(info));
                Assert.IsTrue(controller.InstalledPlugins().ContainsKey("com.example.lanterns"));
                Assert.IsNull(controller.CheckPluginZip(zip), "second install of the same id is refused");

                // A bundled id the server reported also blocks an install.
                boot.State.Plugins.Add(new PluginEntry { Id = "com.example.bundled", Source = "bundled" });
                string bundledZip = Zip("bundled.zip", "plugin.json", "{\"id\":\"com.example.bundled\"}");
                Assert.IsNull(controller.CheckPluginZip(bundledZip));

                controller.SetPluginEnabled("com.example.lanterns", false);
                Assert.IsTrue(controller.IsPluginDisabled("com.example.lanterns"));
                StringAssert.Contains("com.example.lanterns", File.ReadAllText(boot.State.Server.DisabledPluginsFile));
                controller.SetPluginEnabled("com.example.lanterns", true);
                Assert.IsFalse(controller.IsPluginDisabled("com.example.lanterns"));

                controller.SetPluginEnabled("com.example.lanterns", false);
                Assert.IsTrue(controller.UninstallPlugin("com.example.lanterns"));
                Assert.IsFalse(controller.InstalledPlugins().ContainsKey("com.example.lanterns"));
                Assert.IsFalse(controller.IsPluginDisabled("com.example.lanterns"), "uninstall forgets the disabled entry");

                boot.State.Config.ServerUrl = "http://campaigns.example.net:5275";
                Assert.IsFalse(controller.CanManagePlugins, "a remote server's plugins aren't managed from here");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
