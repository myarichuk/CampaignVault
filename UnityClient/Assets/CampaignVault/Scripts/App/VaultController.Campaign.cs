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
    /// <summary>The campaign list, deleting a campaign, searching the world.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // Campaigns + world
        // =====================================================================

        public IEnumerator ListCampaigns()
        {
            if (!_s.TryBeginBusy("campaigns")) { yield break; }
            try
            {
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "build", "list_campaigns", JsonValue.NewObject(),
                    delegate (McpOutcome<ToolPayload> o) { result = o; });
                _s.CampaignsLoaded = true;
                if (result == null || !result.Ok)
                {
                    _s.CampaignsError = result != null ? result.ErrorMessage : "no response";
                    _s.Notify(StateArea.Campaigns);
                    yield break;
                }
                _s.CampaignsError = string.Empty;
                _s.Campaigns.Clear();
                _s.Campaigns.AddRange(ExtractCampaigns(result.Data.Data));
                _s.Notify(StateArea.Campaigns);
            }
            finally { _s.EndBusy("campaigns"); }
        }

        /// <summary>list_campaigns data: [{name (the slug), displayName, system, createdAt}].</summary>
        internal static List<CampaignRow> ExtractCampaigns(JsonValue data)
        {
            var rows = new List<CampaignRow>();
            if (data.Kind != JsonKind.Array || data.ArrayValue == null) { return rows; }
            foreach (var entry in data.ArrayValue)
            {
                string slug = entry.GetStringAny(new[] { "name", "slug" }, string.Empty);
                if (string.IsNullOrEmpty(slug)) { continue; }
                rows.Add(new CampaignRow
                {
                    Slug = slug,
                    Display = entry.GetString("displayName", slug),
                    System = entry.GetString("system", string.Empty),
                });
            }
            return rows;
        }

        /// <summary>Irreversible. The view owns the confirmation; the server requires the slug echoed back.</summary>
        public IEnumerator DeleteCampaign(string slug)
        {
            if (!_s.TryBeginBusy("delete:" + slug)) { yield break; }
            try
            {
                var args = JsonValue.NewObject();
                args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
                args.ObjectValue["confirmName"] = JsonValue.FromString(slug);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, "build", "delete_campaign", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (result != null && result.Ok)
                {
                    if (_s.CampaignSlug == slug) { SelectCampaign(string.Empty, null); }
                    if (_s.Store != null) { _s.Store.Delete(slug); }
                    _prefs.Delete(PrefKeys.OpenSessionPrefix + slug);
                    _prefs.Save();
                    _s.RaiseToast("Campaign “" + slug + "” deleted.", ToastKind.Success);
                }
                else
                {
                    _s.RaiseToast("Delete failed: " + (result != null ? result.ErrorMessage : "no response"), ToastKind.Error);
                }
            }
            finally { _s.EndBusy("delete:" + slug); }
            yield return ListCampaigns();
        }

        private const int MaxSearchHits = 12;

        public IEnumerator SearchWorld(string query)
        {
            if (!RequireCampaign(delegate (string m) { _s.SearchError = m; _s.Notify(StateArea.Search); })) { yield break; }
            if (!_s.TryBeginBusy("search")) { yield break; }
            try
            {
                var args = CampaignArgs();
                args.ObjectValue["query"] = JsonValue.FromString(query ?? string.Empty);
                McpOutcome<ToolPayload> result = null;
                yield return _s.Mcp.CallToolData(_s.Config, _s.Config.ActiveConnector(), "search_world", args,
                    delegate (McpOutcome<ToolPayload> o) { result = o; });
                _s.SearchResults.Clear();
                if (result == null || !result.Ok)
                {
                    _s.SearchError = result != null ? result.ErrorMessage : "no response";
                    _s.SearchSummary = string.Empty;
                    _s.Notify(StateArea.Search);
                    yield break;
                }
                _s.SearchError = string.Empty;
                _s.SearchSummary = result.Data.Summary;
                foreach (var match in result.Data.Data.GetArray("matches"))
                {
                    if (_s.SearchResults.Count >= MaxSearchHits) { break; }
                    if (match.Kind != JsonKind.Object)
                    {
                        _s.SearchResults.Add(new SearchHit { Title = match.ToJson() });
                        continue;
                    }
                    _s.SearchResults.Add(new SearchHit
                    {
                        Title = match.GetStringAny(new[] { "name", "title", "subject", "id" }, "(untitled)"),
                        Id = match.GetString("id", string.Empty),
                        Detail = match.GetStringAny(new[] { "description", "summary", "content", "text", "details" }, string.Empty),
                    });
                }
                _s.Notify(StateArea.Search);
            }
            finally { _s.EndBusy("search"); }
        }

        private JsonValue CampaignArgs()
        {
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(_s.CampaignSlug);
            return args;
        }
    }
}
