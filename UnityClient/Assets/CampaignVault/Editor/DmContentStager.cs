using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using CampaignVault.UnityClient.AI;

namespace CampaignVault.UnityClient.Editor
{
    /// <summary>
    /// Stages the DM content the chat driver reads from StreamingAssets (all
    /// git-ignored copies, regenerated here instead of by hand):
    ///   recommended-system-prompt.md      -> CampaignVault/system-prompt.md
    ///   recommended-system-prompt.narrative.md -> CampaignVault/system-prompt.narrative.md
    ///   (only the ```text fence, without the template CAMPAIGN line: the rest
    ///   is installer notes and must not reach the model)
    ///   claude_skills/NAME/SKILL.md       -> CampaignVault/skills/NAME/SKILL.md
    ///   plugins/P/(skillsPath)/NAME/SKILL.md -> CampaignVault/plugin-skills/P/NAME/SKILL.md
    /// Plugin skills are sidecar-only (PLUGINS.md): the server never serves
    /// them, so a client that wants them has to ship them. Runs before every
    /// player build (all targets) and from the CampaignVault menu.
    /// </summary>
    public class DmContentStager : IPreprocessBuildWithReport
    {
        // Before ServerEmbedBuildProcessor (0): cheap, and independent of it.
        public int callbackOrder { get { return -10; } }

        public void OnPreprocessBuild(BuildReport report) { Stage(); }

        [System.Serializable]
        private class PluginManifest
        {
            public string id = string.Empty;
            public string skillsPath = "./skills";
        }

        [MenuItem("CampaignVault/Stage DM Prompt + Skills")]
        public static void Stage()
        {
            string repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            string dest = Path.Combine(Application.streamingAssetsPath, "CampaignVault");
            Directory.CreateDirectory(dest);

            StagePrompt(repoRoot, dest, "recommended-system-prompt.md", "system-prompt.md", true);
            StagePrompt(repoRoot, dest, "recommended-system-prompt.narrative.md", "system-prompt.narrative.md", false);

            int core = CopySkillTree(Path.Combine(repoRoot, "claude_skills"), Path.Combine(dest, "skills"));

            string pluginDest = Path.Combine(dest, "plugin-skills");
            if (Directory.Exists(pluginDest)) { Directory.Delete(pluginDest, true); }
            int plugin = 0;
            string pluginsRoot = Path.Combine(repoRoot, "plugins");
            if (Directory.Exists(pluginsRoot))
            {
                foreach (string pluginDir in Directory.GetDirectories(pluginsRoot))
                {
                    string manifestPath = Path.Combine(pluginDir, "plugin.json");
                    if (!File.Exists(manifestPath)) { continue; }
                    var manifest = JsonUtility.FromJson<PluginManifest>(File.ReadAllText(manifestPath)) ?? new PluginManifest();
                    string skillsPath = string.IsNullOrEmpty(manifest.skillsPath) ? "./skills" : manifest.skillsPath;
                    string source = Path.GetFullPath(Path.Combine(pluginDir, skillsPath));
                    plugin += CopySkillTree(source, Path.Combine(pluginDest, Path.GetFileName(pluginDir)));
                }
            }
            AssetDatabase.Refresh();
            Debug.Log("[Vault] Staged DM prompt, " + core + " core skills, " + plugin + " plugin skills.");
        }

        private static void StagePrompt(string repoRoot, string dest, string sourceName, string targetName, bool required)
        {
            string source = Path.Combine(repoRoot, sourceName);
            string target = Path.Combine(dest, targetName);
            if (!File.Exists(source))
            {
                if (File.Exists(target)) { File.Delete(target); }
                if (required) { Debug.LogWarning("[Vault] " + sourceName + " not found; the client falls back to a one-line prompt."); }
                return;
            }
            File.WriteAllText(target, SystemPromptProvider.ExtractPromptBody(File.ReadAllText(source)) + "\n");
        }

        /// <summary>Copies every NAME/SKILL.md under source; returns how many. Replaces dest wholesale.</summary>
        private static int CopySkillTree(string source, string dest)
        {
            if (!Directory.Exists(source)) { return 0; }
            if (Directory.Exists(dest)) { Directory.Delete(dest, true); }
            int count = 0;
            foreach (string skillDir in Directory.GetDirectories(source))
            {
                string skillFile = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillFile)) { continue; }
                string target = Path.Combine(dest, Path.GetFileName(skillDir));
                Directory.CreateDirectory(target);
                File.Copy(skillFile, Path.Combine(target, "SKILL.md"), true);
                count++;
            }
            return count;
        }
    }
}
