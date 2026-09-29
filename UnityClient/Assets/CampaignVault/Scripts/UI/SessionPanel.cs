using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// Session lifecycle as first-class actions: Start (with title), Checkpoint
    /// and End with a cap-counted handoff form, plus downtime via the DM driver.
    /// Tooltips carry the compaction guidance: checkpoint before context
    /// compaction, end folds storySoFar forward — the handoff replaces the raw
    /// event/memory dump at the next start_session.
    /// </summary>
    public static class SessionPanel
    {
        public static void BuildSessionPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "SessionScroll");
            var col = VaultTheme.Column(scroll.content, "SessionCol", 10);

            var statusTitle = VaultTheme.MakeText(col.transform, "Title", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            statusTitle.text = "SESSION";
            VaultTheme.FitVertical(statusTitle);

            var status = VaultTheme.MakeText(col.transform, "Status", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(status);

            var startRow = VaultTheme.Row(col.transform, "StartRow", 8);
            var titleInput = VaultTheme.MakeInput(startRow.transform, "Title", "session title (optional)", false);
            var startButton = VaultTheme.GoldButton(startRow.transform, "Start", "Start session", 14);
            startButton.GetComponent<LayoutElement>().minWidth = 140;
            Tooltip.Attach(startButton.gameObject,
                "Opens (or resumes) a session: returns last handoff, party from the DB, time, quests, pressures. Call once per session, or after a reconnect.");
            startButton.onClick.AddListener(delegate
            {
                ui.StartCoroutine(StartSession(ctx, ui, status, titleInput.text.Trim()));
            });

            AddSectionLabel(col.transform, "HANDOFF  ·  end of session summary");
            var lastInput = VaultTheme.MakeArea(col.transform, "Last", "What happened this session (required, ≤600)", 70);
            var lastCount = Counter(col.transform);
            var storyInput = VaultTheme.MakeArea(col.transform, "Story", "Rolling story-so-far ≤800 (omit to carry the previous fold forward)", 60);
            var storyCount = Counter(col.transform);
            var threadsInput = VaultTheme.MakeArea(col.transform, "Threads", "Open threads, one per line (≤6 × 120)", 60);
            var npcInput = VaultTheme.MakeArea(col.transform, "Npcs", "NPCs in play, one per line: id | stance (≤8 × 80)", 60);
            var intentInput = VaultTheme.MakeInput(col.transform, "Intent", "Party intent ≤200", false);
            var toneInput = VaultTheme.MakeInput(col.transform, "Tone", "Tone note ≤120", false);

            System.Action repaint = delegate
            {
                lastCount.text = lastInput.text.Length + " / 600";
                lastCount.color = lastInput.text.Length > 600 ? VaultTheme.Blood : VaultTheme.Faint;
                storyCount.text = storyInput.text.Length + " / 800";
                storyCount.color = storyInput.text.Length > 800 ? VaultTheme.Blood : VaultTheme.Faint;
            };
            lastInput.onValueChanged.AddListener(delegate (string v) { repaint(); });
            storyInput.onValueChanged.AddListener(delegate (string v) { repaint(); });
            repaint();

            var problems = VaultTheme.MakeText(col.transform, "Problems", VaultTheme.SmallSize + 1, VaultTheme.Blood, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(problems);

            var actionRow = VaultTheme.Row(col.transform, "Actions", 8);
            var checkpointButton = VaultTheme.MakeButton(actionRow.transform, "Checkpoint", "Checkpoint", 14);
            Tooltip.Attach(checkpointButton.gameObject,
                "Stores the handoff but keeps the session open. Use mid-session and right before prompt compaction: the handoff is what survives into the next context.");
            var endButton = VaultTheme.GoldButton(actionRow.transform, "End", "End session", 14);
            Tooltip.Attach(endButton.gameObject,
                "Closes the session with this handoff. Next start_session returns it instead of the raw dump — fold storySoFar forward, don't just append.");

            checkpointButton.onClick.AddListener(delegate
            {
                SubmitHandoff(ctx, ui, status, problems, true,
                    lastInput.text, storyInput.text, threadsInput.text,
                    npcInput.text, intentInput.text, toneInput.text);
            });
            endButton.onClick.AddListener(delegate
            {
                SubmitHandoff(ctx, ui, status, problems, false,
                    lastInput.text, storyInput.text, threadsInput.text,
                    npcInput.text, intentInput.text, toneInput.text);
            });

            AddSectionLabel(col.transform, "DOWNTIME");
            var downtimeRow = VaultTheme.Row(col.transform, "Downtime", 8);
            var daysInput = VaultTheme.MakeInput(downtimeRow.transform, "Days", "days", false);
            daysInput.text = "1";
            daysInput.GetComponent<LayoutElement>().minWidth = 80;
            var restButton = VaultTheme.MakeButton(downtimeRow.transform, "Advance", "Advance time", 14);
            Tooltip.Attach(restButton.gameObject,
                "Days pass, resources recover, rumors decay, deadlines approach. Sent as an in-character request so the DM commits it properly.");
            restButton.onClick.AddListener(delegate
            {
                ui.SendChatText("OOC: advance " + daysInput.text.Trim() + " days and narrate what changes.");
            });

            PaintStatus(ctx, status);
        }

        private static void AddSectionLabel(Transform parent, string title)
        {
            var text = VaultTheme.MakeText(parent, "Section", VaultTheme.SmallSize + 1, VaultTheme.Gold, FontStyle.Bold, VaultTheme.BodyFont);
            text.text = title;
            VaultTheme.FitVertical(text);
        }

        private static Text Counter(Transform parent)
        {
            var text = VaultTheme.MakeText(parent, "Count", VaultTheme.SmallSize - 1, VaultTheme.Faint, FontStyle.Normal, VaultTheme.BodyFont);
            text.alignment = TextAnchor.UpperRight;
            VaultTheme.FitVertical(text);
            return text;
        }

        private static IEnumerator StartSession(VaultUiContext ctx, VaultClientUI ui, Text status, string title)
        {
            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                status.text = "Pick a campaign first (Campaigns tab).";
                status.color = VaultTheme.Blood;
                yield break;
            }
            status.text = "Starting session\u2026";
            status.color = VaultTheme.Parchment;
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ctx.Prompts.CampaignSlug);
            if (!string.IsNullOrEmpty(title)) { args.ObjectValue["title"] = JsonValue.FromString(title); }
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "play", "start_session", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                status.text = "start_session failed: " + (result != null ? result.ErrorMessage : "no response");
                status.color = VaultTheme.Blood;
                yield break;
            }
            ui.ApplySession(SessionDigest.FromResult(result.Data.Data));
            PaintStatus(ctx, status);
            ui.Note("Session " + ctx.Session.SessionNumber + " open"
                + (ctx.Session.Resumed ? " (resumed)" : "") + ". Dashboard refreshed.");
        }

        private static void SubmitHandoff(
            VaultUiContext ctx, VaultClientUI ui, Text status, Text problems, bool checkpoint,
            string last, string story, string threads, string npcs, string intent, string tone)
        {
            if (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug))
            {
                problems.text = "Pick a campaign first (Campaigns tab).";
                return;
            }
            var threadList = RosterParser.ParseLines(threads);
            var npcList = RosterParser.ParseStances(npcs);
            var issues = HandoffBuilder.Validate(story, last, threadList, npcList, intent, tone);
            if (issues.Count > 0)
            {
                problems.text = string.Join("\n", issues.ToArray());
                return;
            }
            problems.text = string.Empty;
            var args = HandoffBuilder.BuildArgs(ctx.Prompts.CampaignSlug, story, last, threadList, npcList, intent, tone, checkpoint);
            ui.StartCoroutine(SendEndSession(ctx, ui, status, problems, args, checkpoint));
        }

        private static IEnumerator SendEndSession(
            VaultUiContext ctx, VaultClientUI ui, Text status, Text problems, JsonValue args, bool checkpoint)
        {
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "play", "end_session", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                problems.text = "end_session failed: " + (result != null ? result.ErrorMessage : "no response");
                yield break;
            }
            if (checkpoint)
            {
                status.text = "Checkpoint stored — session still open.";
            }
            else
            {
                ctx.Session = null;
                status.text = "Session ended. The handoff carries the story forward.";
            }
            status.color = VaultTheme.Leaf;
            ui.Note(checkpoint ? "Checkpoint stored." : "Session ended with handoff.");
        }

        private static void PaintStatus(VaultUiContext ctx, Text status)
        {
            if (ctx.Session == null)
            {
                status.text = "No session started this run. Campaign: "
                    + (string.IsNullOrEmpty(ctx.Prompts.CampaignSlug) ? "(none)" : ctx.Prompts.CampaignSlug);
                status.color = VaultTheme.Muted;
                return;
            }
            status.text = "Session " + ctx.Session.SessionNumber
                + (ctx.Session.Resumed ? " (resumed)" : "")
                + " · " + ctx.Session.CampaignDisplay
                + " · " + ctx.Session.Time;
            status.color = VaultTheme.Leaf;
        }
    }
}
