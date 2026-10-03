using System.IO;
using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>The per-campaign cost counter: persistence, following the campaign, and the words on the chip.</summary>
    public class CampaignUsageStoreTests
    {
        private string _dir;

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(Path.GetTempPath(), "vault-usage-" + System.Guid.NewGuid().ToString("N"));
            CampaignUsageStore.DirOverride = _dir;
        }

        [TearDown]
        public void TearDown()
        {
            CampaignUsageStore.DirOverride = null;
            if (Directory.Exists(_dir)) { Directory.Delete(_dir, true); }
        }

        private static TokenUsage Call(double cost, bool exact = false, int prompt = 100)
        {
            var u = TokenUsage.FromJson(JsonValue.Parse("{\"prompt_tokens\":" + prompt + ",\"completion_tokens\":10}"));
            u.Cost = cost;
            if (exact) { u.ExactCalls = 1; } else { u.EstimatedCalls = 1; }
            return u;
        }

        [Test]
        public void Add_ThenReloadInAFreshStore_KeepsTotalsAndPerModelRows()
        {
            var store = new CampaignUsageStore();
            store.Add("ember", "gpt-4o", Call(0.25));
            store.Add("ember", "gpt-4o", Call(0.25, true));
            store.Add("ember", "claude-sonnet-5-5", Call(1.0));

            var fresh = new CampaignUsageStore();
            TokenUsage total = fresh.Total("ember");
            Assert.AreEqual(1.5, total.Cost, 1e-9);
            Assert.AreEqual(3, total.Calls);
            Assert.AreEqual(300, total.Prompt);
            Assert.AreEqual(1, total.ExactCalls);
            Assert.AreEqual(2, total.EstimatedCalls);
            var rows = fresh.Breakdown("ember");
            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("gpt-4o", rows[0].Key);
            Assert.AreEqual(0.5, rows[0].Value.Cost, 1e-9);
        }

        [Test]
        public void SwitchingCampaigns_KeepsEachCountSeparate()
        {
            var store = new CampaignUsageStore();
            store.Add("ember", "gpt-4o", Call(1));
            store.Add("tide", "gpt-4o", Call(5));
            Assert.AreEqual(1, store.Total("ember").Cost, 1e-9);
            Assert.AreEqual(5, store.Total("tide").Cost, 1e-9);
            store.Add("ember", "gpt-4o", Call(1));
            Assert.AreEqual(2, new CampaignUsageStore().Total("ember").Cost, 1e-9);
        }

        [Test]
        public void EstimateAndUnpricedFlagsSurviveSaving_SoTheChipStaysHonest()
        {
            var store = new CampaignUsageStore();
            store.Add("ember", "gpt-4o", Call(2));
            var unpriced = Call(0);
            unpriced.EstimatedCalls = 0;
            unpriced.UnpricedCalls = 1;
            store.Add("ember", "mystery-1", unpriced);
            Assert.AreEqual("~$2.00+", new CampaignUsageStore().Total("ember").CostText());
        }

        [Test]
        public void MissingOrCorruptFile_StartsFromZero()
        {
            Assert.IsTrue(new CampaignUsageStore().Total("ember").IsEmpty);
            Directory.CreateDirectory(_dir);
            File.WriteAllText(CampaignUsageStore.PathFor("ember"), "{ not json at all");
            Assert.IsTrue(new CampaignUsageStore().Total("ember").IsEmpty);
            var store = new CampaignUsageStore();
            store.Add("ember", "gpt-4o", Call(1));
            Assert.AreEqual(1, new CampaignUsageStore().Total("ember").Cost, 1e-9, "a bad file is replaced by the next save");
        }

        [Test]
        public void NoCampaign_CountsNothing()
        {
            var store = new CampaignUsageStore();
            store.Add(string.Empty, "gpt-4o", Call(1));
            Assert.IsTrue(store.Total(string.Empty).IsEmpty);
            Assert.IsFalse(Directory.Exists(_dir));
        }

        [Test]
        public void Reset_ZeroesTheCampaignOnDisk()
        {
            var store = new CampaignUsageStore();
            store.Add("ember", "gpt-4o", Call(1));
            store.Add("tide", "gpt-4o", Call(3));
            store.Reset("ember");
            Assert.IsTrue(store.Total("ember").IsEmpty);
            Assert.IsTrue(new CampaignUsageStore().Total("ember").IsEmpty);
            Assert.AreEqual(3, new CampaignUsageStore().Total("tide").Cost, 1e-9, "other campaigns are untouched");
        }

        [Test]
        public void SlugsWithPathCharacters_CannotEscapeTheFolder()
        {
            string path = CampaignUsageStore.PathFor("../../evil");
            Assert.AreEqual(_dir, Path.GetDirectoryName(path));
        }

        [Test]
        public void Chip_ShowsSessionAndCampaign_HiddenUntilSomethingIsCounted()
        {
            Assert.AreEqual(string.Empty, UsageSummary.Chip(new TokenUsage(), new TokenUsage()));
            var session = Call(0.12);
            var campaign = Call(1.92);
            Assert.AreEqual("session ~$0.12 · campaign ~$1.92", UsageSummary.Chip(session, campaign));
            Assert.AreEqual("session ~$0.12", UsageSummary.Chip(session, null));
            Assert.AreEqual("session $0 · campaign ~$1.92", UsageSummary.Chip(new TokenUsage(), campaign));
        }

        [Test]
        public void Tooltip_ExplainsEstimatesAndNamesThePriceDate()
        {
            string tip = UsageSummary.Tooltip(Call(0.12), Call(1.92), null, "2026-10-03");
            StringAssert.Contains("2026-10-03", tip);
            StringAssert.Contains("estimated", tip);
            StringAssert.Contains("This campaign", tip);
        }
    }
}
