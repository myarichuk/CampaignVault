using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Headless-buildable entry points (unity build/run -executeMethod) plus the
    /// scene bootstrap they depend on: a fresh checkout's SampleScene has no
    /// VaultClient and isn't registered in EditorBuildSettings, so a player
    /// build ships zero scenes. EnsureMainSceneBootstrapped fixes both, and runs
    /// automatically before every build method below.
    /// </summary>
    public static class BuildTools
    {
        private const string ScenePath = "Assets/Scenes/SampleScene.unity";
        private const string OutputRoot = "Builds";

        [MenuItem("CampaignVault/Bootstrap Main Scene")]
        public static void EnsureMainSceneBootstrapped()
        {
            var scene = SceneManager.GetActiveScene();
            bool alreadyOpen = scene.IsValid() && scene.path == ScenePath;
            scene = alreadyOpen ? scene : EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var existing = Object.FindAnyObjectByType<VaultClientUI>();
            if (existing == null)
            {
                var go = new GameObject("VaultClient");
                go.AddComponent<VaultClientUI>();
                Debug.Log("[Vault] VaultClient added to " + ScenePath + ".");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            var scenes = EditorBuildSettings.scenes;
            bool registered = false;
            foreach (var s in scenes) { if (s.path == ScenePath) { registered = true; break; } }
            if (!registered)
            {
                var list = new System.Collections.Generic.List<EditorBuildSettingsScene>(scenes);
                list.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = list.ToArray();
                Debug.Log("[Vault] " + ScenePath + " registered in Build Settings.");
            }
        }

        [MenuItem("CampaignVault/Build/macOS (arm64)")]
        public static void BuildStandaloneOSX()
        {
            // Pinned so the build matches the osx-arm64 server ServerEmbedBuildProcessor stages.
            MacArchitecture.Set(UnityEditor.Build.OSArchitecture.ARM64);
            Build(BuildTarget.StandaloneOSX, "CampaignVaultClient.app");
        }

        [MenuItem("CampaignVault/Build/Windows x64")]
        public static void BuildStandaloneWindows64() { Build(BuildTarget.StandaloneWindows64, "CampaignVaultClient.exe"); }

        [MenuItem("CampaignVault/Build/Linux x64")]
        public static void BuildStandaloneLinux64() { Build(BuildTarget.StandaloneLinux64, "CampaignVaultClient"); }

        [MenuItem("CampaignVault/Build/WebGL")]
        public static void BuildWebGL() { Build(BuildTarget.WebGL, "web"); }

        /// <summary>
        /// Reads a game-ci style command-line argument (e.g. -customBuildPath).
        /// Returns null when running from the editor menu or when absent.
        /// </summary>
        private static string? GetCommandLineArg(string name)
        {
            var args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name && !string.IsNullOrEmpty(args[i + 1]))
                    return args[i + 1];
            }
            return null;
        }

        private static void Build(BuildTarget target, string outputName)
        {
            EnsureMainSceneBootstrapped();

            // game-ci's unity-builder passes -customBuildPath and validates the
            // file exists there after Unity exits; ignoring it fails the job
            // even when the player build itself succeeds.
            string locationPathName;
            string? customPath = GetCommandLineArg("-customBuildPath");
            if (!string.IsNullOrEmpty(customPath))
            {
                string? parent = Path.GetDirectoryName(customPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                locationPathName = customPath;
            }
            else
            {
                string platformDir = Path.Combine(OutputRoot, target.ToString());
                Directory.CreateDirectory(platformDir);
                locationPathName = target == BuildTarget.WebGL
                    ? platformDir
                    : Path.Combine(platformDir, outputName);
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = locationPathName,
                target = target,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log("[Vault] Build " + target + " result=" + summary.result + " size=" + summary.totalSize + "B errors=" + summary.totalErrors);
            if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new UnityEditor.Build.BuildFailedException("[Vault] Build failed for " + target + ": " + summary.result);
            }
        }
    }
}
