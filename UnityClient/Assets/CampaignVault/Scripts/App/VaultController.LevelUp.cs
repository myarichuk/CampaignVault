using System.Collections;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.App
{
    /// <summary>
    /// The level-up menu: character_level_up (options → apply) on the build connector. The server reads the class
    /// progression and checks the picks; the client shows the choices as the builder's cards and sends what was picked.
    /// </summary>
    public sealed partial class VaultController
    {
        public const string LevelUpTool = "character_level_up";

        /// <summary>Opens the menu for a character: forgets the last one and asks the server what the next level offers.</summary>
        public IEnumerator LevelUpOpen(string characterId)
        {
            var l = _s.LevelUp;
            l.CharacterId = characterId ?? string.Empty;
            l.Offer = null;
            l.Choice = null;
            l.Error = string.Empty;
            l.Done = false;
            l.Applying = false;
            l.Loading = true;
            _s.Notify(StateArea.LevelUp);
            McpOutcome<ToolPayload> result = null;
            yield return CallLevelUp("options", characterId, null, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (l.CharacterId != characterId) { yield break; }
            l.Loading = false;
            if (result == null || !result.Ok)
            {
                l.Error = "Couldn't read the level: " + (result != null ? result.ErrorMessage : "no response");
            }
            else { l.Offer = LevelUpOffer.Parse(result.Data.Data); }
            _s.Notify(StateArea.LevelUp);
        }

        /// <summary>One tap on an option of a slot: the builder's own rule (one pick replaces; several toggle up to the count).</summary>
        public void LevelUpPick(LevelSlot slot, string optionId)
        {
            var l = _s.LevelUp;
            var after = LevelChoices.Toggle(l.Choice, slot, optionId);
            if (after == l.Choice) { return; }
            l.Choice = after;
            l.Error = string.Empty;
            _s.Notify(StateArea.LevelUp);
        }

        /// <summary>Gains the level with the picks. The server refuses a pick it doesn't allow, with the reason, and nothing changes.</summary>
        public IEnumerator LevelUpApply()
        {
            var l = _s.LevelUp;
            if (l.Offer == null || l.Applying || !l.Offer.CanApply(l.Choice)) { yield break; }
            string id = l.CharacterId;
            l.Applying = true;
            l.Error = string.Empty;
            _s.Notify(StateArea.LevelUp);
            McpOutcome<ToolPayload> result = null;
            yield return CallLevelUp("apply", id, LevelUpOffer.PicksArgument(l.Choice), delegate (McpOutcome<ToolPayload> o) { result = o; });
            l.Applying = false;
            if (result == null || !result.Ok)
            {
                l.Error = result != null ? result.ErrorMessage : "no response";
                _s.Notify(StateArea.LevelUp);
                yield break;
            }
            var status = LevelUpStatus.Parse(result.Data.Data.Get("status"));
            string who = l.Offer.Name.Length > 0 ? l.Offer.Name : "The character";
            _s.RaiseToast(who + (status != null ? " is now level " + status.Level + "." : " gained a level."), ToastKind.Success);
            l.Done = true;
            _s.Notify(StateArea.LevelUp);
            // The sheet, the party frame and the played character's card read the new level, scores and hit points.
            if (id == _s.PcId) { _s.PcEntity = null; Run(LoadPc()); }
            Run(RefreshTable());
        }

        private IEnumerator CallLevelUp(string action, string characterId, JsonValue picks, System.Action<McpOutcome<ToolPayload>> done)
        {
            var args = CampaignArgs();
            args.ObjectValue["action"] = JsonValue.FromString(action);
            args.ObjectValue["characterId"] = JsonValue.FromString(characterId ?? string.Empty);
            if (picks != null) { args.ObjectValue["picks"] = picks; }
            yield return _s.Mcp.CallToolData(_s.Config, "build", LevelUpTool, args, done);
        }

        /// <summary>
        /// Tells the player, once per level, that a party member has earned one (the XP rule says so). A toast, not a modal:
        /// a menu in the middle of a scene would interrupt the story. The sheet and the party frame carry the offer on.
        /// </summary>
        private void AnnounceLevelUps(Flows.SessionDigest digest)
        {
            if (digest == null) { return; }
            foreach (var member in digest.Party)
            {
                if (!member.LevelUpReady || !member.IsPc) { continue; }
                string key = member.Id + "@" + member.ClassLevel;
                if (!_s.LevelUpAnnounced.Add(key)) { continue; }
                _s.RaiseToast(member.Name + " has earned a level. Open their sheet to level up.", ToastKind.Success);
            }
        }
    }
}
