using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// The running spend of one campaign, kept across sessions in
    /// persistentDataPath/vault-usage-&lt;slug&gt;.json. Totals are also split per
    /// model so switching models mid-campaign stays honest. It follows the
    /// campaign: asking about, or adding to, another slug saves this one and
    /// loads that one. A missing or corrupt file starts from zero.
    /// </summary>
    public sealed class CampaignUsageStore
    {
        [Serializable]
        private sealed class Row
        {
            public string Model = string.Empty;
            public int Prompt;
            public int Completion;
            public int Cached;
            public int CacheWrite;
            public int Calls;
            public double Cost;
            public int Exact;
            public int Estimated;
            public int Free;
            public int Unpriced;
        }

        [Serializable]
        private sealed class UsageFile
        {
            public List<Row> Models = new List<Row>();
        }

        /// <summary>Tests point this at a scratch folder so they never touch the player's real counters.</summary>
        internal static string DirOverride;

        private readonly Dictionary<string, TokenUsage> _byModel = new Dictionary<string, TokenUsage>();
        private readonly List<string> _order = new List<string>();
        private string _slug = string.Empty;

        public string Slug { get { return _slug; } }

        public static string PathFor(string slug)
        {
            string dir = DirOverride ?? Application.persistentDataPath;
            return Path.Combine(dir, "vault-usage-" + SafeName(slug) + ".json");
        }

        private static string SafeName(string slug)
        {
            var chars = (slug ?? string.Empty).ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') { chars[i] = '_'; }
            }
            return new string(chars);
        }

        /// <summary>Everything this campaign has spent, across all models.</summary>
        public TokenUsage Total(string slug)
        {
            Switch(slug);
            var total = new TokenUsage();
            foreach (string model in _order) { total.Add(_byModel[model]); }
            return total;
        }

        /// <summary>Per-model totals for the breakdown, in the order models first appeared.</summary>
        public List<KeyValuePair<string, TokenUsage>> Breakdown(string slug)
        {
            Switch(slug);
            var rows = new List<KeyValuePair<string, TokenUsage>>();
            foreach (string model in _order) { rows.Add(new KeyValuePair<string, TokenUsage>(model, _byModel[model])); }
            return rows;
        }

        /// <summary>Adds one call's usage to the campaign and saves. No campaign (empty slug) means nothing to count it against.</summary>
        public void Add(string slug, string model, TokenUsage usage)
        {
            if (string.IsNullOrEmpty(slug) || usage == null || usage.IsEmpty) { return; }
            Switch(slug);
            string key = string.IsNullOrEmpty(model) ? "(unknown model)" : model;
            TokenUsage row;
            if (!_byModel.TryGetValue(key, out row)) { row = new TokenUsage(); _byModel[key] = row; _order.Add(key); }
            row.Add(usage);
            Save();
        }

        /// <summary>Zeroes the campaign's counter, on disk too.</summary>
        public void Reset(string slug)
        {
            if (string.IsNullOrEmpty(slug)) { return; }
            Switch(slug);
            _byModel.Clear();
            _order.Clear();
            try { File.Delete(PathFor(slug)); }
            catch (Exception e) { Debug.LogWarning("Could not reset the campaign cost counter: " + e.Message); }
        }

        private void Switch(string slug)
        {
            slug = slug ?? string.Empty;
            if (slug == _slug) { return; }
            _slug = slug;
            _byModel.Clear();
            _order.Clear();
            if (slug.Length > 0) { Load(); }
        }

        private void Load()
        {
            try
            {
                string path = PathFor(_slug);
                if (!File.Exists(path)) { return; }
                var file = JsonUtility.FromJson<UsageFile>(File.ReadAllText(path));
                if (file == null || file.Models == null) { return; }
                foreach (Row r in file.Models)
                {
                    if (r == null || string.IsNullOrEmpty(r.Model) || _byModel.ContainsKey(r.Model)) { continue; }
                    _byModel[r.Model] = new TokenUsage
                    {
                        Prompt = r.Prompt, Completion = r.Completion, Cached = r.Cached, CacheWrite = r.CacheWrite,
                        Calls = r.Calls, Cost = r.Cost, ExactCalls = r.Exact, EstimatedCalls = r.Estimated,
                        FreeCalls = r.Free, UnpricedCalls = r.Unpriced,
                    };
                    _order.Add(r.Model);
                }
            }
            catch (Exception)
            {
                // Unreadable file: count from zero rather than crash the client.
                _byModel.Clear();
                _order.Clear();
            }
        }

        private void Save()
        {
            try
            {
                var file = new UsageFile();
                foreach (string model in _order)
                {
                    TokenUsage u = _byModel[model];
                    file.Models.Add(new Row
                    {
                        Model = model, Prompt = u.Prompt, Completion = u.Completion, Cached = u.Cached, CacheWrite = u.CacheWrite,
                        Calls = u.Calls, Cost = u.Cost, Exact = u.ExactCalls, Estimated = u.EstimatedCalls,
                        Free = u.FreeCalls, Unpriced = u.UnpricedCalls,
                    });
                }
                Directory.CreateDirectory(Path.GetDirectoryName(PathFor(_slug)));
                File.WriteAllText(PathFor(_slug), JsonUtility.ToJson(file, true));
            }
            catch (Exception e)
            {
                Debug.LogWarning("Could not save the campaign cost counter: " + e.Message);
            }
        }
    }
}
