using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The party half of the client: PC character sheet, inventory, and
    /// companion tracking. Sheets come from get_entity; gear comes from the
    /// start_session party roster (get_entity carries no items). Item actions
    /// go through the DM driver as in-character requests (the driver owns the
    /// commit schema), never as hand-built take_turn payloads from the UI.
    /// </summary>
    public static class PartyPanels
    {
        // ---- Character sheet ----

        public static void BuildCharacterPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "CharScroll");
            var col = VaultTheme.Column(scroll.content, "CharCol", 10);

            var idRow = VaultTheme.Row(col.transform, "IdRow", 8);
            var idInput = VaultTheme.MakeInput(idRow.transform, "PcId", "PC id (filled when a session starts), e.g. chars/lyra", false);
            idInput.text = ctx.PcId;
            var refresh = VaultTheme.GoldButton(idRow.transform, "Refresh", "Load", 14);
            refresh.GetComponent<LayoutElement>().minWidth = 100;

            Transform body = VaultTheme.Column(col.transform, "CharBody", 10).transform;
            refresh.onClick.AddListener(delegate
            {
                // Blank input keeps the id start_session filled in.
                string typed = idInput.text.Trim();
                if (typed.Length > 0) { ctx.PcId = typed; }
                idInput.text = ctx.PcId;
                PlayerPrefs.SetString("vault.pcid", ctx.PcId);
                PlayerPrefs.Save();
                ui.StartCoroutine(LoadSheet(ctx, body));
            });
            if (!string.IsNullOrEmpty(ctx.PcId)) { ui.StartCoroutine(LoadSheet(ctx, body)); }
            else
            {
                var hint = VaultTheme.MakeText(body.transform, "Hint", VaultTheme.BodySize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                hint.text = "Start a session (Session tab) to fill in your PC, or enter a PC id above and press Load.";
                VaultTheme.FitVertical(hint);
            }
        }

        private static IEnumerator LoadSheet(VaultUiContext ctx, Transform body)
        {
            VaultTheme.ClearChildren(body);
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = "Loading character sheet\u2026";
            VaultTheme.FitVertical(status);

            McpOutcome<JsonValue> result = null;
            yield return GetCharacter(ctx, ctx.PcId, delegate (McpOutcome<JsonValue> o) { result = o; });

            VaultTheme.ClearChildren(body);
            if (!result.Ok)
            {
                FailLine(body, result.ErrorMessage);
                yield break;
            }
            ctx.LastPcEntity = result.Data;
            var sheet = PcSheet.FromEntity(result.Data);
            if (string.IsNullOrEmpty(sheet.Ruleset)) { sheet.Ruleset = ctx.Prompts.Ruleset; }
            RenderSheet(body, sheet);
        }

        /// <summary>
        /// get_entity for a character in the active campaign. Bare ids get the
        /// server's chars/ prefix; the sheet itself is data.character.
        /// </summary>
        internal static IEnumerator GetCharacter(VaultUiContext ctx, string id, System.Action<McpOutcome<JsonValue>> done)
        {
            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                done(McpOutcome<JsonValue>.Fail("NO_CAMPAIGN", "pick a campaign first (Campaigns tab)."));
                yield break;
            }
            string entityId = (id ?? string.Empty).Trim();
            if (entityId.Length == 0)
            {
                done(McpOutcome<JsonValue>.Fail("NO_ID", "no character id."));
                yield break;
            }
            if (entityId.IndexOf('/') < 0) { entityId = "chars/" + entityId; }
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ctx.Prompts.CampaignSlug);
            args.ObjectValue["entityId"] = JsonValue.FromString(entityId);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, ctx.Config.ActiveConnector(), "get_entity", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (!result.Ok)
            {
                done(McpOutcome<JsonValue>.Fail(result.ErrorCode, result.ErrorMessage));
                yield break;
            }
            var character = result.Data.Data.Get("character");
            var entity = character.Kind == JsonKind.Object ? character : result.Data.Data;
            if (entity.Kind != JsonKind.Object)
            {
                done(McpOutcome<JsonValue>.Fail("PROTOCOL", entityId + " is not a character."));
                yield break;
            }
            done(McpOutcome<JsonValue>.Success(entity));
        }

        private static void RenderSheet(Transform body, PcSheet sheet)
        {
            var name = VaultTheme.MakeText(body.transform, "Name", 30, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            name.text = TextSanitizer.Clean(sheet.Name, 80).ToUpperInvariant();
            VaultTheme.FitVertical(name);

            var sub = VaultTheme.MakeText(body.transform, "Sub", VaultTheme.BodySize - 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            string level = string.IsNullOrEmpty(sheet.Level) ? string.Empty : "Level " + sheet.Level + " · ";
            sub.text = level + sheet.Ruleset + (string.IsNullOrEmpty(sheet.Location) ? string.Empty : " · " + sheet.Location);
            VaultTheme.FitVertical(sub);

            if (sheet.MaxHp > 0)
            {
                var hpLabel = VaultTheme.MakeText(body.transform, "HpNumbers", VaultTheme.SubHeaderSize, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
                hpLabel.text = "HP  " + sheet.CurrentHp + " / " + sheet.MaxHp;
                VaultTheme.FitVertical(hpLabel);
                VaultTheme.HealthBar(body.transform, "HpBar", sheet.HpFraction, sheet.CurrentHp + " / " + sheet.MaxHp);
            }

            var chips = VaultTheme.Row(body.transform, "Chips", 8);
            if (!string.IsNullOrEmpty(sheet.ArmorClass)) { Chip(chips.transform, "AC " + sheet.ArmorClass, VaultTheme.Arcane); }
            foreach (string condition in sheet.Conditions)
            {
                Chip(chips.transform, TextSanitizer.Clean(condition, 40), VaultTheme.Blood);
            }

            if (sheet.Stats.Count > 0)
            {
                var statsTitle = VaultTheme.MakeText(body.transform, "StatsTitle", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
                statsTitle.text = "ATTRIBUTES";
                VaultTheme.FitVertical(statsTitle);
                var grid = new GameObject("StatsGrid");
                grid.transform.SetParent(body.transform, false);
                var gridLayout = grid.AddComponent<GridLayoutGroup>();
                gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                gridLayout.constraintCount = 2;
                gridLayout.spacing = new Vector2(12, 6);
                gridLayout.cellSize = new Vector2(560, 30);
                foreach (var kv in sheet.Stats)
                {
                    var row = VaultTheme.Row(grid.transform, "Stat", 8);
                    var key = VaultTheme.MakeText(row.transform, "Key", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Bold, VaultTheme.BodyFont);
                    key.text = kv.Key.ToUpperInvariant();
                    key.GetComponent<RectTransform>().sizeDelta = new Vector2(220, 30);
                    var value = VaultTheme.MakeText(row.transform, "Value", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
                    value.text = TextSanitizer.Clean(kv.Value, 120);
                }
            }
        }

        private static void Chip(Transform parent, string text, Color color)
        {
            var chip = VaultTheme.PanelBox(parent, "Chip", VaultTheme.PanelRaised);
            chip.AddComponent<LayoutElement>().minHeight = 30;
            var label = VaultTheme.MakeText(chip.transform, "ChipLabel", VaultTheme.SmallSize, color, FontStyle.Bold, VaultTheme.BodyFont);
            label.text = text;
            label.alignment = TextAnchor.MiddleCenter;
            VaultTheme.Stretch(label.GetComponent<RectTransform>(), 10, 10, 2, 2);
        }

        private static void FailLine(Transform body, string message)
        {
            var fail = VaultTheme.MakeText(body.transform, "Fail", VaultTheme.BodySize - 1, VaultTheme.Blood, FontStyle.Normal, VaultTheme.BodyFont);
            fail.text = "Could not load: " + TextSanitizer.Clean(message, 300);
            VaultTheme.FitVertical(fail);
        }

        // ---- Inventory ----

        public static void BuildInventoryPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "InvScroll");
            var col = VaultTheme.Column(scroll.content, "InvCol", 10);

            var topRow = VaultTheme.Row(col.transform, "TopRow", 8);
            var title = VaultTheme.MakeText(topRow.transform, "Title", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            title.text = "INVENTORY";
            VaultTheme.FitVertical(title);
            var refresh = VaultTheme.GoldButton(topRow.transform, "Refresh", "Refresh", 14);
            Tooltip.Attach(refresh.gameObject, "Re-reads the party roster (start_session resumes the open session) for current gear.");

            Transform body = VaultTheme.Column(col.transform, "InvBody", 8).transform;
            refresh.onClick.AddListener(delegate { ui.StartCoroutine(LoadInventory(ctx, ui, body, true)); });
            ui.StartCoroutine(LoadInventory(ctx, ui, body, false));
        }

        private static IEnumerator LoadInventory(VaultUiContext ctx, VaultClientUI ui, Transform body, bool forceRefresh)
        {
            VaultTheme.ClearChildren(body);
            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                FailLine(body, "pick a campaign first (Campaigns tab).");
                yield break;
            }
            if (ctx.Session == null && !forceRefresh)
            {
                // Never open a session as a side effect of building the tab.
                FailLine(body, "no session yet: start one (Session tab) or press Refresh.");
                yield break;
            }
            if (forceRefresh)
            {
                var args = JsonValue.NewObject();
                args.ObjectValue["campaignName"] = JsonValue.FromString(ctx.Prompts.CampaignSlug);
                McpOutcome<ToolPayload> result = null;
                yield return ctx.Mcp.CallToolData(ctx.Config, "play", "start_session", args,
                    delegate (McpOutcome<ToolPayload> o) { result = o; });
                if (!result.Ok)
                {
                    FailLine(body, result.ErrorMessage);
                    yield break;
                }
                ui.ApplySession(SessionDigest.FromResult(result.Data.Data));
            }
            DashboardMember member = null;
            foreach (var m in ctx.Session.Party)
            {
                if (m.Id == ctx.PcId) { member = m; break; }
            }
            if (member == null)
            {
                FailLine(body, "no party member " + (string.IsNullOrEmpty(ctx.PcId) ? "selected" : ctx.PcId) + " in this campaign.");
                yield break;
            }
            var items = new System.Collections.Generic.List<InventoryItem>();
            foreach (string n in member.Equipped) { items.Add(new InventoryItem { Name = n, Equipped = true }); }
            foreach (string n in member.Carried) { items.Add(new InventoryItem { Name = n, Equipped = false }); }
            if (items.Count == 0)
            {
                var empty = VaultTheme.MakeText(body.transform, "Empty", VaultTheme.BodySize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                empty.text = "No gear recorded for this character.";
                VaultTheme.FitVertical(empty);
                yield break;
            }
            RenderGroup(body, ctx, ui, items, true, "EQUIPPED");
            RenderGroup(body, ctx, ui, items, false, "CARRIED");
        }

        private static void RenderGroup(Transform body, VaultUiContext ctx, VaultClientUI ui, System.Collections.Generic.List<InventoryItem> items, bool equipped, string title)
        {
            bool any = false;
            foreach (var item in items) { if (item.Equipped == equipped) { any = true; break; } }
            if (!any) { return; }
            var header = VaultTheme.MakeText(body.transform, "Group" + title, VaultTheme.SmallSize + 1, equipped ? VaultTheme.Gold : VaultTheme.Muted, FontStyle.Bold, VaultTheme.BodyFont);
            header.text = title;
            VaultTheme.FitVertical(header);
            foreach (var item in items)
            {
                if (item.Equipped != equipped) { continue; }
                RenderItemRow(body, ctx, ui, item);
            }
        }

        private static void RenderItemRow(Transform body, VaultUiContext ctx, VaultClientUI ui, InventoryItem item)
        {
            var card = VaultTheme.PanelBox(body.transform, "Item", VaultTheme.Panel);
            card.AddComponent<LayoutElement>().minHeight = 64;
            var cardFit = card.AddComponent<ContentSizeFitter>();
            cardFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var row = VaultTheme.Row(card.transform, "ItemRow", 10);
            VaultTheme.Stretch(row.GetComponent<RectTransform>(), 10, 10, 6, 6);

            var dot = new GameObject("Rarity");
            dot.transform.SetParent(row.transform, false);
            dot.AddComponent<Image>().color = RarityColor(item.Rarity);
            var dotLayout = dot.AddComponent<LayoutElement>();
            dotLayout.minWidth = 10;
            dotLayout.minHeight = 10;

            var col = VaultTheme.Column(row.transform, "ItemCol", 2);
            col.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;
            var nameRow = VaultTheme.Row(col.transform, "NameRow", 8);
            var name = VaultTheme.MakeText(nameRow.transform, "Name", VaultTheme.BodySize, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
            name.text = TextSanitizer.Clean(item.Name, 80);
            VaultTheme.FitVertical(name);
            name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            if (item.Quantity != "1")
            {
                var qty = VaultTheme.MakeText(nameRow.transform, "Qty", VaultTheme.SmallSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.BodyFont);
                qty.text = "\u00d7" + TextSanitizer.Clean(item.Quantity, 12);
                VaultTheme.FitVertical(qty);
            }
            var sub = VaultTheme.MakeText(col.transform, "Sub", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            string detail = item.Slot;
            if (!string.IsNullOrEmpty(item.Rarity)) { detail += (detail.Length > 0 ? " · " : string.Empty) + item.Rarity; }
            if (item.Equipped) { detail += (detail.Length > 0 ? " · " : string.Empty) + "equipped"; }
            sub.text = TextSanitizer.Clean(detail, 120);
            VaultTheme.FitVertical(sub);

            var use = VaultTheme.MakeButton(row.transform, "Use", "Use", 13);
            use.GetComponent<LayoutElement>().minWidth = 70;
            string captured = item.Name + (string.IsNullOrEmpty(item.Id) ? string.Empty : " (" + item.Id + ")");
            use.onClick.AddListener(delegate
            {
                ui.SendChatText("OOC: " + ctx.PcId + " uses " + captured + ". Commit it and narrate briefly.");
            });
            var equip = VaultTheme.MakeButton(row.transform, "Equip", item.Equipped ? "Unequip" : "Equip", 13);
            equip.GetComponent<LayoutElement>().minWidth = 90;
            equip.onClick.AddListener(delegate
            {
                string verb = item.Equipped ? "unequips" : "equips";
                ui.SendChatText("OOC: " + ctx.PcId + " " + verb + " " + captured + ". Commit it and narrate briefly.");
            });
        }

        private static Color RarityColor(string rarity)
        {
            string r = (rarity ?? string.Empty).ToLowerInvariant();
            if (r.Contains("legend")) { return new Color(1f, 0.5f, 0f); }
            if (r.Contains("rare") && !r.Contains("uncommon") && !r.Contains("common")) { return VaultTheme.Arcane; }
            if (r.Contains("uncommon")) { return VaultTheme.Leaf; }
            if (r.Contains("magic") || r.Contains("enchant")) { return VaultTheme.Arcane; }
            return VaultTheme.Faint;
        }

        // ---- Companions ----

        public static void BuildCompanionsPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "CompScroll");
            var col = VaultTheme.Column(scroll.content, "CompCol", 10);

            var addRow = VaultTheme.Row(col.transform, "AddRow", 8);
            var idInput = VaultTheme.MakeInput(addRow.transform, "CompId", "companion character id", false);
            var add = VaultTheme.GoldButton(addRow.transform, "Add", "Track", 14);
            add.GetComponent<LayoutElement>().minWidth = 90;

            Transform body = VaultTheme.Column(col.transform, "CompBody", 8).transform;
            add.onClick.AddListener(delegate
            {
                string id = idInput.text.Trim();
                if (!string.IsNullOrEmpty(id) && !ctx.CompanionIds.Contains(id))
                {
                    ctx.CompanionIds.Add(id);
                    ui.SaveCompanionIds();
                    idInput.text = string.Empty;
                    ui.StartCoroutine(LoadCompanions(ctx, ui, body));
                }
            });
            ui.StartCoroutine(LoadCompanions(ctx, ui, body));
        }

        private static IEnumerator LoadCompanions(VaultUiContext ctx, VaultClientUI ui, Transform body)
        {
            VaultTheme.ClearChildren(body);
            if (ctx.CompanionIds.Count == 0)
            {
                var hint = VaultTheme.MakeText(body.transform, "Hint", VaultTheme.BodySize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                hint.text = "No companions tracked. Party companions are added automatically when a session starts; add any other character id above (mounts, hirelings, familiars, raised minions).";
                VaultTheme.FitVertical(hint);
                yield break;
            }
            foreach (string id in ctx.CompanionIds.ToArray())
            {
                string captured = id;
                McpOutcome<JsonValue> result = null;
                yield return GetCharacter(ctx, captured, delegate (McpOutcome<JsonValue> o) { result = o; });
                Companion comp = result.Ok ? Companion.FromEntity(result.Data) : null;
                RenderCompanion(ctx, ui, body, captured, comp, result.Ok ? null : result.ErrorMessage);
            }
        }

        private static void RenderCompanion(VaultUiContext ctx, VaultClientUI ui, Transform list, string id, Companion comp, string error)
        {
            var card = VaultTheme.PanelBox(list, "Companion", VaultTheme.Panel);
            card.AddComponent<LayoutElement>().minHeight = 76;
            var cardFit = card.AddComponent<ContentSizeFitter>();
            cardFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var row = VaultTheme.Row(card.transform, "CompRow", 10);
            VaultTheme.Stretch(row.GetComponent<RectTransform>(), 10, 10, 8, 8);
            var col = VaultTheme.Column(row.transform, "CompCol", 2);
            col.GetComponent<VerticalLayoutGroup>().childForceExpandWidth = true;

            if (comp == null)
            {
                var fail = VaultTheme.MakeText(col.transform, "Fail", VaultTheme.BodySize - 1, VaultTheme.Blood, FontStyle.Normal, VaultTheme.BodyFont);
                fail.text = id + ": " + TextSanitizer.Clean(error ?? "not found", 200);
                VaultTheme.FitVertical(fail);
            }
            else
            {
                var nameRow = VaultTheme.Row(col.transform, "NameRow", 8);
                var name = VaultTheme.MakeText(nameRow.transform, "Name", VaultTheme.SubHeaderSize - 1, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
                name.text = TextSanitizer.Clean(comp.Name, 60);
                VaultTheme.FitVertical(name);
                name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
                if (comp.IsMinion) { Badge(nameRow.transform, "MINION", VaultTheme.Gold); }
                if (!string.IsNullOrEmpty(comp.Stance)) { Badge(nameRow.transform, comp.Stance, VaultTheme.Arcane); }
                var sub = VaultTheme.MakeText(col.transform, "Sub", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
                string detail = comp.Kind;
                if (!string.IsNullOrEmpty(comp.Location)) { detail += (detail.Length > 0 ? " · " : string.Empty) + comp.Location; }
                sub.text = TextSanitizer.Clean(detail, 140);
                VaultTheme.FitVertical(sub);
                if (comp.MaxHp > 0)
                {
                    VaultTheme.HealthBar(col.transform, "Hp", comp.CurrentHp / comp.MaxHp, comp.CurrentHp + " / " + comp.MaxHp);
                }
            }

            var remove = VaultTheme.MakeButton(row.transform, "Remove", "Untrack", 13);
            remove.GetComponent<LayoutElement>().minWidth = 80;
            string trackedId = id;
            Transform listRef = list;
            remove.onClick.AddListener(delegate
            {
                ctx.CompanionIds.Remove(trackedId);
                ui.SaveCompanionIds();
                ui.StartCoroutine(LoadCompanions(ctx, ui, listRef));
            });
        }

        private static void Badge(Transform parent, string text, Color color)
        {
            var badge = VaultTheme.PanelBox(parent, "Badge", VaultTheme.PanelRaised);
            badge.AddComponent<LayoutElement>().minHeight = 24;
            var label = VaultTheme.MakeText(badge.transform, "BadgeLabel", VaultTheme.SmallSize - 1, color, FontStyle.Bold, VaultTheme.BodyFont);
            label.text = TextSanitizer.Clean(text, 30).ToUpperInvariant();
            label.alignment = TextAnchor.MiddleCenter;
            VaultTheme.Stretch(label.GetComponent<RectTransform>(), 8, 8, 0, 0);
        }
    }
}
