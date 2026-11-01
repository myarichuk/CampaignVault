using System.Collections;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.App
{
    /// <summary>The campaign's content level: how far intimate scenes go (fade-to-black by default), the player's to change.</summary>
    public sealed partial class VaultController
    {
        /// <summary>
        /// The player flipped the switch. The server holds the setting (a player-owned campaign setting), so nothing changes locally
        /// until it says yes; a no leaves the old value and the page re-reads it.
        /// </summary>
        public void SetContentLevel(string level)
        {
            string wanted = ContentLevelRequest.Normalize(level);
            if (wanted == null || !_s.HasCampaign) { return; }
            string current = _s.ContentLevel.Length == 0 ? ContentLevelRequest.Fade : _s.ContentLevel;
            if (wanted == current) { return; }
            if (_s.Driver != null && _s.Driver.IsBusy)
            {
                _s.RaiseToast("Wait for the DM to finish this turn, then change the content setting.", ToastKind.Warning);
                _s.Notify(StateArea.Session);
                return;
            }
            Run(SetContentLevelRoutine(wanted));
        }

        private IEnumerator SetContentLevelRoutine(string wanted)
        {
            var args = CampaignArgs();
            args.ObjectValue["request"] = ContentLevelRequest.Build(wanted);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "play", "take_turn", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            bool ok = result != null && result.Ok && result.Data != null && result.Data.Envelope.GetBool("success", true);
            if (!ok)
            {
                string why = result == null ? "no response"
                    : !result.Ok ? result.ErrorMessage
                    : result.Data != null ? result.Data.Envelope.GetString("summary", "the server refused it") : "the server refused it";
                _s.RaiseToast("Couldn't change the content setting: " + why, ToastKind.Error);
                _s.Notify(StateArea.Session); // the switch re-reads the old value
                yield break;
            }
            ApplyContentLevel(wanted);
            _s.RaiseToast(wanted == ContentLevelRequest.Open
                ? "Scenes may now go beyond fade-to-black, within what your AI provider allows."
                : "Intimate scenes fade to black again.", ToastKind.Success);
        }

        /// <summary>The server confirmed a level: the table and the DM prompt follow it.</summary>
        internal void ApplyContentLevel(string level)
        {
            string v = ContentLevelRequest.Normalize(level) ?? string.Empty;
            _s.ContentLevel = v;
            _s.Prompts.ContentLevel = v;
            if (_prefs != null && _s.HasCampaign) { _prefs.SetInt(PrefKeys.ContentHint + _s.CampaignSlug, 1); }
            _s.Notify(StateArea.Session);
        }

        /// <summary>Once per campaign, while the player has never chosen: say where the switch is. Never nags, never decides for them.</summary>
        private void MaybeHintContentLevel()
        {
            if (_prefs == null || !_s.HasCampaign || _s.ContentLevel.Length > 0) { return; }
            string key = PrefKeys.ContentHint + _s.CampaignSlug;
            if (_prefs.GetInt(key, 0) != 0) { return; }
            _prefs.SetInt(key, 1);
            _prefs.Save();
            _s.RaiseToast(ContentLevelRequest.HintText, ToastKind.Info);
        }
    }
}
