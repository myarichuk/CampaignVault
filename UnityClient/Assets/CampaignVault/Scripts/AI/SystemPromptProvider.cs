using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Loads the DM system prompt plus the dnd-* skills shipped in
    /// StreamingAssets/CampaignVault, plus plugin sidecar skills staged under
    /// plugin-skills/PLUGIN (indexed as "PLUGIN:name"; the server never serves
    /// plugin skills, see PLUGINS.md). Skills load on demand, the way Claude
    /// Code/opencode do it: the system prompt carries only a name +
    /// description index and the driver serves bodies through a client-side
    /// load_skill tool, so a call costs ~2k prompt tokens instead of ~24k with
    /// every skill inlined. The prompt is assembled so its prefix is
    /// byte-stable (prompt, then skill index, then the per-campaign line last),
    /// which lets providers with automatic prompt caching reuse it.
    /// Desktop file access is used; on platforms where StreamingAssets is
    /// packed (WebGL) the driver reports skills-unavailable instead of failing
    /// quietly.
    /// </summary>
    public class SystemPromptProvider : MonoBehaviour
    {
        public const string LoadSkillTool = "load_skill";

        [Tooltip("Campaign slug injected into the CAMPAIGN line, e.g. my-campaign.")]
        public string CampaignSlug = string.Empty;

        [Tooltip("PC roster line, e.g. chars/lyra — Lyra. Filled from start_session's PCs.")]
        public string PartyLine = string.Empty;

        [Tooltip("Ruleset for the RULESET line, from the campaign's system (dnd5e, pf2e, narrative).")]
        public string Ruleset = string.Empty;

        [Tooltip("Party fingerprint from the last start_session; take_turn echoes it as clientPartyFingerprint.")]
        public string PartyFingerprint = string.Empty;

        private sealed class Skill
        {
            public string Name = string.Empty;
            public string Description = string.Empty;
            public string Body = string.Empty;
        }

        private string _basePrompt;
        private List<Skill> _skills;

        public string BuildSystemPrompt()
        {
            EnsureLoaded();
            var sb = new StringBuilder(_basePrompt);
            sb.Append("\n\nCAMPAIGN: campaignName=\"").Append(CampaignSlug.Trim()).Append('"');
            sb.Append(" | PCs: ").Append(PartyLine.Trim());
            sb.Append(" | Ruleset: ").Append(string.IsNullOrEmpty(Ruleset) ? "(unknown: read it from start_session)" : Ruleset.Trim());
            if (!string.IsNullOrEmpty(PartyFingerprint))
            {
                sb.Append(" | clientPartyFingerprint=\"").Append(PartyFingerprint.Trim()).Append('"');
            }
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>Skill names for the load_skill tool's enum.</summary>
        public List<string> SkillNames()
        {
            EnsureLoaded();
            var names = new List<string>();
            foreach (var skill in _skills) { names.Add(skill.Name); }
            return names;
        }

        public bool TryLoadSkill(string name, out string body)
        {
            EnsureLoaded();
            string wanted = (name ?? string.Empty).Trim();
            foreach (var skill in _skills)
            {
                if (string.Equals(skill.Name, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    body = skill.Body;
                    return true;
                }
            }
            body = string.Empty;
            return false;
        }

        public static string StreamingRoot()
        {
            return Path.Combine(Application.streamingAssetsPath, "CampaignVault");
        }

        /// <summary>
        /// The skills the driver can serve. Panels show this so "skills
        /// loaded" is a verified fact, not an assumption.
        /// </summary>
        public bool TryGetSkillStatus(out int count, out string names)
        {
            EnsureLoaded();
            count = _skills.Count;
            names = string.Join(", ", SkillNames().ToArray());
            return count > 0;
        }

        private void EnsureLoaded()
        {
            if (_basePrompt != null) { return; }
            string root = StreamingRoot();
            _skills = LoadSkills(Path.Combine(root, "skills"), string.Empty);
            string pluginRoot = Path.Combine(root, "plugin-skills");
            try
            {
                if (Directory.Exists(pluginRoot))
                {
                    foreach (string pluginDir in Directory.GetDirectories(pluginRoot))
                    {
                        _skills.AddRange(LoadSkills(pluginDir, Path.GetFileName(pluginDir)));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Vault] Plugin skill load hit: " + ex.GetType().Name);
            }
            _skills.Sort(delegate (Skill a, Skill b) { return string.CompareOrdinal(a.Name, b.Name); });
            var sb = new StringBuilder(ReadFile(root, "system-prompt.md"));
            if (_skills.Count > 0)
            {
                sb.Append("\n\n# SKILLS (load on demand)\n");
                sb.Append("Call ").Append(LoadSkillTool).Append(" with a skill name to read its full text before the work it covers. ");
                sb.Append("Loads from earlier turns are dropped from the conversation to save context: reload a skill whenever you need it again.\n");
                foreach (var skill in _skills)
                {
                    sb.Append("- ").Append(skill.Name);
                    if (!string.IsNullOrEmpty(skill.Description)) { sb.Append(": ").Append(skill.Description); }
                    sb.Append('\n');
                }
            }
            _basePrompt = sb.ToString();
        }

        /// <summary>Every NAME/SKILL.md under skillsDir; a plugin prefix namespaces the names.</summary>
        private static List<Skill> LoadSkills(string skillsDir, string plugin)
        {
            var skills = new List<Skill>();
            try
            {
                if (!Directory.Exists(skillsDir)) { return skills; }
                foreach (string dir in Directory.GetDirectories(skillsDir))
                {
                    string skillFile = Path.Combine(dir, "SKILL.md");
                    if (!File.Exists(skillFile)) { continue; }
                    string text = File.ReadAllText(skillFile);
                    string description = FrontMatterValue(text, "description");
                    if (plugin.Length > 0) { description = (description.Length > 0 ? description + " " : string.Empty) + "(plugin " + plugin + ")"; }
                    skills.Add(new Skill
                    {
                        Name = plugin.Length > 0 ? plugin + ":" + Path.GetFileName(dir) : Path.GetFileName(dir),
                        Description = description,
                        Body = text,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Vault] Skill load hit: " + ex.GetType().Name);
            }
            return skills;
        }

        /// <summary>A top-level "key: value" line from a SKILL.md YAML front matter block.</summary>
        private static string FrontMatterValue(string text, string key)
        {
            if (!text.StartsWith("---", StringComparison.Ordinal)) { return string.Empty; }
            int end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) { return string.Empty; }
            foreach (string raw in text.Substring(3, end - 3).Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.StartsWith(key + ":", StringComparison.Ordinal))
                {
                    return line.Substring(key.Length + 1).Trim().Trim('"');
                }
            }
            return string.Empty;
        }

        private static string ReadFile(string root, string name)
        {
            try
            {
                string path = Path.Combine(root, name);
                if (File.Exists(path)) { return File.ReadAllText(path); }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Vault] Prompt file missing (" + name + "): " + ex.GetType().Name);
            }
            return "You are a Game Master connected to Campaign Vault MCP. Commit via take_turn, then narrate.";
        }
    }
}
