using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>One saved AI provider: an OpenAI-compatible endpoint, a model, and its key.</summary>
    [Serializable]
    public class ProviderProfile
    {
        public string Name = "Default";
        public string Preset = "custom";
        public string BaseUrl = "https://api.openai.com/v1";
        public string Model = "gpt-4o";
        // Stored as plain text in the profiles file (a deliberate BYOK trade-off).
        public string ApiKey = string.Empty;
        /// <summary>Sampling temperature 0–2. Exactly 1 (the default) is omitted from requests.</summary>
        public float Temperature = 1f;
        /// <summary>Cap on reply tokens. Zero or less means "don't send max_tokens" (provider default).</summary>
        public int MaxTokens;
        /// <summary>Reasoning effort for providers that take it (OpenRouter). Empty = unset.</summary>
        public string ReasoningEffort = string.Empty;
        /// <summary>Send whole replies instead of streaming them (for endpoints that mishandle stream:true).</summary>
        public bool DisableStreaming;
        /// <summary>Legacy: one model call writes both the tool calls and the scene (no storyteller pass).</summary>
        public bool SinglePass;
        /// <summary>Optional prices per million tokens (0 = unset). They win over the bundled table, and are the only way to price a Custom endpoint.</summary>
        public float PriceInPerM;
        public float PriceCachedPerM;
        public float PriceOutPerM;
    }

    /// <summary>A starting point for a profile; local presets need no key.</summary>
    public sealed class ProviderPreset
    {
        public readonly string Id;
        public readonly string Label;
        public readonly string BaseUrl;
        public readonly string Model;
        public readonly bool NeedsKey;

        public ProviderPreset(string id, string label, string baseUrl, string model, bool needsKey)
        {
            Id = id; Label = label; BaseUrl = baseUrl; Model = model; NeedsKey = needsKey;
        }
    }

    [Serializable]
    internal class ProviderFile
    {
        public int Active;
        public List<ProviderProfile> Profiles = new List<ProviderProfile>();
    }

    /// <summary>
    /// Bring-your-own-key settings: a list of provider profiles, one of them
    /// active. Each profile is an OpenAI-compatible chat endpoint + model + key.
    /// Profiles (keys included) live as plain JSON in
    /// persistentDataPath/vault-providers.json — local-only, never logged and
    /// only ever sent as an Authorization header to that profile's endpoint.
    /// BaseUrl/Model/SetApiKey act on the active profile in memory; call Save()
    /// to persist.
    /// </summary>
    public class ByokSettings : MonoBehaviour
    {
        private const string FileName = "vault-providers.json";
        private const string LegacyBaseUrl = "vault.byok.baseurl";
        private const string LegacyModel = "vault.byok.model";

        public static readonly ProviderPreset[] Presets =
        {
            new ProviderPreset("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4o", true),
            new ProviderPreset("anthropic", "Anthropic", "https://api.anthropic.com/v1", "claude-sonnet-5-5", true),
            new ProviderPreset("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "anthropic/claude-sonnet-4.5", true),
            new ProviderPreset("ollama", "Ollama", "http://localhost:11434/v1", "llama3.1", false),
            new ProviderPreset("lmstudio", "LM Studio", "http://localhost:1234/v1", "local-model", false),
            new ProviderPreset("custom", "Custom", "https://api.openai.com/v1", "gpt-4o", true),
        };

        public readonly List<ProviderProfile> Profiles = new List<ProviderProfile>();
        public int ActiveIndex;

        /// <summary>Tests point this at a scratch file so they never read or write the player's real profiles.</summary>
        internal static string PathOverride;

        public static string FilePath { get { return PathOverride ?? Path.Combine(Application.persistentDataPath, FileName); } }

        public static ProviderPreset PresetFor(string id)
        {
            foreach (ProviderPreset p in Presets) { if (p.Id == id) { return p; } }
            return Presets[Presets.Length - 1];
        }

        public ProviderProfile Active
        {
            get
            {
                if (Profiles.Count == 0) { Profiles.Add(new ProviderProfile()); }
                ActiveIndex = Mathf.Clamp(ActiveIndex, 0, Profiles.Count - 1);
                return Profiles[ActiveIndex];
            }
        }

        public string BaseUrl { get { return Active.BaseUrl; } set { Active.BaseUrl = value ?? string.Empty; } }
        public string Model { get { return Active.Model; } set { Active.Model = value ?? string.Empty; } }
        public bool HasKey { get { return !string.IsNullOrEmpty(Active.ApiKey); } }
        public bool NeedsKey { get { return PresetFor(Active.Preset).NeedsKey; } }

        private void Awake() { Load(); }

        public void SetApiKey(string key) { Active.ApiKey = (key ?? string.Empty).Trim(); }

        /// <summary>Forget the active profile's key, on disk too.</summary>
        public void ClearSecrets() { Active.ApiKey = string.Empty; Save(); }

        public ProviderProfile AddProfile(string presetId)
        {
            ProviderPreset preset = PresetFor(presetId);
            var profile = new ProviderProfile
            {
                Name = UniqueName(preset.Label),
                Preset = preset.Id,
                BaseUrl = preset.BaseUrl,
                Model = preset.Model,
            };
            Profiles.Add(profile);
            ActiveIndex = Profiles.Count - 1;
            return profile;
        }

        public void ApplyPreset(ProviderProfile profile, string presetId)
        {
            ProviderPreset preset = PresetFor(presetId);
            profile.Preset = preset.Id;
            profile.BaseUrl = preset.BaseUrl;
            profile.Model = preset.Model;
        }

        public void DeleteActive()
        {
            if (Profiles.Count <= 1) { Profiles[0] = new ProviderProfile(); ActiveIndex = 0; return; }
            Profiles.RemoveAt(ActiveIndex);
            ActiveIndex = Mathf.Clamp(ActiveIndex, 0, Profiles.Count - 1);
        }

        private string UniqueName(string baseName)
        {
            string name = baseName;
            int n = 2;
            while (Profiles.Exists(delegate (ProviderProfile p) { return p.Name == name; })) { name = baseName + " " + n++; }
            return name;
        }

        public void Load()
        {
            Profiles.Clear();
            ActiveIndex = 0;
            try
            {
                if (File.Exists(FilePath))
                {
                    var file = JsonUtility.FromJson<ProviderFile>(File.ReadAllText(FilePath));
                    if (file != null && file.Profiles != null)
                    {
                        Profiles.AddRange(file.Profiles);
                        ActiveIndex = file.Active;
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable file: start clean rather than crash the client.
                Profiles.Clear();
            }
            if (Profiles.Count == 0 && PlayerPrefs.HasKey(LegacyBaseUrl))
            {
                // One-time migration of the old single endpoint (its key was never stored).
                Profiles.Add(new ProviderProfile
                {
                    Name = "Default",
                    BaseUrl = PlayerPrefs.GetString(LegacyBaseUrl, "https://api.openai.com/v1"),
                    Model = PlayerPrefs.GetString(LegacyModel, "gpt-4o"),
                });
            }
            ProviderProfile normalised = Active; // seeds a default profile / clamps the index
        }

        public void Save()
        {
            try
            {
                var file = new ProviderFile { Active = ActiveIndex, Profiles = new List<ProviderProfile>(Profiles) };
                File.WriteAllText(FilePath, JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Could not save provider profiles: " + e.Message);
            }
        }

        public bool Validate(out string reason)
        {
            if (!Net.TextSanitizer.IsAllowedHttpUrl(BaseUrl, out reason)) { return false; }
            if (string.IsNullOrWhiteSpace(Model)) { reason = "Model name is empty."; return false; }
            if (NeedsKey && !HasKey) { reason = "No API key set for \"" + Active.Name + "\" (add one in Settings)."; return false; }
            reason = string.Empty;
            return true;
        }

        internal string ChatUrl() { return BaseUrl.TrimEnd('/') + "/chat/completions"; }
        internal string ApiKey() { return Active.ApiKey ?? string.Empty; }

        /// <summary>
        /// Advisory check of the active profile: GET {base}/models. Reports
        /// reachable / key rejected / model unknown. Not every provider serves
        /// /models, so a 404 is "reachable, unverified", not a failure.
        /// </summary>
        public IEnumerator TestConnection(Action<bool, string> done)
        {
            string reason;
            if (!Validate(out reason)) { done(false, reason); yield break; }
            using (UnityWebRequest web = UnityWebRequest.Get(BaseUrl.TrimEnd('/') + "/models"))
            {
                if (HasKey) { web.SetRequestHeader("Authorization", "Bearer " + ApiKey()); }
                web.timeout = 15;
                yield return web.SendWebRequest();
                long code = web.responseCode;
                if (web.result == UnityWebRequest.Result.ConnectionError)
                {
                    done(false, "Cannot reach " + BaseUrl + " (" + Net.TextSanitizer.Redact(web.error, ApiKey()) + ").");
                    yield break;
                }
                if (code == 401 || code == 403) { done(false, "The provider rejected the API key (HTTP " + code + ")."); yield break; }
                if (code == 404 || code == 405) { done(true, "Reachable, but this provider has no /models to verify against."); yield break; }
                if (web.result != UnityWebRequest.Result.Success)
                {
                    done(false, "HTTP " + code + " from " + BaseUrl + ".");
                    yield break;
                }
                JsonValue body;
                if (JsonValue.TryParse(web.downloadHandler.text ?? string.Empty, out body) && ModelListed(body, Model.Trim()) == false)
                {
                    done(false, "Connected, but model \"" + Model.Trim() + "\" is not in the provider's model list.");
                    yield break;
                }
                done(true, "Connected. Key and model look good.");
            }
        }

        /// <summary>true/false if the list is readable and (not) containing the model; null if unknowable.</summary>
        private static bool? ModelListed(JsonValue body, string model)
        {
            var data = body.GetArray("data");
            if (data == null || data.Count == 0) { return null; }
            foreach (var entry in data)
            {
                // Ollama lists "llama3.1:latest" for the model name "llama3.1".
                string id = entry.GetString("id", string.Empty);
                if (id == model || id.StartsWith(model + ":", StringComparison.Ordinal)) { return true; }
            }
            return false;
        }
    }
}
