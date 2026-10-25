using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Json;
using CampaignVault.UnityClient.Model;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>
    /// Phase 4 A1–A3: the table follows the DM's own take_turn results instead of re-reading start_session
    /// after every turn, the prompt's fingerprint follows too, and a turn that committed is never retried.
    /// </summary>
    public class TableFromTurnTests
    {
        private static JsonValue Parse(string json) { return JsonValue.Parse(json); }

        private static SessionDigest Table()
        {
            return SessionDigest.FromResult(Parse("{\"sessionNumber\":1,\"partyFingerprint\":\"chars/ari:12/12@locations/mill\",\"time\":\"Day 1 — Morning\","
                + "\"party\":[{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true,\"hp\":\"12/12\",\"locationId\":\"locations/mill\",\"classLevel\":\"Fighter 1\"}],"
                + "\"activeQuests\":[{\"id\":\"quests/find-the-miller\",\"title\":\"Find the miller\",\"openObjectives\":2}]}"));
        }

        [Test]
        public void Fingerprint_MovesHpAndLocation()
        {
            var table = Table();
            var update = table.ApplyTurn(null, Parse("{\"partyFingerprint\":\"chars/ari:7/12@locations/cellar\"}"));
            Assert.IsTrue(update.Party);
            Assert.AreEqual("chars/ari:7/12@locations/cellar", table.Fingerprint);
            var ari = table.Member("chars/ari");
            Assert.AreEqual(7, ari.CurHp);
            Assert.AreEqual(12, ari.MaxHp);
            Assert.AreEqual("locations/cellar", ari.Location);
        }

        [Test]
        public void PartySection_AddsAMemberTheTableHasNotSeen()
        {
            var table = Table();
            var update = table.ApplyTurn(null, Parse("{\"party\":[{\"character\":{\"id\":\"chars/bo\",\"name\":\"Bo\",\"isPc\":false,\"isPartyCompanion\":true,"
                + "\"currentHp\":5,\"maxHp\":9,\"classLevel\":\"Scout 1\",\"currentLocationId\":\"locations/mill\"},\"equipped\":[{\"name\":\"Short sword\"}],\"carried\":[{\"name\":\"Rope\"}]}]}"));
            Assert.IsTrue(update.Party);
            var bo = table.Member("chars/bo");
            Assert.IsNotNull(bo);
            Assert.AreEqual("Bo", bo.Name);
            Assert.IsFalse(bo.IsPc);
            Assert.AreEqual(5, bo.CurHp);
            CollectionAssert.AreEqual(new[] { "Short sword" }, bo.Equipped);
            CollectionAssert.AreEqual(new[] { "Rope" }, bo.Carried);
            Assert.AreEqual("chars/bo", table.Companions.Single().Id);
        }

        [Test]
        public void ClockLine_SetsTheTime()
        {
            var table = Table();
            var update = table.ApplyTurn(null, Parse("{\"summary\":[\"Clock: +8h. It is now Day 2, Month 1, Year 1 — Morning, hour 7.\",\"HP now 12/12\"]}"));
            Assert.IsTrue(update.Time);
            Assert.AreEqual("Day 2, Month 1, Year 1 — Morning", table.Time);
        }

        [Test]
        public void LevelUpReady_FollowsTheTurn()
        {
            var table = Table();
            var update = table.ApplyTurn(null, Parse("{\"levelUpReady\":[\"chars/ari\"]}"));
            Assert.IsTrue(update.Party);
            Assert.IsTrue(table.Member("chars/ari").LevelUpReady);
            table.ApplyTurn(null, Parse("{\"levelUpReady\":[]}"));
            Assert.IsFalse(table.Member("chars/ari").LevelUpReady);
        }

        [Test]
        public void QuestChanges_ComeFromTheDmsOwnArguments()
        {
            var table = Table();
            var args = Parse("{\"campaignName\":\"mill\",\"request\":{\"changes\":["
                + "{\"$type\":\"quest_progress\",\"questId\":\"quests/find-the-miller\",\"newState\":\"Complete\"},"
                + "{\"$type\":\"quest_progress\",\"questId\":\"quests/stop-the-flood\",\"newState\":\"Open\"}]}}");
            var before = table.Quests;
            var update = table.ApplyTurn(args, Parse("{\"committed\":true}"));
            Assert.IsTrue(update.Quests);
            Assert.AreNotSame(before, table.Quests, "the quests page refreshes by list identity");
            Assert.AreEqual(1, table.Quests.Count);
            Assert.AreEqual("quests/stop-the-flood", table.Quests[0].Id);
            Assert.AreEqual("Stop the flood", table.Quests[0].Title);
            CollectionAssert.AreEqual(new[] { "quests/stop-the-flood" }, update.NewQuestIds);
        }

        [Test]
        public void Conditions_FollowStatusChanges()
        {
            var table = Table();
            var args = Parse("{\"request\":{\"changes\":[{\"$type\":\"status\",\"characterId\":\"chars/ari\",\"status\":\"Poisoned\"}]}}");
            Assert.IsTrue(table.ApplyTurn(args, Parse("{}")).Party);
            CollectionAssert.Contains(table.Member("chars/ari").Conditions, "Poisoned");
            args = Parse("{\"request\":{\"changes\":[{\"$type\":\"status_remove\",\"characterId\":\"chars/ari\",\"status\":\"Poisoned\"}]}}");
            Assert.IsTrue(table.ApplyTurn(args, Parse("{}")).Party);
            CollectionAssert.DoesNotContain(table.Member("chars/ari").Conditions, "Poisoned");
        }

        [Test]
        public void QuietTurn_ChangesNothing()
        {
            var table = Table();
            var update = table.ApplyTurn(Parse("{\"request\":{\"changes\":[{\"$type\":\"event\",\"summary\":\"x\"}]}}"), Parse("{\"npcs\":[]}"));
            Assert.IsFalse(update.Any);
        }

        [Test]
        public void CommittedTheWorld_NeedsASuccessfulWrite()
        {
            var failed = new TurnRecord();
            failed.Tools.Add("take_turn");
            Assert.IsTrue(VaultController.ChangedTheWorld(failed));
            Assert.IsFalse(VaultController.CommittedTheWorld(failed), "a failed take_turn committed nothing: the line can be resent");
            var read = new TurnRecord();
            read.Tools.Add("get_entity");
            read.Succeeded.Add("get_entity");
            Assert.IsFalse(VaultController.CommittedTheWorld(read));
            var committed = new TurnRecord();
            committed.Tools.Add("take_turn");
            committed.Succeeded.Add("take_turn");
            Assert.IsTrue(VaultController.CommittedTheWorld(committed));
        }
    }

    /// <summary>The controller end of A1/A2: a take_turn result moves the table and the prompt's fingerprint.</summary>
    public class TableFromTurnControllerTests
    {
        private GameObject _go;
        private VaultBootstrap _boot;

        [SetUp]
        public void SetUp()
        {
            _go = new GameObject("TableFromTurnControllerTest");
            _boot = _go.AddComponent<VaultBootstrap>();
            _boot.Initialize(new MemoryPrefs());
        }

        [TearDown]
        public void TearDown() { Object.DestroyImmediate(_go); }

        [Test]
        public void TakeTurn_UpdatesTableAndPromptFingerprint()
        {
            var c = _boot.Controller;
            var s = _boot.State;
            c.SelectCampaign("mill", "Dnd5e");
            c.OnDriverTool("start_session", "{\"success\":true,\"data\":{\"sessionNumber\":1,\"partyFingerprint\":\"chars/ari:12/12@locations/mill\","
                + "\"party\":[{\"id\":\"chars/ari\",\"name\":\"Ari\",\"isPc\":true,\"hp\":\"12/12\",\"locationId\":\"locations/mill\"}]}}");
            StringAssert.Contains("clientPartyFingerprint=\"chars/ari:12/12@locations/mill\"", s.Prompts.CampaignLine());

            c.OnDriverTool("take_turn", JsonValue.Parse("{\"request\":{\"changes\":[{\"$type\":\"hp\",\"characterId\":\"chars/ari\",\"delta\":-5}]}}"),
                "{\"success\":true,\"data\":{\"committed\":true,\"partyFingerprint\":\"chars/ari:7/12@locations/mill\"}}");
            Assert.AreEqual(7, s.Session.Member("chars/ari").CurHp);
            StringAssert.Contains("clientPartyFingerprint=\"chars/ari:7/12@locations/mill\"", s.Prompts.CampaignLine(), "the next request echoes the fresh fingerprint (A2)");

            c.OnDriverTool("take_turn", null, "{\"success\":false,\"summary\":\"rolled back\"}");
            Assert.AreEqual(7, s.Session.Member("chars/ari").CurHp, "a failed turn moves nothing");
        }
    }
}
