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
    /// Guided onboarding wizard driving the server's onboarding state machine
    /// directly (deterministic — no LLM in the loop). Onboarding itself creates
    /// the campaign at finalize, so there is no separate create_campaign step:
    /// step one's name and system pre-answer the questionnaire's first two
    /// questions, the rest get a control matching their answer type, then
    /// finalize and hand creative seeding to the Chat driver. Existing progress
    /// resumes where it stopped.
    /// </summary>
    public static class OnboardWizard
    {
        private sealed class WizardState
        {
            public string Slug = string.Empty;
            public string System = SystemOptions[0];
            // Answers already given in step one, submitted when their question comes up.
            public Dictionary<string, string> Prefilled = new Dictionary<string, string>();
        }

        // Must match the server's onboarding "system" question options verbatim.
        private static readonly string[] SystemOptions = { "Dnd5e", "Pathfinder2e", "Narrative" };
        private static readonly string[] SystemLabels = { "D&D 5e", "Pathfinder 2e", "Narrative" };

        public static void BuildOnboardPanel(Transform parent, VaultUiContext ctx, VaultClientUI ui)
        {
            var scroll = VaultTheme.MakeScrollView(parent, "OnbScroll");
            var col = VaultTheme.Column(scroll.content, "OnbCol", 10);
            var body = VaultTheme.Column(col.transform, "OnbBody", 10);
            RenderStepOne(ctx, ui, body.transform);
        }

        // ---- Step 1: campaign identity ----

        private static void RenderStepOne(VaultUiContext ctx, VaultClientUI ui, Transform body)
        {
            VaultTheme.ClearChildren(body);
            Title(body, "NEW CAMPAIGN");
            var state = new WizardState();

            var nameInput = VaultTheme.MakeInput(body.transform, "Name", "campaign name, e.g. Dragon Heist", false);
            var displayInput = VaultTheme.MakeInput(body.transform, "Display", "display name (optional)", false);

            var sysRow = VaultTheme.Row(body.transform, "SysRow", 8);
            var sysButtons = new List<Button>();
            var sysLabels = new List<Text>();
            string[] systems = SystemOptions;
            for (int i = 0; i < systems.Length; i++)
            {
                string captured = systems[i];
                var button = VaultTheme.MakeButton(sysRow.transform, "Sys" + captured, SystemLabels[i], 14);
                button.GetComponent<LayoutElement>().flexibleWidth = 1;
                sysButtons.Add(button);
                sysLabels.Add(button.GetComponentInChildren<Text>());
                button.onClick.AddListener(delegate
                {
                    state.System = captured;
                    for (int j = 0; j < systems.Length; j++)
                    {
                        sysLabels[j].color = systems[j] == captured ? VaultTheme.Gold : VaultTheme.Parchment;
                    }
                });
            }
            sysLabels[0].color = VaultTheme.Gold;

            var createRow = VaultTheme.Row(body.transform, "CreateRow", 8);
            var create = VaultTheme.GoldButton(createRow.transform, "Create", "Start onboarding", 14);
            Tooltip.Attach(create.gameObject,
                "Opens the guided questionnaire. The campaign is created (and its ruleset locked) when you finalize; nothing is seeded until then.");
            var resumeInput = VaultTheme.MakeInput(createRow.transform, "Resume", "or existing slug to resume", false);
            var resume = VaultTheme.MakeButton(createRow.transform, "ResumeBtn", "Resume", 14);
            resume.GetComponent<LayoutElement>().minWidth = 100;

            create.onClick.AddListener(delegate
            {
                string name = nameInput.text.Trim();
                string slug = Slugify(name);
                if (string.IsNullOrEmpty(slug)) { return; }
                string display = displayInput.text.Trim();
                state.Slug = slug;
                state.Prefilled["campaign_name"] = string.IsNullOrEmpty(display) ? name : display;
                state.Prefilled["system"] = state.System;
                ui.StartCoroutine(StartOnboarding(ctx, ui, body, state));
            });
            resume.onClick.AddListener(delegate
            {
                string slug = resumeInput.text.Trim();
                if (string.IsNullOrEmpty(slug)) { return; }
                state.Slug = slug;
                FetchQuestion(ctx, ui, body, state);
            });
        }

        /// <summary>Client-side preview of the server's slug rule ("Dragon Heist" → dragon-heist).</summary>
        internal static string Slugify(string name)
        {
            var sb = new System.Text.StringBuilder();
            bool dash = false;
            foreach (char c in (name ?? string.Empty).Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) { sb.Append(c); dash = false; }
                else if (!dash && sb.Length > 0) { sb.Append('-'); dash = true; }
            }
            return sb.ToString().TrimEnd('-');
        }

        private static IEnumerator StartOnboarding(VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state)
        {
            Status(body, "Opening onboarding\u2026");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(state.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "build", "start_campaign_onboarding", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (!result.Ok)
            {
                Status(body, "Onboarding failed: " + result.ErrorMessage);
                yield break;
            }
            // The server's normalized slug is authoritative for every later call.
            string serverSlug = result.Data.Data.Get("state").GetString("campaignSlug", string.Empty);
            if (!string.IsNullOrEmpty(serverSlug)) { state.Slug = serverSlug; }
            RenderAnswerStep(ctx, ui, body, state, result.Data.Data);
        }

        // ---- Step 2: question loop ----

        private static void FetchQuestion(VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state)
        {
            ui.StartCoroutine(StartOnboarding(ctx, ui, body, state));
        }

        private static void RenderAnswerStep(VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state, JsonValue payload)
        {
            var question = Pick(payload, "currentQuestion", "CurrentQuestion");
            bool ready = payload.GetBool("isReadyToBuild", payload.GetBool("IsReadyToBuild", false))
                || payload.Get("state").GetBool("isComplete", false);
            if (ready || question.IsNull)
            {
                RenderFinalize(ctx, ui, body, state);
                return;
            }

            string key = question.GetString("key", string.Empty);
            string prefilled;
            if (state.Prefilled.TryGetValue(key, out prefilled))
            {
                state.Prefilled.Remove(key);
                ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, prefilled));
                return;
            }

            VaultTheme.ClearChildren(body);
            Title(body, "ONBOARDING  ·  " + state.Slug.ToUpperInvariant());
            var progress = VaultTheme.MakeText(body.transform, "Progress", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            int answered = (int)payload.Get("state").GetNumber("currentQuestionIndex", 0);
            string remaining = payload.GetString("summary", string.Empty);
            progress.text = answered + " answered" + (remaining.Length > 0 ? "  \u00b7  " + TextSanitizer.Clean(remaining, 120) : string.Empty);
            VaultTheme.FitVertical(progress);

            var text = VaultTheme.MakeText(body.transform, "Q", VaultTheme.SubHeaderSize - 1, VaultTheme.Parchment, FontStyle.Bold, VaultTheme.BodyFont);
            text.text = TextSanitizer.Clean(question.GetStringAny(new[] { "text", "Text" }, string.Empty), 800);
            VaultTheme.FitVertical(text);
            string help = question.GetStringAny(new[] { "helpText", "HelpText" }, string.Empty);
            if (!string.IsNullOrEmpty(help))
            {
                var helpText = VaultTheme.MakeText(body.transform, "Help", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
                helpText.text = TextSanitizer.Clean(help, 800);
                VaultTheme.FitVertical(helpText);
            }

            int answerType = ParseAnswerType(question);
            var options = new List<string>();
            foreach (var o in question.GetArray("enumOptions"))
            {
                if (o.Kind == JsonKind.String) { options.Add(o.StringValue); }
            }
            if (options.Count == 0)
            {
                foreach (var o in question.GetArray("EnumOptions"))
                {
                    if (o.Kind == JsonKind.String) { options.Add(o.StringValue); }
                }
            }

            if (answerType == 1 && options.Count > 0)
            {
                foreach (string option in options)
                {
                    string captured = option;
                    var button = VaultTheme.MakeButton(body.transform, "Opt", TextSanitizer.Clean(captured, 120), 15);
                    button.onClick.AddListener(delegate
                    {
                        ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, captured));
                    });
                }
            }
            else if (answerType == 2)
            {
                var row = VaultTheme.Row(body.transform, "BoolRow", 8);
                var yes = VaultTheme.GoldButton(row.transform, "Yes", "Yes", 15);
                var no = VaultTheme.MakeButton(row.transform, "No", "No", 15);
                yes.onClick.AddListener(delegate { ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, "yes")); });
                no.onClick.AddListener(delegate { ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, "no")); });
            }
            else
            {
                bool multiline = answerType == 3;
                InputField input = multiline
                    ? VaultTheme.MakeArea(body.transform, "Answer", "one entry per line", 90)
                    : VaultTheme.MakeInput(body.transform, "Answer", "your answer", false);
                var send = VaultTheme.GoldButton(body.transform, "Send", "Answer", 14);
                send.onClick.AddListener(delegate
                {
                    string answer = input.text.Trim();
                    if (multiline)
                    {
                        answer = string.Join("; ", RosterParser.ParseLines(answer).ToArray());
                    }
                    if (string.IsNullOrEmpty(answer)) { return; }
                    ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, answer));
                });
            }

            // Roster helper: paste a party list, parse it, use as the answer.
            AddSection(body, "ROSTER HELPER");
            var hint = VaultTheme.MakeText(body.transform, "RosterHint", VaultTheme.SmallSize, VaultTheme.Faint, FontStyle.Italic, VaultTheme.BodyFont);
            hint.text = "Paste the party (\"Name \u2014 detail\" per line) to answer a roster question in one tap.";
            VaultTheme.FitVertical(hint);
            var rosterInput = VaultTheme.MakeArea(body.transform, "Roster", "Lyra \u2014 elf ranger\nShade \u2014 familiar", 70);
            var parseRow = VaultTheme.Row(body.transform, "ParseRow", 8);
            var preview = VaultTheme.MakeText(body.transform, "Preview", VaultTheme.SmallSize + 1, VaultTheme.Muted, FontStyle.Normal, VaultTheme.BodyFont);
            VaultTheme.FitVertical(preview);
            var parse = VaultTheme.MakeButton(parseRow.transform, "Parse", "Parse roster", 14);
            parse.onClick.AddListener(delegate
            {
                var entries = RosterParser.Parse(rosterInput.text);
                preview.text = entries.Count == 0
                    ? "Nothing parsed."
                    : RosterParser.FormatPartyLine(entries);
            });
            var useRoster = VaultTheme.GoldButton(parseRow.transform, "UseRoster", "Use as answer", 14);
            useRoster.onClick.AddListener(delegate
            {
                var entries = RosterParser.Parse(rosterInput.text);
                if (entries.Count == 0) { return; }
                string answer = RosterParser.FormatPartyLine(entries);
                ctx.Prompts.PartyLine = answer;
                ui.StartCoroutine(SubmitAnswer(ctx, ui, body, state, answer));
            });
        }

        private static int ParseAnswerType(JsonValue question)
        {
            var raw = Pick(question, "answerType", "AnswerType");
            if (raw.Kind == JsonKind.Number)
            {
                int n = (int)raw.NumberValue;
                return n >= 0 && n <= 3 ? n : 0;
            }
            string text = raw.Kind == JsonKind.String ? raw.StringValue.ToLowerInvariant() : string.Empty;
            if (text.Contains("enum") || text.Contains("option") || text.Contains("choice")) { return 1; }
            if (text.Contains("bool")) { return 2; }
            if (text.Contains("list") || text.Contains("array")) { return 3; }
            return 0;
        }

        private static IEnumerator SubmitAnswer(
            VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state, string answer)
        {
            Status(body, "Answering\u2026");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(state.Slug);
            args.ObjectValue["answer"] = JsonValue.FromString(answer);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "build", "submit_onboarding_answer", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (!result.Ok)
            {
                // Re-fetch the current question so the player can correct the answer.
                ui.Note("Answer rejected: " + result.ErrorMessage);
                yield return StartOnboarding(ctx, ui, body, state);
                yield break;
            }
            RenderAnswerStep(ctx, ui, body, state, result.Data.Data);
        }

        // ---- Step 3: finalize ----

        private static void RenderFinalize(VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state)
        {
            VaultTheme.ClearChildren(body);
            Title(body, "READY TO BUILD");
            var info = VaultTheme.MakeText(body.transform, "Info", VaultTheme.BodySize - 1, VaultTheme.Parchment, FontStyle.Normal, VaultTheme.BodyFont);
            info.text = "All questions answered. Finalizing locks the campaign settings (it does not seed the world yet).";
            VaultTheme.FitVertical(info);
            var finalize = VaultTheme.GoldButton(body.transform, "Finalize", "Finalize campaign", 14);
            Tooltip.Attach(finalize.gameObject,
                "Locks system, tone, and setting from your answers. Next: seed starter locations, NPCs, and quests with world_build, then start_session.");
            finalize.onClick.AddListener(delegate
            {
                ui.StartCoroutine(Finalize(ctx, ui, body, state));
            });
        }

        private static IEnumerator Finalize(VaultUiContext ctx, VaultClientUI ui, Transform body, WizardState state)
        {
            Status(body, "Finalizing\u2026");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(state.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return ctx.Mcp.CallToolData(ctx.Config, "build", "finalize_campaign_onboarding", args,
                delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (!result.Ok)
            {
                Status(body, "Finalize failed: " + result.ErrorMessage);
                yield break;
            }
            var finalized = result.Data.Data;
            VaultTheme.ClearChildren(body);
            Title(body, "CAMPAIGN READY");
            var done = VaultTheme.MakeText(body.transform, "Done", VaultTheme.BodySize - 1, VaultTheme.Leaf, FontStyle.Bold, VaultTheme.BodyFont);
            var lines = new List<string> { finalized.GetString("summary", result.Data.Summary) };
            foreach (var step in finalized.GetArray("nextSteps"))
            {
                if (step.Kind == JsonKind.String) { lines.Add("\u2022 " + step.StringValue); }
            }
            done.text = TextSanitizer.Clean(string.Join("\n", lines.ToArray()), 2000);
            VaultTheme.FitVertical(done);

            ui.SelectCampaign(state.Slug, finalized.GetString("system", state.System));

            var seed = VaultTheme.GoldButton(body.transform, "Seed", "Seed world in Chat", 14);
            Tooltip.Attach(seed.gameObject,
                "Asks the DM driver to seed starter entities with world_build (using your onboarding answers and loaded skills), then open the first session.");
            seed.onClick.AddListener(delegate
            {
                ui.SendChatText("OOC: seed the starter world for \"" + state.Slug
                    + "\" with world_build from the onboarding answers, then start_session. Narrate the opening.");
            });
            var dash = VaultTheme.MakeButton(body.transform, "Dash", "Open dashboard", 14);
            dash.onClick.AddListener(delegate { ui.ShowTab("Dashboard"); });
        }

        // ---- Small builders ----

        private static void Title(Transform body, string text)
        {
            var title = VaultTheme.MakeText(body.transform, "Title", VaultTheme.SubHeaderSize, VaultTheme.Gold, FontStyle.Bold, VaultTheme.DisplayFont);
            title.text = text;
            VaultTheme.FitVertical(title);
        }

        private static void AddSection(Transform body, string text)
        {
            var section = VaultTheme.MakeText(body.transform, "Section", VaultTheme.SmallSize + 1, VaultTheme.Gold, FontStyle.Bold, VaultTheme.BodyFont);
            section.text = text;
            VaultTheme.FitVertical(section);
        }

        private static void Status(Transform body, string text)
        {
            VaultTheme.ClearChildren(body);
            var status = VaultTheme.MakeText(body.transform, "Status", VaultTheme.BodySize - 1, VaultTheme.Muted, FontStyle.Italic, VaultTheme.BodyFont);
            status.text = text;
            VaultTheme.FitVertical(status);
        }

        private static JsonValue Pick(JsonValue obj, string camel, string pascal)
        {
            var v = obj.Get(camel);
            if (!v.IsNull) { return v; }
            return obj.Get(pascal);
        }
    }
}
