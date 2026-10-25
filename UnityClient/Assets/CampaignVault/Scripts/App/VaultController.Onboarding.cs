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
    /// <summary>Session 0: the guided questions, brainstorming with the DM, finalizing and seeding.</summary>
    public sealed partial class VaultController
    {
        // =====================================================================
        // Onboarding
        // =====================================================================

        /// <summary>Client-side preview of the server's slug rule ("Dragon Heist" → dragon-heist).</summary>
        public static string Slugify(string name)
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

        /// <summary>
        /// Opens the questionnaire. Name, display name and system are answered
        /// up front and submitted automatically when their questions come up.
        /// </summary>
        public IEnumerator BeginOnboarding(string name, string displayName, string system)
        {
            string slug = Slugify(name);
            var ob = _s.Onboarding;
            if (slug.Length == 0)
            {
                ob.Error = "Give the campaign a name.";
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.Prefilled.Clear();
            ob.Slug = slug;
            ob.System = string.IsNullOrEmpty(system) ? OnboardingState.SystemOptions[0] : system;
            ob.Prefilled["campaign_name"] = string.IsNullOrEmpty((displayName ?? string.Empty).Trim()) ? name.Trim() : displayName.Trim();
            ob.Prefilled["system"] = ob.System;
            yield return FetchOnboarding();
        }

        public IEnumerator ResumeOnboarding(string slug)
        {
            slug = (slug ?? string.Empty).Trim();
            if (slug.Length == 0) { yield break; }
            _s.Onboarding.Prefilled.Clear();
            _s.Onboarding.Slug = slug;
            yield return FetchOnboarding();
        }

        private IEnumerator FetchOnboarding()
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Opening onboarding…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "start_campaign_onboarding", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                ob.Error = "Onboarding failed: " + (result != null ? result.ErrorMessage : "no response");
                SetOnboarding(OnboardingPhase.Failed, string.Empty);
                yield break;
            }
            // The server's normalized slug is authoritative for every later call.
            string serverSlug = result.Data.Data.Get("state").GetString("campaignSlug", string.Empty);
            if (!string.IsNullOrEmpty(serverSlug)) { ob.Slug = serverSlug; }
            yield return AdvanceOnboarding(result.Data.Data);
        }

        /// <summary>Shows the next question, auto-answering prefilled ones.</summary>
        private IEnumerator AdvanceOnboarding(JsonValue payload)
        {
            var ob = _s.Onboarding;
            while (true)
            {
                var question = Pick(payload, "currentQuestion", "CurrentQuestion");
                bool ready = payload.GetBool("isReadyToBuild", payload.GetBool("IsReadyToBuild", false))
                    || payload.Get("state").GetBool("isComplete", false);
                ob.Answered = (int)payload.Get("state").GetNumber("currentQuestionIndex", ob.Answered);
                ob.Progress = TextSanitizer.Clean(payload.GetString("summary", string.Empty), 160);
                ReadAnswers(payload.Get("state"), ob.Answers);
                if (ready || question.IsNull)
                {
                    ob.Question = null;
                    SetOnboarding(OnboardingPhase.ReadyToFinalize, string.Empty);
                    yield break;
                }
                var parsed = ParseQuestion(question);
                string prefilled;
                if (!ob.Prefilled.TryGetValue(parsed.Key, out prefilled))
                {
                    MoveToQuestion(ob, parsed);
                    ob.Error = string.Empty;
                    SetOnboarding(OnboardingPhase.Question, string.Empty);
                    // The party step opens with the DM's read on the starting level, asked once; the player still chooses.
                    if (parsed.Key == "party" && !ob.LevelSuggestionAsked) { SuggestPartyLevel(); }
                    yield break;
                }
                ob.Prefilled.Remove(parsed.Key);
                JsonValue next = null;
                yield return PostAnswer(prefilled, delegate (JsonValue p) { next = p; });
                if (next == null) { yield break; }
                payload = next;
            }
        }

        /// <summary>state.collectedAnswers → key/answer strings, for the brainstorm prompt and the party line.</summary>
        /// <summary>
        /// A question arrives. On a new one the draft resets, but the conversation with the DM carries on
        /// (a divider marks the move), so a plot talked through on the world question is still there when
        /// the plot question comes. The same question again (a rejected answer) changes nothing.
        /// </summary>
        internal static void MoveToQuestion(OnboardingState ob, OnboardingQuestion parsed)
        {
            if (ob.Question == null || ob.Question.Key != parsed.Key)
            {
                ob.Draft = string.Empty;
                ob.Brainstorming = false;
                ob.BrainstormError = string.Empty;
                if (OnboardingBrainstorm.HasTalk(ob.BrainstormChat))
                {
                    ob.BrainstormChat.Add(new KeyValuePair<string, string>(OnboardingBrainstorm.MarkerRole, parsed.Text));
                }
            }
            ob.Question = parsed;
        }

        internal static void ReadAnswers(JsonValue state, Dictionary<string, string> into)
        {
            var answers = Pick(state, "collectedAnswers", "CollectedAnswers");
            if (answers.Kind != JsonKind.Object || answers.ObjectValue == null) { return; }
            into.Clear();
            foreach (var kv in answers.ObjectValue)
            {
                if (kv.Value.Kind == JsonKind.String) { into[kv.Key] = TextSanitizer.Clean(kv.Value.StringValue, 3000); }
                else if (kv.Value.Kind == JsonKind.Number) { into[kv.Key] = kv.Value.NumberValue.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            }
        }

        internal static OnboardingQuestion ParseQuestion(JsonValue question)
        {
            var q = new OnboardingQuestion
            {
                Key = question.GetString("key", string.Empty),
                Text = TextSanitizer.Clean(question.GetStringAny(new[] { "text", "Text" }, string.Empty), 800),
                Help = TextSanitizer.Clean(question.GetStringAny(new[] { "helpText", "HelpText" }, string.Empty), 800),
            };
            var options = question.GetArray("enumOptions");
            if (options.Count == 0) { options = question.GetArray("EnumOptions"); }
            foreach (var o in options) { if (o.Kind == JsonKind.String) { q.Options.Add(o.StringValue); } }
            var raw = Pick(question, "answerType", "AnswerType");
            int n = -1;
            if (raw.Kind == JsonKind.Number) { n = (int)raw.NumberValue; }
            else if (raw.Kind == JsonKind.String)
            {
                string text = raw.StringValue.ToLowerInvariant();
                if (text.Contains("enum") || text.Contains("option") || text.Contains("choice")) { n = 1; }
                else if (text.Contains("bool")) { n = 2; }
                else if (text.Contains("list") || text.Contains("array")) { n = 3; }
                else if (text.Contains("number") || text.Contains("int")) { n = 4; }
                else if (text.Contains("party")) { n = 5; }
            }
            switch (n)
            {
                case 1: q.Type = q.Options.Count > 0 ? AnswerType.Choice : AnswerType.Text; break;
                case 2: q.Type = AnswerType.YesNo; break;
                case 3: q.Type = AnswerType.List; break;
                case 4: q.Type = AnswerType.Number; break;
                case 5: q.Type = AnswerType.Party; break;
                default: q.Type = AnswerType.Text; break;
            }
            return q;
        }

        /// <summary>A list answer typed one entry per line, in the server's "a; b; c" form.</summary>
        public static string FormatListAnswer(string lines)
        {
            return string.Join("; ", RosterParser.ParseLines(lines).ToArray());
        }

        public IEnumerator SubmitOnboardingAnswer(string answer)
        {
            answer = (answer ?? string.Empty).Trim();
            if (answer.Length == 0 || _s.Onboarding.Phase == OnboardingPhase.Working) { yield break; }
            JsonValue next = null;
            yield return PostAnswer(answer, delegate (JsonValue p) { next = p; });
            if (next != null) { yield return AdvanceOnboarding(next); }
        }

        private IEnumerator PostAnswer(string answer, Action<JsonValue> done)
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Answering…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            args.ObjectValue["answer"] = JsonValue.FromString(answer);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "submit_onboarding_answer", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                // The same question stays up with the reason under it; what was typed is still in the field to correct.
                done(null);
                if (ob.Question != null)
                {
                    SetOnboarding(OnboardingPhase.Question, string.Empty);
                    ob.Error = TextSanitizer.Clean(result != null ? result.ErrorMessage : "No response from the server.", 400);
                    _s.Notify(StateArea.Onboarding);
                }
                else { yield return FetchOnboarding(); }
                yield break;
            }
            // The player characters as the DM prompt's party line, until the party exists on the server.
            if (ob.Question != null && ob.Question.Key == "pc_roster" && _s.Prompts != null && _s.Prompts.PartyLine.Length == 0)
            {
                _s.Prompts.PartyLine = TextSanitizer.Clean(answer.Replace("\n", "; "), 600);
            }
            if (ob.Question != null && ob.Question.Key == "party" && _s.Prompts != null)
            {
                var names = new List<string>();
                foreach (var m in ob.Party) { names.Add(m.ClassLine.Length > 0 ? m.Name + " — " + m.ClassLine : m.Name); }
                if (names.Count > 0 && answer.Contains("\"" + OnboardingState.PartyBuildAtTable + "\"") == false) { _s.Prompts.PartyLine = TextSanitizer.Clean(string.Join("; ", names.ToArray()), 600); }
            }
            done(result.Data.Data);
        }

        /// <summary>The previous question comes back with its answer in the field; the server drops the answer and replays branching.</summary>
        public IEnumerator GoBackOnboarding()
        {
            var ob = _s.Onboarding;
            if (ob.Phase != OnboardingPhase.Question || ob.Slug.Length == 0) { yield break; }
            var current = ob.Question;
            SetOnboarding(OnboardingPhase.Working, "Going back…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            args.ObjectValue["back"] = JsonValue.FromBool(true);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "start_campaign_onboarding", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                SetOnboarding(OnboardingPhase.Question, string.Empty);
                ob.Error = "Couldn't go back: " + TextSanitizer.Clean(result != null ? result.ErrorMessage : "no response", 300);
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            var payload = result.Data.Data;
            string prior = payload.GetStringAny(new[] { "priorAnswer", "PriorAnswer" }, string.Empty);
            yield return AdvanceOnboarding(payload);
            if (ob.Phase == OnboardingPhase.Question && ob.Question != null && (current == null || ob.Question.Key != current.Key) && prior.Length > 0)
            {
                ob.Draft = ob.Question.Type == AnswerType.List ? prior.Replace("; ", "\n") : prior;
                _s.Notify(StateArea.Onboarding);
            }
        }

        public IEnumerator FinalizeOnboarding()
        {
            var ob = _s.Onboarding;
            SetOnboarding(OnboardingPhase.Working, "Finalizing…");
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(ob.Slug);
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "finalize_campaign_onboarding", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            if (result == null || !result.Ok)
            {
                ob.Error = "Finalize failed: " + (result != null ? result.ErrorMessage : "no response");
                SetOnboarding(OnboardingPhase.ReadyToFinalize, string.Empty);
                yield break;
            }
            var finalized = result.Data.Data;
            ob.DoneSummary = TextSanitizer.Clean(finalized.GetString("summary", result.Data.Summary), 1200);
            ob.SeedBrief = finalized.GetString("seedBrief", string.Empty).Trim();
            ob.NextSteps.Clear();
            foreach (var step in finalized.GetArray("nextSteps"))
            {
                if (step.Kind == JsonKind.String) { ob.NextSteps.Add(TextSanitizer.Clean(step.StringValue, 300)); }
            }
            // SelectCampaign resets the party line for a new table; this one comes from the answers just given.
            string partyLine = _s.Prompts.PartyLine;
            SelectCampaign(ob.Slug, finalized.GetString("system", ob.System));
            _s.Prompts.PartyLine = partyLine;
            _s.SetupPending = true;
            _s.Notify(StateArea.Session | StateArea.Campaign);
            SetOnboarding(OnboardingPhase.Done, string.Empty);
        }

        /// <summary>After finalize: the DM seeds the world with world_build and opens the first session.</summary>
        public bool SeedWorldThroughDm()
        {
            return SendPlayerText(SeedMessage(_s.Onboarding.Slug, _s.Onboarding.SeedBrief));
        }

        /// <summary>
        /// The OOC line that seeds a fresh campaign. It carries the onboarding brief
        /// itself: the DM has no tool that reads the answers back, so a line that only
        /// mentioned them left it asking the player to repeat everything.
        /// </summary>
        internal static string SeedMessage(string slug, string brief)
        {
            if (string.IsNullOrEmpty(brief))
            {
                // An older server without a brief: start_session repeats it, or the DM asks.
                return "OOC: seed the starter world for \"" + slug + "\" from its onboarding answers: create the player characters "
                    + "(world_build, isPc=true) and the opening location. Skip start_session; this client opens the session once they exist.";
            }
            return "OOC: set up the campaign \"" + slug + "\" from its onboarding brief below. Do its character and world steps with world_build. "
                + "Where it says to show me options or walk me through making characters, ask me here and wait for my answers. "
                + "Skip start_session and the opening scene: this client opens the session and asks for the first scene once the player characters exist.\n\n"
                + brief;
        }

        // ---- brainstorming the current question with the model ----

        public void OpenBrainstorm()
        {
            var ob = _s.Onboarding;
            if (!OnboardingBrainstorm.Supports(ob.Question)) { return; }
            string notReady;
            if (!_s.ProviderReady(out notReady))
            {
                _s.RaiseToast("Brainstorming needs a working AI provider: " + notReady, ToastKind.Warning);
                return;
            }
            ob.Brainstorming = true;
            ob.BrainstormError = string.Empty;
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>Back to the question; the conversation is kept for the whole setup.</summary>
        public void CloseBrainstorm()
        {
            _s.Onboarding.Brainstorming = false;
            _s.Notify(StateArea.Onboarding);
        }

        public IEnumerator SendBrainstorm(string text)
        {
            var ob = _s.Onboarding;
            text = TextSanitizer.Clean(text, 0).Trim();
            if (text.Length == 0 || ob.BrainstormBusy || ob.Question == null) { yield break; }
            if (text.Length > OnboardingBrainstorm.MaxMessageChars)
            {
                // Never cut what the player wrote: the composer already shows the limit; say it again and keep the text.
                ob.BrainstormError = "That message is " + text.Length.ToString("N0", CultureInfo.InvariantCulture) + " characters; the limit is "
                    + OnboardingBrainstorm.MaxMessageChars.ToString("N0", CultureInfo.InvariantCulture) + ". Trim it or send it in parts.";
                _s.Notify(StateArea.Onboarding);
                yield break;
            }
            ob.BrainstormChat.Add(new KeyValuePair<string, string>("user", text));
            ob.BrainstormDraft = string.Empty;
            yield return BrainstormTurn(null);
        }

        /// <summary>The model writes the settled answer up; it lands in the question's field to edit and submit.</summary>
        public IEnumerator WriteUpBrainstorm()
        {
            var ob = _s.Onboarding;
            if (ob.BrainstormBusy || ob.Question == null || !OnboardingBrainstorm.HasTalk(ob.BrainstormChat)) { yield break; }
            yield return BrainstormTurn(OnboardingBrainstorm.FinalizeInstruction(ob.Question));
        }

        private IEnumerator BrainstormTurn(string finalizeInstruction)
        {
            var ob = _s.Onboarding;
            var question = ob.Question;
            ob.BrainstormBusy = true;
            ob.BrainstormError = string.Empty;
            _s.Notify(StateArea.Onboarding);
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", OnboardingBrainstorm.SystemPrompt(question, ob.Answers)),
            };
            // Over the budget the middle of the chat drops out; the overlay marks those messages as no longer sent.
            var dropped = OnboardingBrainstorm.Dropped(ob.BrainstormChat, OnboardingBrainstorm.MaxConversationChars);
            messages.AddRange(OnboardingBrainstorm.ModelMessages(ob.BrainstormChat, dropped));
            if (finalizeInstruction != null) { messages.Add(new KeyValuePair<string, string>("user", finalizeInstruction)); }
            string reply = null;
            string error = null;
            // Not streamed: the overlay re-renders on every change, which would drop what the player is typing.
            yield return _s.Driver.Brainstorm(messages, null, delegate (string r, string e) { reply = r; error = e; });
            ob.BrainstormBusy = false;
            if (ob.Question != question) { yield break; }
            if (error != null)
            {
                ob.BrainstormError = TextSanitizer.Clean(error, 400);
                ob.BrainstormFailure = RecordFailure(ob.BrainstormError, delegate
                {
                    if (ob.BrainstormBusy || ob.Question != question) { return; }
                    Run(BrainstormTurn(finalizeInstruction));
                });
            }
            else if (finalizeInstruction == null)
            {
                ob.BrainstormChat.Add(new KeyValuePair<string, string>("assistant", TextSanitizer.Clean(reply, OnboardingBrainstorm.MaxReplyChars)));
            }
            else
            {
                ob.Draft = OnboardingBrainstorm.CleanAnswer(TextSanitizer.Clean(reply, 0));
                ob.Brainstorming = false;
            }
            _s.Notify(StateArea.Onboarding);
        }

        /// <summary>
        /// The driver's record of the call that just failed, with how to send it again. Read it right after the call:
        /// the next call overwrites it. Null when the driver left none (the caller then shows its plain error text).
        /// </summary>
        private FailedCall RecordFailure(string message, Action retry)
        {
            var failure = _s.Driver != null ? _s.Driver.LastFailure : null;
            if (failure == null) { return null; }
            return new FailedCall
            {
                Failure = failure,
                Message = message,
                Retry = retry,
                OpenSettings = delegate { _s.RequestSettings(); },
                Copied = delegate (string note) { _s.RaiseToast(note, ToastKind.Success); },
            };
        }

        public void ResetOnboarding()
        {
            var ob = _s.Onboarding;
            ob.Prefilled.Clear();
            ob.Slug = string.Empty;
            ob.Question = null;
            ob.Error = string.Empty;
            ob.DoneSummary = string.Empty;
            ob.NextSteps.Clear();
            ob.SeedBrief = string.Empty;
            ob.Answers.Clear();
            ob.Draft = string.Empty;
            ob.Party.Clear();
            ob.PartyLevel = 1;
            ob.LevelSuggestion = 0;
            ob.LevelReason = string.Empty;
            ob.SuggestingLevel = false;
            ob.LevelSuggestionAsked = false;
            ob.Drafting = false;
            ob.DraftError = string.Empty;
            ob.ClearBrainstorm();
            SetOnboarding(OnboardingPhase.Idle, string.Empty);
        }

        private void SetOnboarding(OnboardingPhase phase, string status)
        {
            _s.Onboarding.Phase = phase;
            _s.Onboarding.Status = status;
            if (phase != OnboardingPhase.Failed && phase != OnboardingPhase.ReadyToFinalize) { _s.Onboarding.Error = string.Empty; }
            _s.Notify(StateArea.Onboarding);
        }

        private static JsonValue Pick(JsonValue obj, string camel, string pascal)
        {
            var v = obj.Get(camel);
            return v.IsNull ? obj.Get(pascal) : v;
        }
    }
}
