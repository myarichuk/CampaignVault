using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Compilation-step embed: before every desktop player build, publish the
    /// CampaignVault server self-contained for the target RID and stage it
    /// (+ embedding models) under StreamingAssets, so the redistributable
    /// carries its own MCP server. Mirrors tools/embed-server.sh. WebGL builds
    /// skip with a warning (no subprocesses there).
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
            string target = Path.Combine(repoRoot, "src", "CampaignVault", "CampaignVault.csproj");
            string output = Path.Combine(repoRoot, "UnityClient", "Assets", "StreamingAssets", "CampaignVault", "Server", rid);
            Directory.CreateDirectory(output);

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
            StageModels(repoRoot, output);
            AssetDatabase.Refresh();
        }

        private static string RidForPlatform(BuildTarget platform)
        {
            if (platform == BuildTarget.StandaloneOSX) { return "osx-arm64"; }
            if (platform == BuildTarget.StandaloneWindows || platform == BuildTarget.StandaloneWindows64) { return "win-x64"; }
            if (platform == BuildTarget.StandaloneLinux64) { return "linux-x64"; }
            return null;
        }

        private static void StageModels(string repoRoot, string output)
        {
            string modelSrc = Path.Combine(repoRoot, "models", "embedding", "model.onnx");
            if (!File.Exists(modelSrc) || new FileInfo(modelSrc).Length < 1000000)
            {
                UnityEngine.Debug.LogWarning("[Vault] Real model.onnx not found — embedded server ships without embeddings.");
                return;
            }
            string dest = Path.Combine(output, "models", "embedding");
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(Path.Combine(repoRoot, "models", "embedding")))
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            }
        }
    }
}
