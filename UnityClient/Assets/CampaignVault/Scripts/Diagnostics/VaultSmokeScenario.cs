using System;
using System.Collections;
using System.Collections.Generic;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.Diagnostics
{
    /// <summary>
    /// The end-to-end smoke flow, driven through VaultController with no UI:
    /// MCP handshake → onboarding → seed → campaigns → session → party, sheet,
    /// pack, search → prompt budget → optional chat turns → delete. Shared by
    /// the player's -vault-smoke runner and the edit-mode integration test.
    /// Point it only at a scratch server: it creates and deletes a campaign.
    /// </summary>
    public sealed class VaultSmokeScenario
    {
        public const string HeroId = "chars/smoke-hero";
        public const string HeroName = "Smoke Hero";
        public const string BladeName = "Smoke Blade";

        private readonly VaultAppState _s;
        private readonly VaultController _c;

        public int Passed { get; private set; }
        public int Failed { get; private set; }
        public readonly List<string> Lines = new List<string>();
        public Action<string> Log;

        public VaultSmokeScenario(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
        }

        public IEnumerator Run(string serverUrl, string llmUrl)
        {
            _s.Config.ServerUrl = serverUrl;
            _s.Mcp.ResetSessions();
            _s.Driver.InvalidateTools();

            // 1. MCP handshake against a stateful server.
            McpOutcome<List<McpToolInfo>> play = null;
            yield return _s.Mcp.ListTools(_s.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { play = o; });
            Check("mcp-handshake-play", play.Ok && play.Data.Count > 0, play.Ok ? play.Data.Count + " tools" : play.ErrorMessage);
            McpOutcome<List<McpToolInfo>> build = null;
            yield return _s.Mcp.ListTools(_s.Config, "build", delegate (McpOutcome<List<McpToolInfo>> o) { build = o; });
            Check("mcp-handshake-build", build.Ok && build.Data.Count > 0, build.Ok ? build.Data.Count + " tools" : build.ErrorMessage);
            if (!play.Ok || !build.Ok) { yield break; }

            // 2. Onboarding, answering whatever the server asks.
            string name = "Smoke Test " + DateTime.UtcNow.ToString("HHmmssfff");
            string expectedSlug = VaultController.Slugify(name);
            yield return Onboard(name);
            Check("onboard-selects-campaign", _s.CampaignSlug == expectedSlug, "active=" + _s.CampaignSlug + " expected=" + expectedSlug);
            string slug = _s.CampaignSlug;
            if (slug != expectedSlug) { yield break; }

            // 3. Seed a PC with gear (the DM's job in real play).
            yield return Seed(slug);

            // 4. The campaign list shows it.
            yield return _c.ListCampaigns();
            Check("campaigns-lists-slug", _s.Campaigns.Exists(delegate (CampaignRow r) { return r.Slug == slug; }), _s.CampaignsError.Length > 0 ? _s.CampaignsError : slug);

            // 5. Session start fills ruleset, roster, fingerprint, PC id.
            yield return _c.StartSession("Smoke session");
            Check("session-start", _s.Session != null, _s.Session != null ? "session " + _s.Session.SessionNumber : _s.SessionStatus);
            Check("session-fills-pc", _s.PcId == HeroId, "pcId=" + _s.PcId);
            Check("session-fills-prompt", _s.Prompts.PartyLine.Contains(HeroName) && _s.Prompts.PartyFingerprint.Length > 0 && _s.Prompts.Ruleset.Length > 0,
                "party=" + _s.Prompts.PartyLine + " ruleset=" + _s.Prompts.Ruleset + " fp=" + _s.Prompts.PartyFingerprint);

            // 6. Table state, sheet, pack and search all read real data.
            yield return _c.RefreshTable();
            Check("table-party", _s.Session != null && _s.Session.Party.Exists(delegate (Flows.DashboardMember m) { return m.Name == HeroName; }), _s.SessionStatus);
            yield return _c.LoadPc();
            Check("character-sheet", _s.Pc != null && _s.Pc.Name == HeroName, _s.Pc != null ? _s.Pc.Name : _s.PcError);
            Check("inventory-gear", _s.Inventory().Exists(delegate (InventoryItem i) { return i.Name == BladeName; }), _s.Inventory().Count + " items");
            yield return _c.SearchWorld("Smoke");
            Check("search-world", _s.SearchError.Length == 0, _s.SearchError.Length > 0 ? _s.SearchError : _s.SearchResults.Count + " hits");

            // 7. Token-efficient prompt: skills are indexed, not inlined.
            string prompt = _s.Prompts.BuildSystemPrompt();
            int skills;
            string skillNames;
            _s.Prompts.TryGetSkillStatus(out skills, out skillNames);
            string narration;
            bool loaded = _s.Prompts.TryLoadSkill("dnd-narration", out narration);
            Check("prompt-size", prompt.Length < 20000 && skills > 0 && loaded,
                "system prompt " + prompt.Length + " chars, " + skills + " skills indexed, narration body " + (narration ?? string.Empty).Length + " chars");
            // N1: installer notes and the template CAMPAIGN line never reach the model.
            int campaignCount = 0;
            foreach (string line in prompt.Split('\n')) { if (line.StartsWith("CAMPAIGN:", StringComparison.Ordinal)) { campaignCount++; } }
            Check("prompt-clean", campaignCount == 1 && !prompt.Contains("<slug>") && !prompt.Contains("```"),
                campaignCount + " CAMPAIGN line(s), template slug " + (prompt.Contains("<slug>") ? "present" : "absent"));

            // 8. Chat loop against a mock LLM (load_skill, a real MCP tool, prose; then compaction).
            if (!string.IsNullOrEmpty(llmUrl)) { yield return Chat(llmUrl); }

            // 9. Clean up.
            yield return _c.DeleteCampaign(slug);
            bool goneLocally = !_s.Campaigns.Exists(delegate (CampaignRow r) { return r.Slug == slug; });
            Check("delete-campaign", goneLocally && _s.CampaignSlug.Length == 0, "listed=" + !goneLocally + " active=" + _s.CampaignSlug);
        }

        private IEnumerator Onboard(string name)
        {
            yield return _c.BeginOnboarding(name, string.Empty, "Dnd5e");
            var ob = _s.Onboarding;
            for (int step = 0; step < 30; step++)
            {
                switch (ob.Phase)
                {
                    case OnboardingPhase.Done:
                        Check("onboard-finalize", true, "answered " + step + " questions");
                        yield break;
                    case OnboardingPhase.ReadyToFinalize:
                        if (ob.Error.Length > 0) { Check("onboard-finalize", false, ob.Error); yield break; }
                        yield return _c.FinalizeOnboarding();
                        break;
                    case OnboardingPhase.Question:
                        yield return _c.SubmitOnboardingAnswer(AnswerFor(ob.Question));
                        break;
                    default:
                        Check("onboard-finalize", false, "stuck in " + ob.Phase + ": " + ob.Error);
                        yield break;
                }
            }
            Check("onboard-finalize", false, "more than 30 steps");
        }

        private static string AnswerFor(OnboardingQuestion q)
        {
            switch (q.Type)
            {
                case AnswerType.Choice: return q.Options[0];
                case AnswerType.YesNo: return "yes";
                case AnswerType.List: return VaultController.FormatListAnswer("Smoke Hero — human fighter");
                default: return "A windswept frontier town on the edge of old ruins";
            }
        }

        private IEnumerator Seed(string slug)
        {
            string batch = "{\"locations\":[{\"id\":\"locations/smoke-inn\",\"name\":\"Smoke Inn\"}],"
                + "\"characters\":[{\"id\":\"" + HeroId + "\",\"name\":\"" + HeroName + "\",\"isPc\":true,\"currentLocationId\":\"locations/smoke-inn\"}],"
                + "\"items\":[{\"id\":\"items/smoke-blade\",\"name\":\"" + BladeName + "\",\"holderId\":\"" + HeroId + "\"}]}";
            JsonValue batchValue;
            JsonValue.TryParse(batch, out batchValue);
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
            args.ObjectValue["batch"] = batchValue;
            McpOutcome<ToolPayload> result = null;
            yield return _s.Mcp.CallToolData(_s.Config, "build", "world_build", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            Check("seed-world", result.Ok, result.Ok ? result.Data.Summary : result.ErrorMessage);
        }

        private IEnumerator Chat(string llmUrl)
        {
            _s.Byok.BaseUrl = llmUrl;
            _s.Byok.Model = "smoke-mock";
            _s.Byok.SetApiKey("smoke-key");
            yield return _c.SendPlayerTextRoutine("I look around the inn.");
            Check("chat-turn-1", HasNarration("smoke-mock"), "mock narration " + (HasNarration("smoke-mock") ? "rendered" : "missing"));
            // The mock asserts compaction server-side and says so in its narration.
            yield return _c.SendPlayerTextRoutine("I order a drink.");
            Check("chat-turn-2-compaction", HasNarration("history compacted"), "earlier tool results compacted");
            Check("chat-storyteller-clean", _s.Byok.Active.SinglePass || !HasNarration("storyteller saw JSON"), "storyteller context has no tool JSON");
        }

        private bool HasNarration(string needle)
        {
            foreach (var seg in _s.Transcript.Segments)
            {
                if (seg.Kind == SegmentKind.Narration && seg.Text.Contains(needle)) { return true; }
            }
            return false;
        }

        public void Check(string step, bool ok, string detail)
        {
            if (ok) { Passed++; } else { Failed++; }
            string line = (ok ? "PASS " : "FAIL ") + step + ": " + detail;
            Lines.Add(line);
            if (Log != null) { Log(line); }
        }
    }
}
