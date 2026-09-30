using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Net;

namespace CampaignVault.UnityClient.PlayTests
{
    /// <summary>
    /// N7 fixtures: a fully built 5e table and PF2e table (PC with class,
    /// ancestry, abilities and gear; a companion; NPCs; quests; a hurt and
    /// poisoned PC; a checkpoint handoff), photographed on every screen the UI
    /// pass redesigns. Each sheet's get_entity and the session kickoff are
    /// saved next to the snapshots as JSON, so the redesign works from what the
    /// server really sends. Scratch server, scratch campaigns, deleted after.
    /// </summary>
    [Category("Integration")]
    public class SheetFixtureTests
    {
        private TableHarness _h;

        [TearDown]
        public void TearDown()
        {
            if (_h != null) { _h.Dispose(); }
        }

        private static string Fixture(string name)
        {
            return File.ReadAllText(Path.Combine(Application.dataPath, "CampaignVault", "Tests", "PlayMode", "Fixtures", name));
        }

        private static JsonValue Args(string slug)
        {
            var args = JsonValue.NewObject();
            args.ObjectValue["campaignName"] = JsonValue.FromString(slug);
            return args;
        }

        private static void Dump(string name, JsonValue value)
        {
            Directory.CreateDirectory(UiSnapshotTests.SnapshotDir);
            File.WriteAllText(Path.Combine(UiSnapshotTests.SnapshotDir, name + ".json"), value.ToJson());
        }

        [UnityTest]
        [Timeout(300000)]
        public IEnumerator FullTables_PhotographSheetsCompanionsAndCodex()
        {
            _h = new TableHarness();
            yield return _h.Start();
            // No campaign yet: the campaign book opens first. Put it away.
            yield return TableHarness.WaitFor(delegate { return _h.Ui.Overlays.AnyOpen && _h.State.CampaignsLoaded; }, 10f);
            while (_h.Ui.Overlays.CloseTop()) { }
            yield return Table("dnd5e", "Dnd5e", "n7-dnd5e.json", "chars/aric", "chars/wren", "chars/tam");
            yield return Table("pf2e", "Pf2e", "n7-pf2e.json", "chars/liesl", "chars/brakk", "chars/archivist-orla");
        }

        /// <summary>Scrolls the top page to its end and photographs the rest of it.</summary>
        private IEnumerator ScrollToEnd(string snap)
        {
            var scroll = _h.Ui.Overlays.Top.Root.Q<ScrollView>(className: "cv-modal__scroll");
            scroll.verticalScroller.value = scroll.verticalScroller.highValue;
            yield return _h.Snap(snap);
        }

        private IEnumerator Table(string tag, string system, string fixture, string pcId, string companionId, string npcId)
        {
            var s = _h.State;
            var c = _h.Controller;
            var ui = _h.Ui;
            string slug = "n7-" + tag;
            TableHarness.Step("table " + slug);

            var create = Args(slug);
            create.ObjectValue["name"] = JsonValue.FromString(slug);
            create.ObjectValue.Remove("campaignName");
            create.ObjectValue["initialSystem"] = JsonValue.FromString(system);
            yield return _h.Call("build", "create_campaign", create, null);
            var build = Args(slug);
            build.ObjectValue["batch"] = JsonValue.Parse(Fixture(fixture));
            yield return _h.Call("build", "world_build", build, delegate (ToolPayload p) { Dump("n7-" + tag + "-world-build", p.Envelope); });

            c.SelectCampaign(slug, system);
            while (ui.Overlays.CloseTop()) { }
            yield return c.StartSession("The first night");
            Assert.IsNotNull(s.Session, s.SessionStatus);
            Assert.AreEqual(pcId, s.PcId);

            // A rough first night: the PC is hurt and poisoned, the companion scratched.
            var turn = Args(slug);
            turn.ObjectValue["request"] = JsonValue.Parse("{\"narrative\":\"An ambush on the road; a poisoned blade.\",\"changes\":["
                + "{\"$type\":\"event\",\"category\":\"Combat\",\"summary\":\"Bandits ambushed the party at dusk; one blade was poisoned.\",\"involved\":[\"" + pcId + "\",\"" + companionId + "\"]},"
                + "{\"$type\":\"hp\",\"characterId\":\"" + pcId + "\",\"delta\":-11},"
                + "{\"$type\":\"hp\",\"characterId\":\"" + companionId + "\",\"delta\":-4},"
                + "{\"$type\":\"status\",\"characterId\":\"" + pcId + "\",\"status\":\"Poisoned\"}]}");
            yield return _h.Call("play", "take_turn", turn, null);

            // A checkpoint, so the journal has a story so far and known faces.
            var draft = new HandoffDraft
            {
                LastSession = "The party reached the crossing, took the job, and was ambushed on the way back.",
                StorySoFar = "Hired for a quiet errand, the party found the countryside anything but quiet.",
                Threads = "Who hired the bandits?\nThe poisoned blade bore a guild mark.",
                Npcs = npcId + " | grateful, but hiding something",
                Intent = "Rest, then follow the tracks at first light.",
                Tone = "Slow dread, small kindnesses.",
            };
            yield return c.EndSession(draft, true);
            Assert.IsEmpty(s.HandoffIssues, string.Join("; ", s.HandoffIssues.ToArray()));
            yield return c.RefreshTable();

            yield return _h.Call("play", "start_session", Args(slug), delegate (ToolPayload p) { Dump("n7-" + tag + "-session", p.Data); });
            foreach (string id in new[] { pcId, companionId })
            {
                var get = Args(slug);
                get.ObjectValue["entityId"] = JsonValue.FromString(id);
                yield return _h.Call("play", "get_entity", get, delegate (ToolPayload p) { Dump("n7-" + tag + "-" + id.Substring(id.IndexOf('/') + 1), p.Data); });
            }

            yield return TableHarness.Frames(10);
            for (int tab = 0; tab < 4; tab++)
            {
                ui.Codex.Show(tab);
                if (tab == 1) { yield return TableHarness.WaitFor(delegate { return s.Companions.Count > 0; }, 10f); }
                yield return _h.Snap("n7-" + tag + "-codex-" + new[] { "quests", "scene", "pack", "journal" }[tab]);
            }
            ui.Codex.Show(0);

            ui.OpenSheet(pcId);
            yield return TableHarness.WaitFor(delegate { return ui.Root.Q(className: "cv-sheet__hero") != null || ui.Root.Q(className: "cv-statblock") != null; }, 10f);
            yield return _h.Snap("n7-" + tag + "-sheet-pc");
            yield return ScrollToEnd("n7-" + tag + "-sheet-pc-lower");
            while (ui.Overlays.CloseTop()) { }
            ui.OpenSheet(companionId);
            yield return TableHarness.WaitFor(delegate { return ui.Root.Q(className: "cv-sheet__hero") != null || ui.Root.Q(className: "cv-statblock") != null; }, 10f);
            yield return _h.Snap("n7-" + tag + "-sheet-companion");
            yield return ScrollToEnd("n7-" + tag + "-sheet-companion-lower");
            while (ui.Overlays.CloseTop()) { }

            yield return c.DeleteCampaign(slug);
            Assert.AreEqual(string.Empty, s.CampaignSlug);
        }
    }
}
