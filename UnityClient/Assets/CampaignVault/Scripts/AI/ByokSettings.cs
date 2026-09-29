using System;
using UnityEngine;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Bring-your-own-key settings for one OpenAI-compatible chat endpoint.
    /// The API key is memory-only by design: it is never written to PlayerPrefs
    /// or disk, never logged, and cleared when the app quits or the component
    /// disables. Only the base URL and model name persist, and only those.
    /// </summary>
    public class ByokSettings : MonoBehaviour
    {
        private const string PrefsBaseUrl = "vault.byok.baseurl";
        private const string PrefsModel = "vault.byok.model";

        [Tooltip("Chat-completions base URL, e.g. https://api.openai.com/v1 (http allowed for localhost only).")]
        public string BaseUrl = "https://api.openai.com/v1";

        [Tooltip("Model name sent as `model` in every chat request.")]
        public string Model = "gpt-4o";

        private string _apiKey = string.Empty;

        public bool HasKey { get { return !string.IsNullOrEmpty(_apiKey); } }

        private void Awake()
        {
            BaseUrl = PlayerPrefs.GetString(PrefsBaseUrl, BaseUrl);
            Model = PlayerPrefs.GetString(PrefsModel, Model);
        }

        public void SetApiKey(string key) { _apiKey = (key ?? string.Empty).Trim(); }

        public void ClearSecrets() { _apiKey = string.Empty; }

        private void OnApplicationQuit() { ClearSecrets(); }
        private void OnDisable() { ClearSecrets(); }

        public void SaveNonSecrets()
        {
            PlayerPrefs.SetString(PrefsBaseUrl, BaseUrl.Trim());
            PlayerPrefs.SetString(PrefsModel, Model.Trim());
            PlayerPrefs.Save();
        }

        public bool Validate(out string reason)
        {
            if (!Net.TextSanitizer.IsAllowedHttpUrl(BaseUrl, out reason)) { return false; }
            if (string.IsNullOrWhiteSpace(Model)) { reason = "Model name is empty."; return false; }
            if (!HasKey) { reason = "No API key set (session-only, paste it in Settings)."; return false; }
            reason = string.Empty;
            return true;
        }

        internal string ChatUrl() { return BaseUrl.TrimEnd('/') + "/chat/completions"; }
        internal string ApiKey() { return _apiKey; }
    }
}
