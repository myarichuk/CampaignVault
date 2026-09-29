using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.Diagnostics
{
    /// <summary>
    /// End-to-end smoke test of a player build, driven through the real UI:
    ///   CampaignVaultClient -vault-smoke http://127.0.0.1:PORT [-vault-smoke-llm http://127.0.0.1:PORT/v1]
    /// Clicks the actual buttons (Onboard, Campaigns, Session, Dashboard,
    /// Character, Inventory, Events), optionally runs two chat turns against
    /// an OpenAI-compatible mock, deletes its scratch campaign, logs one
    /// "[VaultSmoke] PASS|FAIL step: detail" line per check and quits with
    /// exit code 0 (all pass) or 1. Any logged exception fails the run.
    /// Point it only at a scratch server: it creates and deletes a campaign.
    /// </summary>
    public class VaultSmokeRunner : MonoBehaviour
    {
        private const string Tag = "[VaultSmoke] ";
        private static readonly string[] RememberedPrefs =
        {
            "vault.campaign", "vault.pcid", "vault.companions", "vault.server", "vault.byok.baseurl", "vault.byok.model",
        };

        private VaultClientUI _ui;
        private VaultUiContext _ctx;
        private int _pass;
        private int _fail;
        private int _exceptions;
        private readonly Dictionary<string, string> _savedPrefs = new Dictionary<string, string>();

        public static string RequestedServerUrl() { return ArgAfter("-vault-smoke"); }

        private static string ArgAfter(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == flag) { return args[i + 1]; }
            }
            return null;
        }

        public void Run(VaultClientUI ui, string serverUrl)
        {
            _ui = ui;
            _ctx = ui.Context;
            foreach (string key in RememberedPrefs)
            {
                if (PlayerPrefs.HasKey(key)) { _savedPrefs[key] = PlayerPrefs.GetString(key); }
            }
            Application.logMessageReceived += OnLog;
            StartCoroutine(Main(serverUrl, ArgAfter("-vault-smoke-llm")));
        }

        private void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Exception) { _exceptions++; }
        }

        private IEnumerator Main(string serverUrl, string llmUrl)
        {
            Debug.Log(Tag + "start server=" + serverUrl + " llm=" + (llmUrl ?? "(none)"));
            _ctx.Config.ServerUrl = serverUrl;
            _ctx.Mcp.ResetSessions();
            _ctx.Driver.InvalidateTools();
            yield return null;

            // 1. MCP handshake against a stateful server.
            McpOutcome<List<McpToolInfo>> play = null;
            yield return _ctx.Mcp.ListTools(_ctx.Config, "play", delegate (McpOutcome<List<McpToolInfo>> o) { play = o; });
            Check("mcp-handshake-play", play.Ok && play.Data.Count > 0, play.Ok ? play.Data.Count + " tools" : play.ErrorMessage);
            McpOutcome<List<McpToolInfo>> build = null;
            yield return _ctx.Mcp.ListTools(_ctx.Config, "build", delegate (McpOutcome<List<McpToolInfo>> o) { build = o; });
            Check("mcp-handshake-build", build.Ok && build.Data.Count > 0, build.Ok ? build.Data.Count + " tools" : build.ErrorMessage);
            if (!play.Ok) { Finish(); yield break; }

            // 2. Onboarding through the wizard's own buttons.
            string name = "Smoke Test " + DateTime.UtcNow.ToString("HHmmss");
            string expectedSlug = OnboardWizard.Slugify(name);
            yield return Onboard(name);
            Check("onboard-selects-campaign", _ctx.Prompts.CampaignSlug == expectedSlug,
                "active=" + _ctx.Prompts.CampaignSlug + " expected=" + expectedSlug);
            string slug = _ctx.Prompts.CampaignSlug;
            if (slug != expectedSlug) { Finish(); yield break; }

            // 3. Seed a PC with gear (the Chat driver's job in real play).
            yield return Seed(slug);

            // 4. Campaigns panel lists it.
            _ui.ShowTab("Campaigns");
            yield return ClickAndWait("Campaigns", "Refresh", delegate { return TextContains("Campaigns", slug); }, 20f);
            Check("campaigns-lists-slug", TextContains("Campaigns", slug), slug);

            // 5. Session start fills ruleset, roster, fingerprint, PC id.
            _ui.ShowTab("Session");
            yield return ClickAndWait("Session", "Start", delegate { return _ctx.Session != null; }, 30f);
            Check("session-start", _ctx.Session != null, _ctx.Session != null ? "session " + _ctx.Session.SessionNumber : "no digest");
            Check("session-fills-pc", _ctx.PcId == "chars/smoke-hero", "pcId=" + _ctx.PcId);
            Check("session-fills-prompt", _ctx.Prompts.PartyLine.Contains("Smoke Hero") && _ctx.Prompts.PartyFingerprint.Length > 0
                && _ctx.Prompts.Ruleset.Length > 0,
                "party=" + _ctx.Prompts.PartyLine + " ruleset=" + _ctx.Prompts.Ruleset + " fp=" + _ctx.Prompts.PartyFingerprint);

            // 6. Dashboard, Character, Inventory, Events render real data.
            _ui.ShowTab("Dashboard");
            yield return ClickAndWait("Dashboard", "Refresh", delegate { return TextContains("Dashboard", "Smoke Hero"); }, 30f);
            Check("dashboard-party", TextContains("Dashboard", "Smoke Hero"), FailText("Dashboard"));

            _ui.ShowTab("Character");
            yield return ClickAndWait("Character", "Refresh", delegate { return TextContains("Character", "SMOKE HERO"); }, 30f);
            Check("character-sheet", TextContains("Character", "SMOKE HERO"), FailText("Character"));

            _ui.ShowTab("Inventory");
            yield return ClickAndWait("Inventory", "Refresh", delegate { return TextContains("Inventory", "Smoke Blade"); }, 30f);
            Check("inventory-gear", TextContains("Inventory", "Smoke Blade"), FailText("Inventory"));

            _ui.ShowTab("Events");
            var query = Find<InputField>("Events", "Query");
            if (query != null) { query.text = "Smoke"; }
            yield return ClickAndWait("Events", "Search", delegate { return !TextContains("Events", "Searching"); }, 30f);
            Check("events-search", !TextContains("Events", "Could not load"), FailText("Events"));

            // 7. Token-efficient prompt: skills are indexed, not inlined.
            string prompt = _ctx.Prompts.BuildSystemPrompt();
            int skills;
            string skillNames;
            _ctx.Prompts.TryGetSkillStatus(out skills, out skillNames);
            string narration;
            bool loaded = _ctx.Prompts.TryLoadSkill("dnd-narration", out narration);
            Check("prompt-size", prompt.Length < 20000 && skills > 0 && loaded,
                "system prompt " + prompt.Length + " chars, " + skills + " skills indexed, narration body " + narration.Length + " chars");

            // 8. Chat loop against the mock LLM (load_skill, a real MCP tool, prose; then compaction).
            if (!string.IsNullOrEmpty(llmUrl)) { yield return Chat(llmUrl); }

            // 9. Effects settle to their exact end state (animations never leave half-states behind).
            yield return Effects();

            // 10. Clean up through the Campaigns panel's two-tap delete.
            _ui.ShowTab("Campaigns");
            yield return ClickAndWait("Campaigns", "Refresh", delegate { return TextContains("Campaigns", slug); }, 20f);
            var delete = FindDeleteFor(slug);
            // Record before clicking: the list re-renders on delete, and a destroyed button compares equal to null.
            bool foundDelete = delete != null;
            if (foundDelete)
            {
                delete.onClick.Invoke();
                delete.onClick.Invoke();
                yield return WaitFor(delegate { return _ctx.Prompts.CampaignSlug.Length == 0; }, 20f);
            }
            McpOutcome<ToolPayload> listed = null;
            yield return _ctx.Mcp.CallToolData(_ctx.Config, "build", "list_campaigns", JsonValue.NewObject(), delegate (McpOutcome<ToolPayload> o) { listed = o; });
            bool goneOnServer = listed.Ok && listed.Data.Data.ToJson().IndexOf("\"" + slug + "\"", StringComparison.Ordinal) < 0;
            Check("delete-campaign", foundDelete && goneOnServer && _ctx.Prompts.CampaignSlug.Length == 0,
                "button=" + foundDelete + " goneOnServer=" + goneOnServer + " active=" + _ctx.Prompts.CampaignSlug);

            Finish();
        }

        private IEnumerator Onboard(string name)
        {
            _ui.ShowTab("Onboard");
            var nameInput = Find<InputField>("Onboard", "Name");
            var create = Find<Button>("Onboard", "Create");
            if (nameInput == null || create == null) { Check("onboard-ui", false, "wizard inputs not found"); yield break; }
            nameInput.text = name;
            create.onClick.Invoke();
            GameObject last = create.gameObject;
            for (int step = 0; step < 30; step++)
            {
                yield return WaitFor(delegate { return last == null; }, 5f);
                yield return WaitFor(delegate { return OnboardReady(); }, 30f);
                var seed = Find<Button>("Onboard", "Seed");
                if (seed != null)
                {
                    Check("onboard-finalize", true, "answered " + step + " questions via UI");
                    yield break;
                }
                var finalize = Find<Button>("Onboard", "Finalize");
                var option = Find<Button>("Onboard", "Opt");
                var yes = Find<Button>("Onboard", "Yes");
                var answer = Find<InputField>("Onboard", "Answer");
                var send = Find<Button>("Onboard", "Send");
                Button click = finalize ?? option ?? yes;
                if (click == null && answer != null && send != null)
                {
                    answer.text = "A windswept frontier town on the edge of old ruins";
                    click = send;
                }
                if (click == null)
                {
                    Check("onboard-finalize", false, "stuck: " + FailText("Onboard"));
                    yield break;
                }
                last = click.gameObject;
                click.onClick.Invoke();
            }
            Check("onboard-finalize", false, "more than 30 steps");
        }

        private bool OnboardReady()
        {
            return Find<Button>("Onboard", "Seed") != null || Find<Button>("Onboard", "Finalize") != null
                || Find<Button>("Onboard", "Opt") != null || Find<Button>("Onboard", "Yes") != null
                || Find<InputField>("Onboard", "Answer") != null || TextContains("Onboard", "failed");
        }

        private IEnumerator Seed(string slug)
        {
            string batch = "{\"locations\":[{\"id\":\"locations/smoke-inn\",\"name\":\"Smoke Inn\"}],"
                + "\"characters\":[{\"id\":\"chars/smoke-hero\",\"name\":\"Smoke Hero\",\"isPc\":true,\"currentLocationId\":\"locations/smoke-inn\"}],"
                + "\"items\":[{\"id\":\"items/smoke-blade\",\"name\":\"Smoke Blade\",\"holderId\":\"chars/smoke-hero\"}]}";
            JsonValue batchValue;
            JsonValue.TryParse(batch, out batchValue);
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
            args.ObjectValue["batch"] = batchValue;
            McpOutcome<ToolPayload> result = null;
            yield return _ctx.Mcp.CallToolData(_ctx.Config, "build", "world_build", args, delegate (McpOutcome<ToolPayload> o) { result = o; });
            Check("seed-world", result.Ok, result.Ok ? result.Data.Summary : result.ErrorMessage);
        }

        private IEnumerator Chat(string llmUrl)
        {
            _ctx.Byok.BaseUrl = llmUrl;
            _ctx.Byok.Model = "smoke-mock";
            _ctx.Byok.SetApiKey("smoke-key");
            _ui.SendChatText("I look around the inn.");
            yield return WaitFor(delegate { return !_ctx.Driver.IsBusy; }, 60f);
            yield return WaitFor(delegate { return !Typewriter.AnyTyping; }, 10f);
            bool narrated = false;
            foreach (var seg in _ctx.Transcript.Segments)
            {
                if (seg.Kind == Model.SegmentKind.Narration && seg.Text.Contains("smoke-mock")) { narrated = true; }
            }
            Check("chat-turn-1", narrated, "mock narration " + (narrated ? "rendered" : "missing"));
            _ui.SendChatText("I order a drink.");
            yield return WaitFor(delegate { return !_ctx.Driver.IsBusy; }, 60f);
            // The mock asserts compaction server-side and says so in its narration.
            bool compacted = false;
            foreach (var seg in _ctx.Transcript.Segments)
            {
                if (seg.Kind == Model.SegmentKind.Narration && seg.Text.Contains("history compacted")) { compacted = true; }
            }
            Check("chat-turn-2-compaction", compacted, compacted ? "earlier tool results compacted" : "mock saw uncompacted history");
        }

        private IEnumerator Effects()
        {
            _ui.ShowTab("Chat");
            _ctx.Transcript.Add(new Model.TranscriptSegment
            {
                Kind = Model.SegmentKind.Roll,
                Roll = new Model.RollInfo { Label = "Perception", Detail = "d20 14 + 3 = 17 vs DC 12", Success = true },
            });
            _ctx.Transcript.Add(new Model.TranscriptSegment { Kind = Model.SegmentKind.Narration, Text = "The lantern gutters as the smoke-effects check completes." });
            _ctx.OnTranscriptChanged();
            yield return WaitFor(delegate { return false; }, 1.0f);
            bool rolling = Find<DiceTumble>("Chat", "Roll") != null;
            yield return WaitFor(delegate { return Find<DiceTumble>("Chat", "Roll") == null && !Typewriter.AnyTyping; }, 8f);
            Check("fx-dice-settles", TextEquals("Chat", "Outcome", "SUCCESS"), "tumbling at 1s=" + rolling);
            Check("fx-typewriter-settles", TextEquals("Chat", "Narration", "The lantern gutters as the smoke-effects check completes."),
                "narration fully revealed, no rich-text mask left");
        }

        private bool TextEquals(string tab, string objectName, string expected)
        {
            var panel = _ui.PanelFor(tab);
            foreach (var t in panel.GetComponentsInChildren<Text>(false))
            {
                if (t.gameObject.name == objectName && t.text == expected) { return true; }
            }
            return false;
        }

        // ---- UI helpers ----

        private IEnumerator ClickAndWait(string tab, string buttonName, Func<bool> done, float timeout)
        {
            var button = Find<Button>(tab, buttonName);
            if (button == null) { Check(tab + "-" + buttonName, false, "button not found"); yield break; }
            button.onClick.Invoke();
            yield return null;
            yield return WaitFor(done, timeout);
        }

        private static IEnumerator WaitFor(Func<bool> condition, float timeout)
        {
            float until = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < until) { yield return null; }
        }

        private T Find<T>(string tab, string objectName) where T : Component
        {
            var panel = _ui.PanelFor(tab);
            if (panel == null) { return null; }
            foreach (var c in panel.GetComponentsInChildren<T>(false))
            {
                if (c != null && c.gameObject.name == objectName) { return c; }
            }
            return null;
        }

        private bool TextContains(string tab, string needle)
        {
            var panel = _ui.PanelFor(tab);
            if (panel == null) { return false; }
            foreach (var t in panel.GetComponentsInChildren<Text>(false))
            {
                if (t != null && t.text != null && t.text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) { return true; }
            }
            return false;
        }

        private string FailText(string tab)
        {
            var panel = _ui.PanelFor(tab);
            if (panel == null) { return "no panel"; }
            foreach (var t in panel.GetComponentsInChildren<Text>(false))
            {
                if (t.gameObject.name == "Fail" || t.gameObject.name == "Status") { return t.text; }
            }
            return "ok";
        }

        private Button FindDeleteFor(string slug)
        {
            var panel = _ui.PanelFor("Campaigns");
            foreach (var t in panel.GetComponentsInChildren<Text>(false))
            {
                if (t.gameObject.name != "Name" || t.text.IndexOf(slug, StringComparison.Ordinal) < 0) { continue; }
                var row = t.transform.parent;
                var delete = row.Find("Delete");
                if (delete != null) { return delete.GetComponent<Button>(); }
            }
            return null;
        }

        private void Check(string step, bool ok, string detail)
        {
            if (ok) { _pass++; } else { _fail++; }
            Debug.Log(Tag + (ok ? "PASS " : "FAIL ") + step + ": " + detail);
        }

        private void Finish()
        {
            Check("no-exceptions", _exceptions == 0, _exceptions + " exceptions logged");
            Application.logMessageReceived -= OnLog;
            foreach (string key in RememberedPrefs)
            {
                string value;
                if (_savedPrefs.TryGetValue(key, out value)) { PlayerPrefs.SetString(key, value); }
                else { PlayerPrefs.DeleteKey(key); }
            }
            PlayerPrefs.Save();
            Debug.Log(Tag + "RESULT pass=" + _pass + " fail=" + _fail);
            Application.Quit(_fail == 0 ? 0 : 1);
        }
    }
}
