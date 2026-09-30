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
        public const string Connector = "vault.connector";
        public const string Campaign = "vault.campaign";
        public const string PcId = "vault.pcid";
        public const string Companions = "vault.companions";
        public const string Fx = "vault.fx";
        public const string Sfx = "vault.sfx";
        public const string EmbeddedPort = "vault.embedded.port";
        public const string EmbeddedAutostart = "vault.embedded.autostart";
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
