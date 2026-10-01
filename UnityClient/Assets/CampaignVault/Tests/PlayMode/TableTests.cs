using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;
using CampaignVault.UnityClient.Net;
using CampaignVault.UnityClient.Tests;
using CampaignVault.UnityClient.UI;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// The P4-P6 gate: the real UI in play mode, against the embedded server on
    /// a scratch DB and a scripted Dungeon Master. Creates a campaign through
    /// onboarding, plays a turn with a real Enter keypress, stops a slow turn
    /// with the STOP button (must abort in under a second), and photographs
    /// each screen into Library/VaultSnapshots for review.
    /// </summary>
    [Category("Integration")]
    public class TableTests
    {
        private TableHarness _h;

        [TearDown]
        public void TearDown()
        {
            if (_h != null) { _h.Dispose(); }
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds) { return TableHarness.WaitFor(condition, seconds); }

        private static IEnumerator Frames(int n) { return TableHarness.Frames(n); }

        private static void Step(string what) { TableHarness.Step(what); }

        private IEnumerator Snap(string name) { return _h.Snap(name); }

        [UnityTest]
        [Timeout(240000)]
        public IEnumerator PlayATurn_StopFast_AndPhotographEveryScreen()
        {
            _h = new TableHarness();
            yield return _h.Start();
            var chat = _h.Chat;
            var ui = _h.Ui;
            var s = _h.State;
            var c = _h.Controller;

            yield return WaitFor(delegate { return ui.Overlays.AnyOpen && s.CampaignsLoaded; }, 10f);
            Assert.IsInstanceOf<CampaignsOverlay>(ui.Overlays.Top, "no campaign: the campaign book opens first");
            yield return Snap("p6-campaigns-empty");

            Step("onboarding");
            // Onboarding, through the page.
            while (ui.Overlays.CloseTop()) { }
            ui.OpenOnboarding();
            yield return Snap("p6-onboarding-start");
            yield return c.BeginOnboarding("Lantern Test", "The Lantern Test", "Dnd5e");
            Assert.AreEqual(OnboardingPhase.Question, s.Onboarding.Phase, s.Onboarding.Error);
            yield return Snap("p6-onboarding-question");
            for (int step = 0; step < 30 && s.Onboarding.Phase != OnboardingPhase.Done; step++)
            {
                if (s.Onboarding.Phase == OnboardingPhase.ReadyToFinalize) { yield return c.FinalizeOnboarding(); continue; }
                var q = s.Onboarding.Question;
                string answer = q.Type == AnswerType.Choice ? q.Options[0] : q.Type == AnswerType.YesNo ? "yes"
                    : q.Type == AnswerType.Number ? "1"
                    : q.Type == AnswerType.Party ? VaultController.PartyAnswer(OnboardingState.PartyBuildAtTable, 1, null)
                    : q.Type == AnswerType.List ? "Aric Thorne — human fighter" : "A drowned mill town under a copper sky";
                yield return c.SubmitOnboardingAnswer(answer);
            }
            Assert.AreEqual(OnboardingPhase.Done, s.Onboarding.Phase, s.Onboarding.Error);
            yield return Snap("p6-onboarding-done");
            string slug = s.CampaignSlug;
            Assert.AreEqual("lantern-test", slug);
            while (ui.Overlays.CloseTop()) { }

            Step("seed + session");
            // Seed a small world, then open the session: the party frames and codex fill in.
            var args = JsonValue.Parse("{\"batch\":{\"locations\":[{\"id\":\"locations/old-mill\",\"name\":\"The Old Mill\"}],"
                + "\"characters\":[{\"id\":\"chars/aric\",\"name\":\"Aric Thorne\",\"isPc\":true,\"currentLocationId\":\"locations/old-mill\"},"
                + "{\"id\":\"chars/mirelle\",\"name\":\"Mirelle\",\"currentLocationId\":\"locations/old-mill\"}],"
                + "\"items\":[{\"id\":\"items/longsword\",\"name\":\"Longsword\",\"holderId\":\"chars/aric\"},{\"id\":\"items/rope\",\"name\":\"Hempen Rope\",\"holderId\":\"chars/aric\"}]}}");
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
            McpOutcome<ToolPayload> seeded = null;
            yield return s.Mcp.CallToolData(s.Config, "build", "world_build", args, delegate (McpOutcome<ToolPayload> o) { seeded = o; });
            Assert.IsTrue(seeded.Ok, seeded.ErrorMessage);
            yield return c.StartSession("The drowned mill");
            Assert.IsNotNull(s.Session, s.SessionStatus);
            Assert.AreEqual("chars/aric", s.PcId);

            Step("turn");
            // A turn through the command bar: a real Enter keypress in the field.
            // Two-pass: the loop (with tools) searches, then says DONE; the
            // storyteller (no tools) writes the scene.
            chat.Respond = delegate (JsonValue request, int i)
            {
                bool storyteller = request.Get("tools").Kind != JsonKind.Array;
                bool first = request.GetArray("messages").Count(m => m.GetString("role", string.Empty) == "tool") == 0;
                if (!storyteller && first)
                {
                    return new ScriptedChat.Reply { Body = ScriptedChat.ToolCall("c1", "search_world", "{\"campaignName\":\"" + slug + "\",\"query\":\"mill\"}") };
                }
                if (!storyteller) { return new ScriptedChat.Reply { Body = ScriptedChat.Prose("DONE") }; }
                return new ScriptedChat.Reply
                {
                    Body = ScriptedChat.Prose("The mill wheel groans in the dark water, its paddles slick with weed. Flour lies spilled like **snow** across the boards.\n\n"
                        + "Mirelle: “Those aren't rat tracks. Something walked through here on two legs — carrying the miller.”"),
                };
            };
            int before = s.Transcript.Segments.Count;
            Step("before enter: busy=" + s.Driver.IsBusy + " overlays=" + ui.Overlays.AnyOpen + " focused=" + (ui.Root.focusController.focusedElement != null ? ui.Root.focusController.focusedElement.ToString() : "none"));
            ui.Command.Input.Focus();
            ui.Command.Input.value = "I crouch by the sacks and look for tracks in the flour.";
            using (var enter = KeyDownEvent.GetPooled('\n', KeyCode.Return, EventModifiers.None)) { ui.Command.Input.SendEvent(enter); }
            yield return WaitFor(delegate { return s.Driver.IsBusy; }, 3f);
            yield return WaitFor(delegate { return !s.Driver.IsBusy; }, 30f);
            Assert.AreEqual(string.Empty, ui.Command.Input.value, "Enter sends and clears the box");
            var segs = s.Transcript.Segments.Skip(before).ToList();
            Assert.IsTrue(segs.Any(x => x.Kind == SegmentKind.Player), "player line shown");
            Assert.IsTrue(segs.Any(x => x.Kind == SegmentKind.Narration && x.Text.Contains("mill wheel")), "narration shown: " + string.Join(" | ", segs.Select(x => x.Kind + ":" + x.Text).ToArray()));
            Assert.IsTrue(segs.Any(x => x.Kind == SegmentKind.NpcVoice && x.Speaker == "Mirelle"), "voice line split out");
            Assert.AreEqual(s.Transcript.Segments.Count, ui.Log.EntryCount, "one log entry per segment");

            // Showcase the roll cards the server's resolvers produce.
            foreach (var roll in SegmentSplitter.ExtractRolls("Search (Perception): Success. Rolled 17 vs DC 14. "
                + "Longsword vs Ghoul: Hit for 14 damage. (Attack 25 vs AC 13). CRITICAL HIT! Added 6 extra damage. "
                + "Poison Needle (Constitution Save): Failure. Rolled 9 vs DC 13.")) { s.Transcript.Add(roll); }
            yield return Frames(40);
            yield return Snap("p4-table");

            Step("stop");
            // STOP must cut a slow turn in well under a second.
            chat.Respond = delegate { return new ScriptedChat.Reply { Body = ScriptedChat.Prose("too late"), DelayMs = 20000 }; };
            int sent = chat.Requests.Count;
            Assert.IsTrue(c.SendPlayerText("I wait in silence."));
            yield return WaitFor(delegate { return chat.Requests.Count > sent; }, 5f);
            yield return Frames(3);
            var act = ui.Root.Q<Button>("CommandAct");
            StringAssert.Contains("STOP", act.Q<Label>().text);
            yield return Snap("p4-busy");
            float pressed = Time.realtimeSinceStartup;
            using (var submit = NavigationSubmitEvent.GetPooled()) { submit.target = act; act.SendEvent(submit); }
            yield return WaitFor(delegate { return !s.Driver.IsBusy; }, 3f);
            float took = Time.realtimeSinceStartup - pressed;
            Assert.IsFalse(s.Driver.IsBusy);
            Assert.Less(took, 1f, "STOP took " + took + "s");
            Assert.IsTrue(s.Transcript.Segments.Last().Text.StartsWith("Stopped"));

            Step("sheet");
            // P5: the sheet, keyed off the party frame's id.
            ui.OpenSheet(s.PcId);
            yield return WaitFor(delegate { return ui.Root.Q(className: "cv-sheet__hero") != null; }, 10f);
            yield return Snap("p5-sheet");
            while (ui.Overlays.CloseTop()) { }

            Step("settings");
            // P6: settings and first-run setup.
            ui.OpenSettings(0);
            yield return Snap("p6-settings-provider");
            ui.OpenSettings(SettingsOverlay.AdvancedTab);
            yield return WaitFor(delegate { return s.Tools.Count > 0; }, 10f);
            yield return Snap("n7-settings-advanced");
            // N6: the plugin manager, listing what the scratch server loaded.
            ui.OpenSettings(SettingsOverlay.PluginsTab);
            yield return WaitFor(delegate { return s.PluginsLoaded; }, 10f);
            Assert.AreEqual(string.Empty, s.PluginsError);
            Assert.Greater(s.Plugins.Count, 0, "the scratch server's bundled plugins are listed");
            yield return Snap("n6-settings-plugins");
            while (ui.Overlays.CloseTop()) { }
            ui.OpenSetup();
            yield return Snap("p6-setup");
            while (ui.Overlays.CloseTop()) { }
            ui.OpenCampaigns();
            yield return WaitFor(delegate { return s.Campaigns.Count > 0; }, 10f);
            yield return Snap("p6-campaigns");
            while (ui.Overlays.CloseTop()) { }

            Step("cleanup");
            yield return c.DeleteCampaign(slug);
            Assert.AreEqual(string.Empty, s.CampaignSlug);
        }
    }
}
