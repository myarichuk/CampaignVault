using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Server;

namespace CampaignVault.UnityClient.App
{
    /// <summary>AI provider profiles: one source of truth for Settings and Setup.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // AI provider profiles (one source of truth for Settings and Setup)
        // =====================================================================

        public void SelectProfile(int index)
        {
            _s.Byok.ActiveIndex = Mathf.Clamp(index, 0, _s.Byok.Profiles.Count - 1);
            _s.Byok.Save();
            _s.Driver.ClearProviderProblem();
            _s.Notify(StateArea.Providers | StateArea.Driver);
        }

        public void AddProfile(string presetId)
        {
            _s.Byok.AddProfile(presetId);
            _s.Byok.Save();
            _s.Notify(StateArea.Providers);
        }

        public void ApplyPreset(string presetId)
        {
            _s.Byok.ApplyPreset(_s.Byok.Active, presetId);
            _s.Notify(StateArea.Providers);
        }

        /// <summary>Zeroes the open campaign's persisted cost counter (the session counter is untouched).</summary>
        public void ResetCampaignUsage()
        {
            if (_s.Driver.CampaignUsage == null || !_s.HasCampaign) { return; }
            _s.Driver.CampaignUsage.Reset(_s.CampaignSlug);
            _s.Notify(StateArea.Driver);
        }

        /// <summary>Copies the edited fields into the active profile. A blank key keeps the saved one, unless the host changed: a key is never sent to a host it wasn't issued for (A14).</summary>
        public bool SaveProfile(ProviderProfile edited, string newKey, out string reason)
        {
            var p = _s.Byok.Active;
            if (!string.IsNullOrEmpty((edited.Name ?? string.Empty).Trim())) { p.Name = edited.Name.Trim(); }
            string newBase = (edited.BaseUrl ?? string.Empty).Trim();
            if (!string.Equals(newBase, p.BaseUrl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty((newKey ?? string.Empty).Trim())) { p.ApiKey = string.Empty; }
            p.BaseUrl = newBase;
            p.Model = (edited.Model ?? string.Empty).Trim();
            p.Temperature = Mathf.Clamp(edited.Temperature, 0f, 2f);
            p.MaxTokens = Math.Max(0, edited.MaxTokens);
            p.ReasoningEffort = edited.ReasoningEffort ?? string.Empty;
            p.DisableStreaming = edited.DisableStreaming;
            p.SinglePass = edited.SinglePass;
            // Only a saved profile other than this one can narrate for it; anything else means "this profile narrates".
            var narrator = _s.Byok.Find(edited.Narrator);
            p.Narrator = narrator != null && !ReferenceEquals(narrator, p) ? ByokSettings.IdOf(narrator) : string.Empty;
            p.PriceInPerM = Math.Max(0f, edited.PriceInPerM);
            p.PriceCachedPerM = Math.Max(0f, edited.PriceCachedPerM);
            p.PriceOutPerM = Math.Max(0f, edited.PriceOutPerM);
            if (!string.IsNullOrEmpty((newKey ?? string.Empty).Trim())) { _s.Byok.SetApiKey(newKey); }
            _s.Byok.Save();
            // New settings deserve a fresh try: the old refusal was about the old ones.
            _s.Driver.ClearProviderProblem();
            bool ok = _s.Byok.Validate(out reason);
            _s.Notify(StateArea.Providers | StateArea.Driver);
            return ok;
        }

        /// <summary>
        /// Tests the active profile, then the profile that writes its scenes when that is
        /// another one, and records the verdict: a rejected key or an unknown model blocks
        /// play until fixed; an unreachable provider doesn't (networks blip), it only shows
        /// in the result.
        /// </summary>
        public IEnumerator TestProvider(Action<bool, string> done)
        {
            bool ok = false;
            string message = string.Empty;
            yield return _s.Byok.TestConnection(delegate (bool o, string m) { ok = o; message = m; });
            if (ok && _s.Byok.HasSeparateNarrator)
            {
                ProviderProfile narrator = _s.Byok.Narrator;
                string first = message;
                yield return _s.Byok.TestConnection(narrator, delegate (bool o, string m)
                {
                    ok = o;
                    message = o ? first + " Storyteller \"" + narrator.Name + "\": " + m : "Storyteller profile \"" + narrator.Name + "\": " + m;
                });
            }
            if (ok) { _s.Driver.ClearProviderProblem(); }
            else if (IsSettingsProblem(message)) { _s.Driver.ReportProviderProblem(message); }
            _s.Notify(StateArea.Providers | StateArea.Driver);
            if (done != null) { done(ok, message); }
        }

        /// <summary>Startup: a configured provider is tested once, quietly, so a dead key shows before the first turn.</summary>
        public IEnumerator CheckProvider()
        {
            string reason;
            if (!_s.Byok.Validate(out reason)) { yield break; }
            yield return TestProvider(null);
        }

        internal static bool IsSettingsProblem(string testMessage)
        {
            string m = testMessage ?? string.Empty;
            return m.IndexOf("rejected the API key", StringComparison.Ordinal) >= 0
                || m.IndexOf("not in the provider's model list", StringComparison.Ordinal) >= 0;
        }

        public void ForgetProviderKey()
        {
            _s.Byok.ClearSecrets();
            _s.Notify(StateArea.Providers);
        }

        public void DeleteProfile()
        {
            _s.Byok.DeleteActive();
            _s.Byok.Save();
            _s.Notify(StateArea.Providers | StateArea.Driver);
        }
    }
}
