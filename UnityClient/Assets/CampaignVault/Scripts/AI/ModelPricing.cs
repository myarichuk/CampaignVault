using System;
using System.Collections.Generic;
using UnityEngine;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>USD per million tokens. Missing cached/cache-write rates fall back to the plain input rate.</summary>
    public sealed class ModelRate
    {
        public double Input;
        public double CachedInput;
        public double CacheWrite;
        public double Output;
    }

    /// <summary>
    /// Bundled price table (Resources/VaultData/model-pricing.json) plus the
    /// rules for finding a model's row. Unknown models return null: the UI shows
    /// tokens only rather than a made-up price.
    /// </summary>
    public sealed class ModelPricing
    {
        private const string ResourcePath = "VaultData/model-pricing";

        private readonly Dictionary<string, ModelRate> _rates = new Dictionary<string, ModelRate>();

        /// <summary>When the bundled rates were last checked against the providers' pricing pages.</summary>
        public string AsOf { get; private set; } = string.Empty;

        public int Count { get { return _rates.Count; } }

        private static ModelPricing _bundled;

        public static ModelPricing Bundled
        {
            get
            {
                if (_bundled != null) { return _bundled; }
                var asset = Resources.Load<TextAsset>(ResourcePath);
                _bundled = asset != null ? Parse(asset.text) : new ModelPricing();
                return _bundled;
            }
        }

        public static ModelPricing Parse(string json)
        {
            var pricing = new ModelPricing();
            JsonValue root;
            if (string.IsNullOrEmpty(json) || !JsonValue.TryParse(json, out root) || root.Kind != JsonKind.Object) { return pricing; }
            pricing.AsOf = root.GetString("asOf", string.Empty);
            JsonValue models = root.Get("models");
            if (models.Kind != JsonKind.Object) { return pricing; }
            foreach (KeyValuePair<string, JsonValue> entry in models.ObjectValue)
            {
                double input = entry.Value.GetNumber("input", -1);
                double output = entry.Value.GetNumber("output", -1);
                if (input < 0 || output < 0) { continue; }
                pricing._rates[Normalize(entry.Key)] = new ModelRate
                {
                    Input = input,
                    Output = output,
                    CachedInput = entry.Value.GetNumber("cachedInput", input),
                    CacheWrite = entry.Value.GetNumber("cacheWrite", input),
                };
            }
            return pricing;
        }

        /// <summary>Lower-case, no vendor prefix ("anthropic/"), no variant ("…:free"), '.' written as '-'.</summary>
        internal static string Normalize(string model)
        {
            if (string.IsNullOrEmpty(model)) { return string.Empty; }
            string id = model.Trim().ToLowerInvariant();
            int colon = id.IndexOf(':');
            if (colon >= 0) { id = id.Substring(0, colon); }
            int slash = id.LastIndexOf('/');
            if (slash >= 0) { id = id.Substring(slash + 1); }
            return id.Replace('.', '-');
        }

        /// <summary>OpenRouter's ":free" variants cost nothing.</summary>
        public static bool IsFreeVariant(string model)
        {
            return !string.IsNullOrEmpty(model) && model.Trim().EndsWith(":free", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Local servers (Ollama, LM Studio, anything on localhost) bill nothing.</summary>
        public static bool IsLocal(ProviderProfile profile)
        {
            if (profile == null) { return false; }
            if (profile.Preset == "ollama" || profile.Preset == "lmstudio") { return true; }
            string url = (profile.BaseUrl ?? string.Empty).ToLowerInvariant();
            return url.Contains("://localhost") || url.Contains("://127.0.0.1") || url.Contains("://[::1]");
        }

        /// <summary>
        /// The exact row, else the longest row the id extends by a date or "-latest"
        /// (claude-haiku-4-5-20251001 → claude-haiku-4-5). "claude-opus-4-9" must not
        /// silently borrow claude-opus-4's price.
        /// </summary>
        public ModelRate Find(string model)
        {
            string id = Normalize(model);
            if (id.Length == 0) { return null; }
            ModelRate rate;
            if (_rates.TryGetValue(id, out rate)) { return rate; }
            string bestKey = null;
            foreach (string key in _rates.Keys)
            {
                if (id.Length <= key.Length || !id.StartsWith(key, StringComparison.Ordinal)) { continue; }
                if (!IsVersionSuffix(id.Substring(key.Length))) { continue; }
                if (bestKey == null || key.Length > bestKey.Length) { bestKey = key; }
            }
            return bestKey != null ? _rates[bestKey] : null;
        }

        private static bool IsVersionSuffix(string rest)
        {
            if (rest == "-latest") { return true; }
            if (rest.Length < 5 || rest[0] != '-') { return false; }
            for (int i = 1; i <= 4; i++) { if (rest[i] < '0' || rest[i] > '9') { return false; } }
            return true;
        }

        /// <summary>The profile's own prices (set in Settings) win over the table; null when it has none.</summary>
        public static ModelRate OverrideFor(ProviderProfile profile)
        {
            if (profile == null || (profile.PriceInPerM <= 0 && profile.PriceOutPerM <= 0)) { return null; }
            return new ModelRate
            {
                Input = profile.PriceInPerM,
                Output = profile.PriceOutPerM,
                CachedInput = profile.PriceCachedPerM > 0 ? profile.PriceCachedPerM : profile.PriceInPerM,
                CacheWrite = profile.PriceInPerM,
            };
        }
    }
}
