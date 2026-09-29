using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// At-a-glance table state, all engine truth fetched from MCP: party HP /
    /// status / location / needs, open quests with deadlines, the current
    /// handoff digest (or server digest), and the party fingerprint the next
    /// take_turn must echo. Refresh resumes the session — it never opens a
    /// second one.
    /// </summary>
    public static class DashboardPanel
    {
        public static void BuildDashboardPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "DashScroll");
            var col = VaultTheme.Column(scroll.content, "DashCol", 10);

            var topRow = VaultTheme.Row(col.transform, "TopRow", 8);
            var title = VaultTheme.MakeText(topRow.transform, "Title", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            title.text = "DASHBOARD";
            VaultTheme.FitVertical(title);
            var refresh = VaultTheme.GoldButton(topRow.transform, "Refresh", "Refresh", 14);
            refresh.GetComponent<LayoutElement>().minWidth = 110;
            Tooltip.Attach(refresh.gameObject,
                "Re-reads start_session (resumes the open session, never forks a new one) for fresh HP, quests, and pressures.");

            Transform body = VaultTheme.Column(col.transform, "DashBody", 10).transform;
            refresh.onClick.AddListener(delegate { ui.StartCoroutine(LoadDashboard(ctx, ui, body)); });
            var hint = VaultTheme.MakeText(body.transform, "Hint", VaultTheme.BodySize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            hint.text = "Press Refresh to pull the live table state.";
            VaultTheme.FitVertical(hint);
        }

        private static IEnumerator LoadDashboard(VaultUiContext ctx, VaultClientUI ui, Transform body)
        {
            VaultTheme.ClearChildren(body);
            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                FailLine(body, "pick a campaign first (Campaigns tab).");
                yield break;
            }
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = "Reading table state\u2026";
            VaultTheme.FitVertical(status);

            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ctx.Prompts.CampaignSlug);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "play", "start_session", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            VaultTheme.ClearChildren(body);
            if (result == null || !result.Ok)
            {
                FailLine(body, result != null ? result.ErrorMessage : "no response");
                yield break;
            }
            var digest = SessionDigest.FromResult(result.Data.Data);
            ui.ApplySession(digest);
            RenderDashboard(body, digest);
        }

        private static void RenderDashboard(Transform body, SessionDigest digest)
        {
            var head = VaultTheme.MakeText(body.transform, "Head", 26, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            head.text = string.IsNullOrEmpty(digest.CampaignDisplay) ? digest.CampaignSlug.ToUpperInvariant() : digest.CampaignDisplay.ToUpperInvariant();
            VaultTheme.FitVertical(head);
            var sub = VaultTheme.MakeText(body.transform, "Sub", VaultTheme.BodySize - 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            sub.text = "Session " + digest.SessionNumber + (digest.Resumed ? " (resumed)" : "")
                + " · " + digest.System + " · " + digest.Time;
            VaultTheme.FitVertical(sub);

            Section(body, "PARTY");
            if (digest.Party.Count == 0)
            {
                MutedLine(body, "No party on record yet.");
            }
            foreach (var member in digest.Party)
            {
                var card = VaultTheme.PanelBox(body.transform, "Member", VaultTheme.Panel);
                card.AddComponent<LayoutElement>().minHeight = 96;
                var fit = card.AddComponent<ContentSizeFitter>();
                fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                var inner = VaultTheme.Column(card.transform, "Inner", 4);
                var pad = inner.GetComponent<VerticalLayoutGroup>();
                pad.padding = new RectOffset(10, 10, 8, 8);

                var nameRow = VaultTheme.Row(inner.transform, "NameRow", 8);
                var name = VaultTheme.MakeText(nameRow.transform, "Name", VaultTheme.SubHeaderSize - 1, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
                name.text = TextSanitizer.Clean(member.Name, 60) + (member.IsPc ? "" : "  ·  ally");
                VaultTheme.FitVertical(name);
                name.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
                if (!string.IsNullOrEmpty(member.ClassLevel))
                {
                    var cls = VaultTheme.MakeText(nameRow.transform, "Class", VaultTheme.SmallSize, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
                    cls.text = TextSanitizer.Clean(member.ClassLevel, 60);
                    VaultTheme.FitVertical(cls);
                }

                VaultTheme.HealthBar(inner.transform, "Hp", member.HpFraction, "HP " + member.HpText);

                var chips = VaultTheme.Row(inner.transform, "Chips", 8);
                if (!string.IsNullOrEmpty(member.Ac)) { Chip(chips.transform, "AC " + member.Ac, VaultTheme.Arcane); }
                if (!string.IsNullOrEmpty(member.Location)) { Chip(chips.transform, member.Location, VaultTheme.Muted); }
                if (!string.IsNullOrEmpty(member.Activity)) { Chip(chips.transform, member.Activity, VaultTheme.Muted); }
                foreach (var condition in member.Conditions)
                {
                    Chip(chips.transform, condition, VaultTheme.Blood);
                }
                foreach (var need in member.Needs)
                {
                    Chip(chips.transform, need.Key + " " + need.Value, VaultTheme.Gold);
                }
                if (member.MemoryCount > 0)
                {
                    var mem = VaultTheme.MakeText(inner.transform, "Mem", VaultTheme.SmallSize, VaultTheme.Faint, FontStyle.Italic, VaultTheme.BodyFont);
                    mem.text = member.MemoryCount + " memories"
                        + (member.KeyMemories.Count > 0 ? ": " + string.Join(", ", member.KeyMemories.ToArray()) : string.Empty);
                    VaultTheme.FitVertical(mem);
                }
            }

            Section(body, "QUESTS");
            if (digest.Quests.Count == 0)
            {
                MutedLine(body, "No open quests.");
            }
            foreach (var quest in digest.Quests)
            {
                var row = VaultTheme.Row(body.transform, "Quest", 8);
                var dot = new GameObject("Dot");
                dot.transform.SetParent(row.transform, false);
                dot.AddComponent<Image>().color = quest.Overdue ? VaultTheme.Blood : VaultTheme.Leaf;
                var dotLayout = dot.AddComponent<LayoutElement>();
                dotLayout.minWidth = 10;
                dotLayout.minHeight = 10;
                var text = VaultTheme.MakeText(row.transform, "QuestText", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
                text.text = TextSanitizer.Clean(quest.Title, 100)
                    + "  ·  " + quest.OpenObjectives + " open"
                    + (string.IsNullOrEmpty(quest.Deadline) ? string.Empty : "  ·  " + quest.Deadline)
                    + (quest.Overdue ? "  ·  OVERDUE" : string.Empty);
                VaultTheme.FitVertical(text);
                text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            }

            Section(body, "STORY SO FAR");
            if (digest.Handoff != null && !string.IsNullOrEmpty(digest.Handoff.LastSession))
            {
                var h = digest.Handoff;
                var last = VaultTheme.MakeText(body.transform, "Last", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Italic, VaultTheme.BodyFont);
                last.text = "\u201C" + TextSanitizer.Clean(h.LastSession, 600) + "\u201D"
                    + "  — session " + h.FromSession + (h.Checkpoint ? " (checkpoint)" : string.Empty);
                VaultTheme.FitVertical(last);
                if (h.OpenThreads.Count > 0)
                {
                    var threads = VaultTheme.MakeText(body.transform, "Threads", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
                    threads.text = "Open: " + string.Join("  ·  ", h.OpenThreads.ToArray());
                    VaultTheme.FitVertical(threads);
                }
            }
            else if (!string.IsNullOrEmpty(digest.RecentDigest))
            {
                var recent = VaultTheme.MakeText(body.transform, "Recent", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
                recent.text = TextSanitizer.Clean(digest.RecentDigest, 2000);
                VaultTheme.FitVertical(recent);
            }
            else
            {
                MutedLine(body, "No handoff yet — end the first session to start the chain.");
            }

            if (!string.IsNullOrEmpty(digest.Fingerprint))
            {
                var fp = VaultTheme.MakeText(body.transform, "Fp", VaultTheme.SmallSize - 1, VaultTheme.Faint, FontStyle.Normal, VaultTheme.MonoFont);
                fp.text = "party fingerprint: " + digest.Fingerprint;
                VaultTheme.FitVertical(fp);
            }
        }

        private static void Section(Transform body, string title)
        {
            var text = VaultTheme.MakeText(body.transform, "Section", VaultTheme.SmallSize + 1, VaultTheme.Gold, FontStyle.Bold, VaultTheme.BodyFont);
            text.text = title;
            VaultTheme.FitVertical(text);
        }

        private static void MutedLine(Transform body, string text)
        {
            var line = VaultTheme.MakeText(body.transform, "Muted", VaultTheme.BodySize - 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            line.text = text;
            VaultTheme.FitVertical(line);
        }

        private static void Chip(Transform parent, string text, Color color)
        {
            var chip = VaultTheme.PanelBox(parent, "Chip", VaultTheme.PanelRaised);
            chip.AddComponent<LayoutElement>().minHeight = 30;
            var label = VaultTheme.MakeText(chip.transform, "ChipLabel", VaultTheme.SmallSize, color, FontStyle.Bold, VaultTheme.BodyFont);
            label.text = TextSanitizer.Clean(text, 80);
            label.alignment = TextAnchor.MiddleCenter;
            VaultTheme.Stretch(label.GetComponent<RectTransform>(), 10, 10, 2, 2);
        }

        private static void FailLine(Transform body, string message)
        {
            var fail = VaultTheme.MakeText(body.transform, "Fail", VaultTheme.BodySize - 1, VaultTheme.Blood, FontStyle.Normal, VaultTheme.BodyFont);
            fail.text = "Could not load: " + TextSanitizer.Clean(message, 300);
            VaultTheme.FitVertical(fail);
        }
    }
}
