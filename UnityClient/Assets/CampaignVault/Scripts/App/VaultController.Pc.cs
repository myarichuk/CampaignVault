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
    /// <summary>The player character and companions: the sheet, items, tracked allies.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // Party
        // =====================================================================

        public void SetPcId(string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0 || id == _s.PcId) { return; }
            _s.PcId = id;
            _s.Pc = null;
            _s.PcEntity = null;
            SavePcId();
            _s.Notify(StateArea.Pc);
        }

        public IEnumerator LoadPc()
        {
            if (!_s.TryBeginBusy("pc")) { yield break; }
            try
            {
                McpOutcome<JsonValue> result = null;
                yield return GetCharacter(_s.PcId, delegate (McpOutcome<JsonValue> o) { result = o; });
                if (!result.Ok)
                {
                    _s.PcError = result.ErrorMessage;
                    _s.Pc = null;
                    _s.Notify(StateArea.Pc);
                    yield break;
                }
                _s.PcEntity = result.Data;
                _s.Driver.PcCard = Storyteller.PcCard(result.Data);
                var sheet = PcSheet.FromEntity(result.Data);
                if (string.IsNullOrEmpty(sheet.Ruleset)) { sheet.Ruleset = _s.Ruleset; }
                _s.Pc = sheet;
                _s.PcError = string.Empty;
                _s.Notify(StateArea.Pc);
            }
            finally { _s.EndBusy("pc"); }
        }

        /// <summary>get_entity for a character in the active campaign: the character entity alone. Bare ids get the chars/ prefix.</summary>
        public IEnumerator GetCharacter(string id, Action<McpOutcome<JsonValue>> done)
        {
            yield return GetCharacterDetail(id, delegate (McpOutcome<JsonValue> o)
            {
                if (!o.Ok) { done(o); return; }
                var character = o.Data.Get("character");
                done(McpOutcome<JsonValue>.Success(character.Kind == JsonKind.Object ? character : o.Data));
            });
        }

        /// <summary>
        /// get_entity's whole answer for a character: the entity plus equipped/carried gear, recent
        /// interactions and needs, which the sheet shows.
        /// </summary>
        public IEnumerator GetCharacterDetail(string id, Action<McpOutcome<JsonValue>> done)
        {
            if (!_s.HasCampaign)
            {
                done(McpOutcome<JsonValue>.Fail("NO_CAMPAIGN", "pick a campaign first."));
                yield break;
            }
            string entityId = (id ?? string.Empty).Trim();
            if (entityId.Length == 0)
            {
                done(McpOutcome<JsonValue>.Fail("NO_ID", "no character selected."));
                yield break;
            }
            if (entityId.IndexOf('/') < 0) { entityId = "chars/" + entityId; }
            var args = CampaignArgs();
            args.ObjectValue["entityId"] = JsonValue.FromString(entityId);
            args.ObjectValue["includeCombatDetail"] = JsonValue.FromBool(true); // the sheet shows each feature's rule text
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, _s.Config.ActiveConnector(), "get_entity", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                done(McpOutcome<JsonValue>.Fail(result != null ? result.ErrorCode : "MCP", result != null ? result.ErrorMessage : "no response"));
                yield break;
            }
            if (result.Data.Data.Kind != JsonKind.Object)
            {
                done(McpOutcome<JsonValue>.Fail("PROTOCOL", entityId + " is not a character."));
                yield break;
            }
            done(McpOutcome<JsonValue>.Success(result.Data.Data));
        }

        public bool UseItem(InventoryItem item)
        {
            return SendPlayerText("[Table action] " + _s.PcId + " uses " + ItemRef(item) + ". Commit it.");
        }

        public bool ToggleEquip(InventoryItem item)
        {
            string verb = item.Equipped ? "unequips" : "equips";
            return SendPlayerText("[Table action] " + _s.PcId + " " + verb + " " + ItemRef(item) + ". Commit it.");
        }

        private static string ItemRef(InventoryItem item)
        {
            return item.Name + (string.IsNullOrEmpty(item.Id) ? string.Empty : " (" + item.Id + ")");
        }

        public IEnumerator TrackCompanion(string id)
        {
            id = (id ?? string.Empty).Trim();
            if (id.Length == 0 || _s.CompanionIds.Contains(id)) { yield break; }
            _s.CompanionIds.Add(id);
            SaveCompanionIds();
            yield return LoadCompanions();
        }

        public void UntrackCompanion(string id)
        {
            if (!_s.CompanionIds.Remove(id)) { return; }
            _s.Companions.RemoveAll(delegate (CompanionEntry e) { return e.Id == id; });
            SaveCompanionIds();
            _s.Notify(StateArea.Companions);
        }

        public IEnumerator LoadCompanions()
        {
            if (!_s.TryBeginBusy("companions")) { yield break; }
            try
            {
                var entries = new List<CompanionEntry>();
                foreach (string id in _s.CompanionIds.ToArray())
                {
                    McpOutcome<JsonValue> result = null;
                    yield return GetCharacter(id, delegate (McpOutcome<JsonValue> o) { result = o; });
                    entries.Add(new CompanionEntry
                    {
                        Id = id,
                        Companion = result.Ok ? Companion.FromEntity(result.Data) : null,
                        Error = result.Ok ? string.Empty : result.ErrorMessage,
                    });
                }
                _s.Companions.Clear();
                _s.Companions.AddRange(entries);
                _s.Notify(StateArea.Companions);
            }
            finally { _s.EndBusy("companions"); }
        }
    }
}
