using NUnit.Framework;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.Tests
{
    /// <summary>Cost counter: the price table, how model ids match it, and how a call's usage becomes dollars.</summary>
    public class ModelPricingTests
    {
        private const string Table = "{\"asOf\":\"2026-10-03\",\"models\":{"
            + "\"claude-sonnet-5-5\":{\"input\":2,\"cachedInput\":0.2,\"cacheWrite\":2.5,\"output\":10},"
            + "\"claude-opus-4\":{\"input\":15,\"output\":75},"
            + "\"gpt-4o\":{\"input\":2.5,\"cachedInput\":1.25,\"output\":10},"
            + "\"gpt-4o-mini\":{\"input\":0.15,\"output\":0.6}}}";

        private static ModelPricing Pricing() { return ModelPricing.Parse(Table); }

        private static TokenUsage Usage(string json) { return TokenUsage.FromJson(JsonValue.Parse(json)); }

        private static ProviderProfile Profile(string preset = "openai", string url = "https://api.openai.com/v1")
        {
            return new ProviderProfile { Preset = preset, BaseUrl = url };
        }

        [Test]
        public void Bundled_TableLoadsWithRows()
        {
            var bundled = ModelPricing.Bundled;
            Assert.Greater(bundled.Count, 20);
            Assert.IsNotEmpty(bundled.AsOf);
            Assert.IsNotNull(bundled.Find("claude-sonnet-5-5"));
            Assert.IsNotNull(bundled.Find("gpt-4o"));
        }

        [TestCase("claude-sonnet-5-5")]
        [TestCase("anthropic/claude-sonnet-5.5")]
        [TestCase("Anthropic/Claude-Sonnet-5.5:thinking")]
        [TestCase("claude-sonnet-5-5-20260301")]
        [TestCase("claude-sonnet-5-5-latest")]
        public void Find_MatchesVendorPrefixDotsVariantsAndDates(string id)
        {
            Assert.AreEqual(2.0, Pricing().Find(id).Input, id);
        }

        [Test]
        public void Find_LongestKeyWins_MiniIsNotPricedAsFullModel()
        {
            Assert.AreEqual(0.15, Pricing().Find("gpt-4o-mini").Input);
            Assert.AreEqual(0.15, Pricing().Find("gpt-4o-mini-2024-07-18").Input);
            Assert.AreEqual(2.5, Pricing().Find("gpt-4o-2024-08-06").Input);
        }

        [Test]
        public void Find_UnknownNewerVersionIsNotPricedAsOlderOne()
        {
            Assert.IsNull(Pricing().Find("claude-opus-4-9"));
            Assert.IsNull(Pricing().Find("some-local-model"));
            Assert.IsNull(Pricing().Find(""));
        }

        [Test]
        public void Parse_MissingCachedAndWriteRatesFallBackToInput_BadRowsAreSkipped()
        {
            var pricing = ModelPricing.Parse("{\"models\":{\"a\":{\"input\":3,\"output\":6},\"bad\":{\"input\":1}}}");
            Assert.AreEqual(3, pricing.Find("a").CachedInput);
            Assert.AreEqual(3, pricing.Find("a").CacheWrite);
            Assert.IsNull(pricing.Find("bad"));
            Assert.AreEqual(0, ModelPricing.Parse("not json").Count);
            Assert.AreEqual(0, ModelPricing.Parse(null).Count);
        }

        [Test]
        public void ApplyPrice_EstimatesFromTokensWithCachedDiscount()
        {
            var u = Usage("{\"prompt_tokens\":1000000,\"completion_tokens\":100000,\"prompt_tokens_details\":{\"cached_tokens\":800000}}");
            u.ApplyPrice(Pricing(), Profile(), "claude-sonnet-5-5");
            // 200k plain × $2 + 800k cached × $0.20 + 100k out × $10, per million
            Assert.AreEqual(1.56, u.Cost, 1e-9);
            Assert.AreEqual(1, u.EstimatedCalls);
            Assert.AreEqual("~$1.56", u.CostText());
        }

        [Test]
        public void FromJson_AnthropicShapeCountsCacheOutsideInputTokens()
        {
            var u = Usage("{\"input_tokens\":200000,\"output_tokens\":100000,\"cache_read_input_tokens\":800000,\"cache_creation_input_tokens\":0}");
            Assert.AreEqual(200000, u.Prompt, "Prompt stays as reported");
            Assert.AreEqual(800000, u.Cached);
            u.ApplyPrice(Pricing(), Profile(), "claude-sonnet-5-5");
            Assert.AreEqual(1.56, u.Cost, 1e-9);
        }

        [Test]
        public void ApplyPrice_CacheWriteTokensUseTheWriteRate()
        {
            var u = Usage("{\"input_tokens\":0,\"output_tokens\":0,\"cache_creation_input_tokens\":1000000}");
            u.ApplyPrice(Pricing(), Profile(), "claude-sonnet-5-5");
            Assert.AreEqual(2.5, u.Cost, 1e-9);
        }

        [Test]
        public void ApplyPrice_ProviderReportedCostIsExactAndWins()
        {
            var u = Usage("{\"prompt_tokens\":1000,\"completion_tokens\":100,\"cost\":0.0042}");
            u.ApplyPrice(Pricing(), Profile("openrouter", "https://openrouter.ai/api/v1"), "anthropic/claude-sonnet-5.5");
            Assert.AreEqual(0.0042, u.Cost, 1e-12);
            Assert.AreEqual(1, u.ExactCalls);
            Assert.AreEqual("$0.0042", u.CostText());
        }

        [Test]
        public void ApplyPrice_LocalAndFreeVariantsAreFree()
        {
            var local = Usage("{\"prompt_tokens\":500,\"completion_tokens\":50}");
            local.ApplyPrice(Pricing(), Profile("ollama", "http://localhost:11434/v1"), "llama3.1");
            Assert.AreEqual("local · free", local.CostText());

            var custom = Usage("{\"prompt_tokens\":500,\"completion_tokens\":50}");
            custom.ApplyPrice(Pricing(), Profile("custom", "http://127.0.0.1:8080/v1"), "gpt-4o");
            Assert.AreEqual("local · free", custom.CostText(), "a custom endpoint on localhost is local too");

            var free = Usage("{\"prompt_tokens\":500,\"completion_tokens\":50}");
            free.ApplyPrice(Pricing(), Profile("openrouter", "https://openrouter.ai/api/v1"), "meta-llama/llama-3.3-70b-instruct:free");
            Assert.AreEqual("local · free", free.CostText());
            Assert.AreEqual(0, free.Cost);
        }

        [Test]
        public void ApplyPrice_UnknownModelStaysUnpriced_ProfileOverrideFixesIt()
        {
            var u = Usage("{\"prompt_tokens\":1000000,\"completion_tokens\":1000000}");
            u.ApplyPrice(Pricing(), Profile("custom", "https://example.test/v1"), "mystery-1");
            Assert.AreEqual(1, u.UnpricedCalls);
            Assert.IsFalse(u.HasCost);
            Assert.AreEqual("price unknown", u.CostText());

            var priced = Usage("{\"prompt_tokens\":1000000,\"completion_tokens\":1000000}");
            var profile = Profile("custom", "https://example.test/v1");
            profile.PriceInPerM = 1f;
            profile.PriceOutPerM = 3f;
            priced.ApplyPrice(Pricing(), profile, "mystery-1");
            Assert.AreEqual(4.0, priced.Cost, 1e-6);
            Assert.AreEqual("~$4.00", priced.CostText());
        }

        [Test]
        public void ApplyPrice_NoUsageReportedStaysEmpty()
        {
            var none = TokenUsage.FromJson(JsonValue.Null);
            none.ApplyPrice(Pricing(), Profile(), "gpt-4o");
            Assert.IsTrue(none.IsEmpty);
            Assert.AreEqual(string.Empty, none.CostText());
        }

        [Test]
        public void Add_MixedExactAndEstimatedIsAnEstimate_UnpricedCallsMarkItPartial()
        {
            var total = new TokenUsage();
            var exact = Usage("{\"prompt_tokens\":10,\"completion_tokens\":1,\"cost\":0.5}");
            exact.ApplyPrice(Pricing(), Profile(), "gpt-4o");
            var estimated = Usage("{\"prompt_tokens\":1000000,\"completion_tokens\":0}");
            estimated.ApplyPrice(Pricing(), Profile(), "gpt-4o");
            total.Add(exact);
            total.Add(estimated);
            Assert.AreEqual(3.0, total.Cost, 1e-9);
            Assert.AreEqual("~$3.00", total.CostText());

            var unknown = Usage("{\"prompt_tokens\":10,\"completion_tokens\":1}");
            unknown.ApplyPrice(Pricing(), Profile(), "mystery-1");
            total.Add(unknown);
            Assert.AreEqual("~$3.00+", total.CostText());
        }

        [Test]
        public void FormatMoney_KeepsSmallAmountsReadable()
        {
            Assert.AreEqual("12.35", TokenUsage.FormatMoney(12.346));
            Assert.AreEqual("0.042", TokenUsage.FormatMoney(0.042));
            Assert.AreEqual("0.0004", TokenUsage.FormatMoney(0.0004));
        }
    }
}
