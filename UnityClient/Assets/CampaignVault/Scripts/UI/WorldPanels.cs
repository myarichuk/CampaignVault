using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The world half of the client: campaign select, event search, and the
    /// plugin/capability allowlist. The Plugins panel does not pretend the
    /// server has per-plugin switches: it lists the tools each connector
    /// advertises and lets the table decide which ones the DM driver may call.
    /// Server plugin verbs ride inside take_turn and stay governed server-side.
    /// </summary>
    public static class WorldPanels
    {
        // ---- Campaigns ----

        public static void BuildCampaignsPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "CampScroll");
            var col = VaultTheme.Column(scroll.content, "CampCol", 10);

            var topRow = VaultTheme.Row(col.transform, "TopRow", 8);
            var title = VaultTheme.MakeText(topRow.transform, "Title", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            title.text = "CAMPAIGNS";
            VaultTheme.FitVertical(title);
            var refresh = VaultTheme.GoldButton(topRow.transform, "Refresh", "Refresh", 14);
            refresh.GetComponent<LayoutElement>().minWidth = 100;
            var guided = VaultTheme.MakeButton(topRow.transform, "NewGuided", "New campaign\u2026", 14);
            guided.GetComponent<LayoutElement>().minWidth = 150;
            guided.onClick.AddListener(delegate { ui.ShowTab("Onboard"); });

            var active = VaultTheme.MakeText(col.transform, "Active", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Italic, VaultTheme.BodyFont);
            VaultTheme.FitVertical(active);

            Transform body = VaultTheme.Column(col.transform, "CampBody", 8).transform;
            refresh.onClick.AddListener(delegate
            {
                MonoRunner.Run(ctx, LoadCampaigns(ctx, ui, body, active));
            });
            MonoRunner.Run(ctx, LoadCampaigns(ctx, ui, body, active));
        }

        private static IEnumerator LoadCampaigns(VaultUiContext ctx, VaultClientUI ui, Transform body, Text active)
        {
            active.text = "Active campaign: " + (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug) ? "(none)" : ctx.Prompts.CampaignSlug);
            VaultTheme.ClearChildren(body);
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = "Listing campaigns\u2026";
            VaultTheme.FitVertical(status);

            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "build", "list_campaigns", JsonValue.NewObject(),
                delegate (McpOutcome<ToolPayload> o) { result = o; });

            VaultTheme.ClearChildren(body);
            if (!result.Ok)
            {
                FailLine(body, result.ErrorMessage);
                yield break;
            }
            List<CampaignRow> campaigns = ExtractCampaigns(result.Data.Data);
            if (campaigns.Count == 0)
            {
                var empty = VaultTheme.MakeText(body.transform, "Empty", VaultTheme.BodySize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                empty.text = "No campaigns yet. Create one in the Onboard tab.";
                VaultTheme.FitVertical(empty);
                yield break;
            }
            foreach (var campaign in campaigns)
            {
                string captured = campaign.Slug;
                string capturedSystem = campaign.System;
                var card = VaultTheme.PanelBox(body.transform, "Campaign", VaultTheme.Panel);
                card.AddComponent<LayoutElement>().minHeight = 52;
                var cardFit = card.AddComponent<ContentSizeFitter>();
                cardFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var row = VaultTheme.Row(card.transform, "CampRow", 10);
                VaultTheme.Stretch(row.GetComponent<RectTransform>(), 10, 10, 6, 6);
                var name = VaultTheme.MakeText(row.transform, "Name", VaultTheme.BodySize, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
                string label = campaign.Display == captured ? captured : campaign.Display + "  (" + captured + ")";
                if (!string.IsNullOrEmpty(capturedSystem)) { label += "  \u00b7  " + capturedSystem; }
                name.text = TextSanitizer.Clean(label, 120);
                VaultTheme.FitVertical(name);
                name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
                if (captured == ctx.Prompts.CampaignSlug)
                {
                    var badge = VaultTheme.MakeText(row.transform, "Active", VaultTheme.SmallSize, VaultTheme.Leaf, FontStyle.Bold, VaultTheme.BodyFont);
                    badge.text = "ACTIVE";
                    VaultTheme.FitVertical(badge);
                }
                else
                {
                    var select = VaultTheme.GoldButton(row.transform, "Select", "Play", 13);
                    select.GetComponent<LayoutElement>().minWidth = 80;
                    select.onClick.AddListener(delegate
                    {
                        ui.SelectCampaign(captured, capturedSystem);
                        active.text = "Active campaign: " + captured;
                        MonoRunner.Run(ctx, LoadCampaigns(ctx, ui, body, active));
                    });
                }
                var delete = VaultTheme.MakeButton(row.transform, "Delete", "Delete", 13);
                delete.GetComponent<LayoutElement>().minWidth = 80;
                var deleteLabel = delete.GetComponentInChildren<Text>();
                bool armed = false;
                Tooltip.Attach(delete.gameObject,
                    "Irreversibly deletes the campaign and every document in it. Two taps: arm, then confirm. The server requires the exact slug as confirmation.");
                delete.onClick.AddListener(delegate
                {
                    if (!armed)
                    {
                        armed = true;
                        deleteLabel.text = "Confirm?";
                        deleteLabel.color = VaultTheme.Blood;
                        return;
                    }
                    var args = JsonValue.NewObject();
                    args.ObjectValue["campaignName"] = JsonValue.FromString(captured);
                    args.ObjectValue["confirmName"] = JsonValue.FromString(captured);
                    MonoRunner.Run(ctx, DeleteCampaign(ctx, ui, body, active, args, captured));
                });
            }
        }

        private static IEnumerator DeleteCampaign(
            VaultUiContext ctx, VaultClientUI ui, Transform body, Text active, JsonValue args, string slug)
        {
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "build", "delete_campaign", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result.Ok)
            {
                if (ctx.Prompts.CampaignSlug == slug) { ui.SelectCampaign(string.Empty, null); }
                ui.Note("Campaign \"" + slug + "\" deleted.");
            }
            else
            {
                ui.Note("Delete failed: " + result.ErrorMessage);
            }
            MonoRunner.Run(ctx, LoadCampaigns(ctx, ui, body, active));
        }

        private sealed class CampaignRow
        {
            public string Slug = string.Empty;
            public string Display = string.Empty;
            public string System = string.Empty;
        }

        /// <summary>list_campaigns data: [{name (the slug), displayName, system, createdAt}].</summary>
        private static List<CampaignRow> ExtractCampaigns(JsonValue data)
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

        // ---- Events ----

        public static void BuildEventsPanel(Transform parent, VaultUiContext ctx)
        {
            var column = VaultTheme.Column(parent, "EventsCol", 8);
            VaultTheme.Stretch(column.GetComponent<RectTransform>(), 8, 8, 8, 8);

            var searchRow = VaultTheme.Row(column.transform, "SearchRow", 8);
            searchRow.AddComponent<LayoutElement>().minHeight = 40;
            var query = VaultTheme.MakeInput(searchRow.transform, "Query", "search campaign events\u2026", false);
            var search = VaultTheme.GoldButton(searchRow.transform, "Search", "Search", 15);
            search.GetComponent<LayoutElement>().minWidth = 110;

            var scroll = VaultTheme.MakeScrollView(column.transform, "EventsScroll");
            scroll.GetComponent<LayoutElement>().flexibleHeight = 1;
            Transform body = scroll.content;

            search.onClick.AddListener(delegate
            {
                MonoRunner.Run(ctx, LoadEvents(ctx, body, query.text.Trim()));
            });
        }

        private static IEnumerator LoadEvents(VaultUiContext ctx, Transform body, string query)
        {
            VaultTheme.ClearChildren(body);
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = "Searching\u2026";
            VaultTheme.FitVertical(status);

            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                VaultTheme.ClearChildren(body);
                FailLine(body, "pick a campaign first (Campaigns tab).");
                yield break;
            }
            var args = JsonValue.NewObject();
            args.ObjectValue["query"] = JsonValue.FromString(query);
            args.ObjectValue["campaignName"] = JsonValue.FromString(ctx.Prompts.CampaignSlug);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, ctx.Config.ActiveConnector(), "search_world", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });

            VaultTheme.ClearChildren(body);
            if (!result.Ok)
            {
                FailLine(body, result.ErrorMessage);
                yield break;
            }
            var hits = SearchHits(result.Data);
            if (hits.Count == 0)
            {
                var none = VaultTheme.MakeText(body.transform, "None", VaultTheme.BodySize - 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                none.text = string.IsNullOrEmpty(result.Data.Summary) ? "No matches." : TextSanitizer.Clean(result.Data.Summary, 300);
                VaultTheme.FitVertical(none);
                yield break;
            }
            foreach (string chunk in hits)
            {
                var card = VaultTheme.PanelBox(body.transform, "Event", VaultTheme.Panel);
                card.AddComponent<LayoutElement>().minHeight = 60;
                var cardFit = card.AddComponent<ContentSizeFitter>();
                cardFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var pad = card.AddComponent<VerticalLayoutGroup>();
                pad.padding = new RectOffset(10, 10, 8, 8);
                pad.childControlWidth = true;
                pad.childControlHeight = false;
                pad.childForceExpandWidth = true;
                pad.childForceExpandHeight = false;
                var text = VaultTheme.MakeText(card.transform, "EventText", VaultTheme.BodySize - 2, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
                text.text = TextSanitizer.Clean(chunk, 1200);
                VaultTheme.FitVertical(text);
            }
        }

        private const int MaxSearchHits = 12;

        /// <summary>
        /// search_world data: {matches: [...]} of mixed entities (characters,
        /// locations, lore, rumors, events...). One card per match: its
        /// name/title/subject, id, and whatever description it carries.
        /// </summary>
        private static List<string> SearchHits(ToolPayload payload)
        {
            var hits = new List<string>();
            foreach (var match in payload.Data.GetArray("matches"))
            {
                if (hits.Count >= MaxSearchHits) { break; }
                if (match.Kind != JsonKind.Object) { hits.Add(match.ToJson()); continue; }
                string title = match.GetStringAny(new[] { "name", "title", "subject", "id" }, "(untitled)");
                string id = match.GetString("id", string.Empty);
                string detail = match.GetStringAny(new[] { "description", "summary", "content", "text", "details" }, string.Empty);
                string line = id.Length > 0 && id != title ? title + "  \u00b7  " + id : title;
                hits.Add(detail.Length > 0 ? line + "\n" + detail : line);
            }
            return hits;
        }

        // ---- Plugins (capability allowlist) ----

        public static void BuildPluginsPanel(Transform parent, VaultUiContext ctx)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "PluginScroll");
            var col = VaultTheme.Column(scroll.content, "PluginCol", 10);

            var note = VaultTheme.MakeText(col.transform, "Note", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            note.text = "Toggle which server capabilities the DM driver may call. Untoggled tools are hidden from the model for this session.";
            VaultTheme.FitVertical(note);

            var topRow = VaultTheme.Row(col.transform, "TopRow", 8);
            var refresh = VaultTheme.GoldButton(topRow.transform, "Refresh", "Reload tool list", 14);
            var all = VaultTheme.MakeButton(topRow.transform, "All", "Enable all", 14);

            Transform body = VaultTheme.Column(col.transform, "PluginBody", 6).transform;
            all.onClick.AddListener(delegate
            {
                ctx.Driver.AllowedTools.Clear();
                MonoRunner.Run(ctx, LoadPlugins(ctx, body));
            });
            refresh.onClick.AddListener(delegate
            {
                ctx.Driver.InvalidateTools();
                MonoRunner.Run(ctx, LoadPlugins(ctx, body));
            });
            MonoRunner.Run(ctx, LoadPlugins(ctx, body));
        }

        private static IEnumerator LoadPlugins(VaultUiContext ctx, Transform body)
        {
            VaultTheme.ClearChildren(body);
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = "Reading advertised tools\u2026";
            VaultTheme.FitVertical(status);

            McpOutcome<List<McpToolInfo>> play = null;
            yield return ctx.Mcp.ListTools(ctx.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { play = o; });
            McpOutcome<List<McpToolInfo>> build = null;
            yield return ctx.Mcp.ListTools(ctx.Config, "build", delegate (McpOutcome<List<McpToolInfo>> o) { build = o; });

            VaultTheme.ClearChildren(body);
            var allNames = new List<string>();
            CollectNames(play, allNames);
            CollectNames(build, allNames);
            RenderToolGroup(ctx, body, "LIVE SESSION (/play)", play, allNames);
            RenderToolGroup(ctx, body, "CAMPAIGN SETUP (/build)", build, allNames);
        }

        private static void CollectNames(McpOutcome<List<McpToolInfo>> outcome, List<string> into)
        {
            if (outcome == null || !outcome.Ok) { return; }
            foreach (var tool in outcome.Data)
            {
                if (!into.Contains(tool.Name)) { into.Add(tool.Name); }
            }
        }

        private static void RenderToolGroup(VaultUiContext ctx, Transform body, string title, McpOutcome<List<McpToolInfo>> outcome, List<string> allNames)
        {
            var header = VaultTheme.MakeText(body.transform, "Group", VaultTheme.SmallSize + 1, VaultTheme.Gold, FontStyle.Bold, VaultTheme.BodyFont);
            header.text = title;
            VaultTheme.FitVertical(header);
            if (outcome == null || !outcome.Ok)
            {
                FailLine(body, outcome != null ? outcome.ErrorMessage : "no response");
                return;
            }
            foreach (var tool in outcome.Data)
            {
                string name = tool.Name;
                var row = VaultTheme.Row(body.transform, "Tool", 8);
                var toggle = VaultTheme.MakeButton(row.transform, "Toggle" + name, string.Empty, 14);
                toggle.GetComponent<LayoutElement>().minWidth = 46;
                var label = toggle.GetComponentInChildren<Text>();
                var desc = VaultTheme.MakeText(row.transform, "Desc", VaultTheme.BodySize - 2, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
                desc.text = TextSanitizer.Clean(name + " — " + tool.Description, 220);
                VaultTheme.FitVertical(desc);

                System.Action repaint = delegate
                {
                    bool enabled = ctx.Driver.AllowedTools.Count == 0 || ctx.Driver.AllowedTools.Contains(name);
                    label.text = enabled ? "[x]" : "[ ]";
                    label.color = enabled ? VaultTheme.Leaf : VaultTheme.Faint;
                };
                repaint();
                toggle.onClick.AddListener(delegate
                {
                    if (ctx.Driver.AllowedTools.Count == 0)
                    {
                        // Empty means "everything on": seed the set with the full
                        // advertised list first so this tap disables just one tool.
                        foreach (string known in allNames) { ctx.Driver.AllowedTools.Add(known); }
                    }
                    if (ctx.Driver.AllowedTools.Contains(name)) { ctx.Driver.AllowedTools.Remove(name); }
                    else { ctx.Driver.AllowedTools.Add(name); }
                    repaint();
                });
            }
        }

        private static void FailLine(Transform body, string message)
        {
            var fail = VaultTheme.MakeText(body.transform, "Fail", VaultTheme.BodySize - 1, VaultTheme.Blood, FontStyle.Normal, VaultTheme.BodyFont);
            fail.text = "Could not load: " + TextSanitizer.Clean(message, 300);
            VaultTheme.FitVertical(fail);
        }
    }

    /// <summary>
    /// Coroutine runner for static panels: VaultClientUI registers itself here
    /// on Awake so panels never need a direct reference to start MCP calls.
    /// </summary>
    public static class MonoRunner
    {
        private static VaultClientUI _owner;

        public static void Bind(VaultClientUI owner) { _owner = owner; }

        public static void Run(VaultUiContext ctx, IEnumerator routine)
        {
            if (_owner != null) { _owner.StartCoroutine(routine); }
        }
    }
}
