using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Compilation-step embed: before every desktop player build, publish the
    /// CampaignVault server self-contained for the target RID and stage it
    /// (+ embedding models) under StreamingAssets, so the redistributable
    /// carries its own MCP server. Mirrors tools/embed-server.sh. WebGL builds
    /// skip with a warning (no subprocesses there).
    ///
    /// Only the target RID is staged (other RID folders are removed so a
    /// Windows build never carries the macOS payload), .pdb files and
    /// appsettings.Development.json are stripped, and the build fails when the
    /// embedding model is a git-lfs pointer stub. Server/version.txt records
    /// the server version for the client's /health handshake.
    /// </summary>
    public class ServerEmbedBuildProcessor : IPreprocessBuildWithReport
    {
        public int callbackOrder { get { return 0; } }

        public void OnPreprocessBuild(BuildReport report)
        {
            string rid = RidForPlatform(report.summary.platform);
            if (rid == null)
            {
                UnityEngine.Debug.LogWarning("[Vault] Embedded server skipped: this platform cannot launch subprocesses. Point the client at a hosted server instead.");
                return;
            }
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            CheckModel(repoRoot);

            string serverRoot = Path.Combine(repoRoot, "UnityClient", "Assets", "StreamingAssets", "CampaignVault", "Server");
            string output = Path.Combine(serverRoot, rid);
            if (HasArg(PrestagedArg))
            {
                UsePrestaged(serverRoot, rid);
                WriteVersionFile(repoRoot, serverRoot);
                AssetDatabase.Refresh();
                return;
            }
            if (Directory.Exists(serverRoot))
            {
                foreach (string dir in Directory.GetDirectories(serverRoot))
                {
                    // Stale files from an older publish (or another RID) would ship otherwise.
                    Directory.Delete(dir, true);
                    string meta = dir + ".meta";
                    if (File.Exists(meta)) { File.Delete(meta); }
                }
            }
            Directory.CreateDirectory(output);

            string target = Path.Combine(repoRoot, "src", "CampaignVault", "CampaignVault.csproj");
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "publish \"" + target + "\" -c Release -r " + rid + " --self-contained -o \"" + output + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using (Process publish = Process.Start(psi))
            {
                if (publish == null) { throw new BuildFailedException("[Vault] Could not start dotnet publish."); }
                string stdout = publish.StandardOutput.ReadToEnd();
                string stderr = publish.StandardError.ReadToEnd();
                publish.WaitForExit();
                UnityEngine.Debug.Log("[Vault] dotnet publish " + rid + " exit " + publish.ExitCode + ":\n" + stdout);
                if (publish.ExitCode != 0)
                {
                    throw new BuildFailedException("[Vault] Server embed failed for " + rid + ":\n" + stderr);
                }
            }
            int stripped = StripDevelopmentFiles(output);
            StageModels(repoRoot, output);
            WriteVersionFile(repoRoot, serverRoot);
            UnityEngine.Debug.Log("[Vault] Staged server " + rid + " (" + stripped + " debug/dev files stripped).");
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// CI builds inside the GameCI container, which has no dotnet SDK: the workflow runs
        /// tools/embed-server.sh on the host first and passes this flag, so the build only
        /// checks the staged server and drops other RIDs' payloads.
        /// </summary>
        public const string PrestagedArg = "-vaultPrestagedServer";

        private static bool HasArg(string name)
        {
            foreach (string arg in System.Environment.GetCommandLineArgs())
            {
                if (arg == name) { return true; }
            }
            return false;
        }

        private static void UsePrestaged(string serverRoot, string rid)
        {
            string output = Path.Combine(serverRoot, rid);
            string exe = Path.Combine(output, rid.StartsWith("win") ? "CampaignVault.exe" : "CampaignVault");
            if (!File.Exists(exe))
            {
                throw new BuildFailedException("[Vault] " + PrestagedArg + " was passed but " + exe + " is missing. Run tools/embed-server.sh " + rid + " first.");
            }
            foreach (string dir in Directory.GetDirectories(serverRoot))
            {
                if (Path.GetFileName(dir) == rid) { continue; }
                Directory.Delete(dir, true);
                string meta = dir + ".meta";
                if (File.Exists(meta)) { File.Delete(meta); }
            }
            UnityEngine.Debug.Log("[Vault] Using the pre-staged server " + rid + ".");
        }

        private static string RidForPlatform(BuildTarget platform)
        {
            if (platform == BuildTarget.StandaloneOSX)
            {
                // A universal build still gets the Apple Silicon server only; Intel Macs need an x64 build.
                return MacArchitecture.Get() == UnityEditor.Build.OSArchitecture.x64 ? "osx-x64" : "osx-arm64";
            }
            if (platform == BuildTarget.StandaloneWindows || platform == BuildTarget.StandaloneWindows64) { return "win-x64"; }
            if (platform == BuildTarget.StandaloneLinux64) { return "linux-x64"; }
            return null;
        }

        /// <summary>A clone without git-lfs has a pointer stub instead of the model; shipping it silently disables embeddings.</summary>
        private static void CheckModel(string repoRoot)
        {
            string model = Path.Combine(repoRoot, "models", "embedding", "model.onnx");
            if (!File.Exists(model))
            {
                throw new BuildFailedException("[Vault] models/embedding/model.onnx is missing. Install git-lfs and run `git lfs pull` before building.");
            }
            if (EmbeddedServerSupport.IsLfsPointer(model))
            {
                throw new BuildFailedException("[Vault] models/embedding/model.onnx is a git-lfs pointer, not the model. Install git-lfs and run `git lfs pull` before building.");
            }
        }

        /// <summary>Removes .pdb files and appsettings.Development.json from the published server; returns how many.</summary>
        public static int StripDevelopmentFiles(string output)
        {
            int count = 0;
            foreach (string pdb in Directory.GetFiles(output, "*.pdb", SearchOption.AllDirectories)) { File.Delete(pdb); count++; }
            foreach (string dev in Directory.GetFiles(output, "appsettings.Development.json", SearchOption.AllDirectories)) { File.Delete(dev); count++; }
            return count;
        }

        public static void WriteVersionFile(string repoRoot, string serverRoot)
        {
            string source = Path.Combine(repoRoot, "src", "CampaignVault", "Plugins", "EngineVersion.cs");
            string version = File.Exists(source) ? EmbeddedServerSupport.ParseEngineVersion(File.ReadAllText(source)) : null;
            if (version == null)
            {
                UnityEngine.Debug.LogWarning("[Vault] Could not read the server version from " + source + "; the client won't check it.");
                return;
            }
            Directory.CreateDirectory(serverRoot);
            File.WriteAllText(Path.Combine(serverRoot, EmbeddedServerSupport.VersionFileName), version + "\n");
        }

        private static void StageModels(string repoRoot, string output)
        {
            string dest = Path.Combine(output, "models", "embedding");
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(Path.Combine(repoRoot, "models", "embedding")))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            }
        }
    }
}
