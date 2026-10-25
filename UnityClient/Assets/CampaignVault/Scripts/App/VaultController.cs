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
    /// <summary>
    /// Every player-facing command, UI-free. Commands that talk to the server
    /// are IEnumerators, so a view fires them with Run(...) while the smoke
    /// scenario and tests yield them directly (no play mode needed). All
    /// writes to VaultAppState happen here and are announced via Notify.
    /// </summary>
    public sealed partial class VaultController
    {
        private readonly VaultAppState _s;
        private readonly MonoBehaviour _host;
        private readonly IVaultPrefs _prefs;

        public VaultController(VaultAppState state, MonoBehaviour host, IVaultPrefs prefs)
        {
            _s = state;
            _host = host;
            _prefs = prefs;
            if (_s.Driver != null) { _s.Driver.ToolSucceeded += OnDriverTool; }
            _s.TurnRetryAvailable = delegate { return CanRetryLastTurn; };
            _s.RetryTurnRequested += delegate { RetryLastTurn(); };
            _s.EarlierHistoryRequested += delegate { LoadEarlierHistory(); };
            _s.EarlierHistoryAvailable = delegate { return CanLoadEarlier; };
        }

        // The session number the log already shows a recap for (0 = none this run).
        private int _recappedSession;
        // True while a player turn runs: its segments are saved together when it ends.
        private bool _turnActive;

        /// <summary>Tools that only read; a turn that ran nothing else changed nothing the table shows.</summary>
        private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>
        {
            SystemPromptProvider.LoadSkillTool, "lookup", "get_entity", "search_world", "start_session", "end_session", "list_campaigns",
        };

        public VaultAppState State { get { return _s; } }

        /// <summary>Fire-and-forget for views.</summary>
        public Coroutine Run(IEnumerator routine) { return _host.StartCoroutine(routine); }

        // =====================================================================
        // Persistence
        // =====================================================================

        public void LoadPreferences()
        {
            _s.Config.ServerUrl = _prefs.GetString(PrefKeys.Server, _s.Config.ServerUrl);
            _s.Config.Connector = _prefs.GetString(PrefKeys.Connector, "play");
            _s.Prompts.CampaignSlug = _prefs.GetString(PrefKeys.Campaign, string.Empty);
            _s.PcId = _prefs.GetString(PrefKeys.PcId, string.Empty);
            _s.FxEnabled = _prefs.GetInt(PrefKeys.Fx, 1) == 1;
            _s.SfxMuted = _prefs.GetInt(PrefKeys.Sfx, 1) == 0;
            _s.StoryTextSize = Mathf.Clamp(_prefs.GetInt(PrefKeys.StoryTextSize, VaultAppState.DefaultStoryTextSize), 0, VaultAppState.StoryTextSizes.Length - 1);
            if (_s.Server != null)
            {
                _s.Server.Config = _s.Config;
                _s.Server.Port = _prefs.GetInt(PrefKeys.EmbeddedPort, 5275);
                _s.Server.AutoStart = _prefs.GetInt(PrefKeys.EmbeddedAutostart, 1) == 1;
                // Zip-installed plugins' skills, minus disabled plugins (A17).
                _s.Prompts.UserPluginsDir = _s.Server.UserPluginsDir;
                _s.Prompts.IsPluginDisabled = IsPluginDisabled;
            }
            // The tool allowlist survives a relaunch (A34); empty means everything on.
            foreach (string name in _prefs.GetString(PrefKeys.Tools, string.Empty).Split(','))
            {
                if (name.Trim().Length > 0) { _s.Driver.AllowedTools.Add(name.Trim()); }
            }
            _s.CompanionIds.Clear();
            foreach (string id in _prefs.GetString(PrefKeys.Companions, string.Empty).Split(';'))
            {
                string clean = id.Trim();
                if (clean.Length > 0 && !_s.CompanionIds.Contains(clean)) { _s.CompanionIds.Add(clean); }
            }
        }

        private void SaveCompanionIds()
        {
            _prefs.SetString(PrefKeys.Companions, string.Join(";", _s.CompanionIds.ToArray()));
            _prefs.Save();
        }

        private void SavePcId()
        {
            if (string.IsNullOrEmpty(_s.PcId)) { _prefs.Delete(PrefKeys.PcId); }
            else { _prefs.SetString(PrefKeys.PcId, _s.PcId); }
            _prefs.Save();
        }

        public void SetFx(bool enabled)
        {
            _s.FxEnabled = enabled;
            _prefs.SetInt(PrefKeys.Fx, enabled ? 1 : 0);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
        }

        /// <summary>Sets the story text size step (clamped); returns the step now in effect.</summary>
        public int SetStoryTextSize(int step)
        {
            step = Mathf.Clamp(step, 0, VaultAppState.StoryTextSizes.Length - 1);
            if (step == _s.StoryTextSize) { return step; }
            _s.StoryTextSize = step;
            _prefs.SetInt(PrefKeys.StoryTextSize, step);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
            return step;
        }

        public void SetSfxMuted(bool muted)
        {
            _s.SfxMuted = muted;
            _prefs.SetInt(PrefKeys.Sfx, muted ? 0 : 1);
            _prefs.Save();
            _s.Notify(StateArea.Preferences);
        }
    }
}
