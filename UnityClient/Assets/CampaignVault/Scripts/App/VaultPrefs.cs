using System.Collections.Generic;
using UnityEngine;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// What the client persists between runs (server URL, connector, campaign,
    /// PC and companion ids, feel toggles). Never provider keys: those live
    /// in ByokSettings' own file.
    /// </summary>
    public interface IVaultPrefs
    {
        string GetString(string key, string fallback);
        void SetString(string key, string value);
        int GetInt(string key, int fallback);
        void SetInt(string key, int value);
        void Delete(string key);
        void Save();
    }

    public static class PrefKeys
    {
        public const string Server = "vault.server";
        /// <summary>
        /// Retired: the DM's connector used to be a saved choice (play/build). Saved as "build", it routed whole sessions
        /// to /build, which has no take_turn, so the DM could narrate but never commit. Deleted on load; turns route
        /// themselves (setup → /build, play → /play).
        /// </summary>
        public const string LegacyConnector = "vault.connector";
        public const string Campaign = "vault.campaign";
        public const string PcId = "vault.pcid";
        public const string Companions = "vault.companions";
        public const string Fx = "vault.fx";
        /// <summary>Prefix; the campaign slug is appended. Set once the "intimate scenes fade to black" hint was shown for that campaign.</summary>
        public const string ContentHint = "vault.contenthint.";
        public const string Sfx = "vault.sfx";
        public const string StoryTextSize = "vault.storytext";
        public const string EmbeddedPort = "vault.embedded.port";
        public const string EmbeddedAutostart = "vault.embedded.autostart";
        /// <summary>
        /// Legacy comma-separated tool allowlist. Dropped on load, never written: an allowlist silently disabled every tool it
        /// didn't name, including ones the server added later and /build's world_build when /build failed to list, which left
        /// the DM unable to seed a new campaign.
        /// </summary>
        public const string LegacyToolAllowlist = "vault.tools";
        /// <summary>Comma-separated tools the player switched off for the DM (empty = every tool), kept across launches (A34).</summary>
        public const string DisabledTools = "vault.tools.disabled";
        /// <summary>Per campaign (suffix the slug): the session number this client left open, so a relaunch resumes it.</summary>
        public const string OpenSessionPrefix = "vault.session.open.";
    }

    public sealed class UnityPrefs : IVaultPrefs
    {
        public string GetString(string key, string fallback) { return PlayerPrefs.GetString(key, fallback); }
        public void SetString(string key, string value) { PlayerPrefs.SetString(key, value); }
        public int GetInt(string key, int fallback) { return PlayerPrefs.GetInt(key, fallback); }
        public void SetInt(string key, int value) { PlayerPrefs.SetInt(key, value); }
        public void Delete(string key) { PlayerPrefs.DeleteKey(key); }
        public void Save() { PlayerPrefs.Save(); }
    }

    /// <summary>For smoke runs and tests: nothing touches the player's real prefs.</summary>
    public sealed class MemoryPrefs : IVaultPrefs
    {
        private readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        private readonly Dictionary<string, int> _ints = new Dictionary<string, int>();

        public string GetString(string key, string fallback) { string v; return _strings.TryGetValue(key, out v) ? v : fallback; }
        public void SetString(string key, string value) { _strings[key] = value; }
        public int GetInt(string key, int fallback) { int v; return _ints.TryGetValue(key, out v) ? v : fallback; }
        public void SetInt(string key, int value) { _ints[key] = value; }
        public void Delete(string key) { _strings.Remove(key); _ints.Remove(key); }
        public void Save() { }
    }
}
