using System;
using System.Collections.Generic;
using System.Text;
using CampaignVault.UnityClient.Json;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>
    /// Token counts as providers report them in the OpenAI-style "usage"
    /// object. Cached prompt tokens come from prompt_tokens_details (OpenAI,
    /// OpenRouter) or cache_read_input_tokens (Anthropic-style gateways).
    /// Missing fields stay 0: not every endpoint reports usage.
    /// </summary>
    public sealed class TokenUsage
    {
        /// <summary>Prompt tokens as the provider reported them: OpenAI-style counts include cached ones, Anthropic-style don't.</summary>
        public int Prompt;
        public int Completion;
        public int Cached;
        /// <summary>Prompt tokens written to a provider cache (Anthropic-style); billed above the plain input rate.</summary>
        public int CacheWrite;
        /// <summary>Model calls that reported usage.</summary>
        public int Calls;

        /// <summary>Dollars across the calls that could be priced.</summary>
        public double Cost;
        /// <summary>The provider's own cost figure for this call (OpenRouter usage.cost); negative when it sent none.</summary>
        public double ReportedCost = -1;
        /// <summary>Calls priced exactly (provider-reported), by table or override estimate, free (local), or not at all.</summary>
        public int ExactCalls;
        public int EstimatedCalls;
        public int FreeCalls;
        public int UnpricedCalls;

        /// <summary>This call's usage was Anthropic-shaped: cache reads and writes are not part of <see cref="Prompt"/>. Only meaningful per call, for pricing.</summary>
        private bool _cacheOutsidePrompt;

        public bool IsEmpty { get { return Calls == 0; } }

        public static TokenUsage FromJson(JsonValue usage)
        {
            var u = new TokenUsage();
            if (usage == null || usage.Kind != JsonKind.Object) { return u; }
            bool openAiStyle = usage.Get("prompt_tokens").Kind == JsonKind.Number;
            u.Cached = (int)usage.Get("prompt_tokens_details").GetNumber("cached_tokens",
                usage.GetNumber("cache_read_input_tokens", 0));
            u.CacheWrite = (int)usage.Get("prompt_tokens_details").GetNumber("cache_write_tokens",
                usage.GetNumber("cache_creation_input_tokens", 0));
            u.Prompt = (int)usage.GetNumber("prompt_tokens", usage.GetNumber("input_tokens", 0));
            // Anthropic's own shape counts cache reads and writes outside input_tokens; OpenAI's counts them inside.
            u._cacheOutsidePrompt = !openAiStyle;
            u.Completion = (int)usage.GetNumber("completion_tokens", usage.GetNumber("output_tokens", 0));
            u.ReportedCost = usage.GetNumber("cost", -1);
            u.Calls = 1;
            return u;
        }

        /// <summary>
        /// Prices one call's usage: the provider's own cost when it sent one, free for
        /// local servers and ":free" models, else tokens × the profile's override or
        /// the bundled table. A model with no known price stays unpriced.
        /// </summary>
        public void ApplyPrice(ModelPricing pricing, ProviderProfile profile, string model)
        {
            if (Calls == 0) { return; }
            if (ReportedCost >= 0) { Cost = ReportedCost; ExactCalls = 1; return; }
            if (ModelPricing.IsLocal(profile) || ModelPricing.IsFreeVariant(model)) { FreeCalls = 1; return; }
            ModelRate rate = ModelPricing.OverrideFor(profile) ?? (pricing != null ? pricing.Find(model) : null);
            if (rate == null) { UnpricedCalls = 1; return; }
            int plain = _cacheOutsidePrompt ? Prompt : Math.Max(0, Prompt - Cached - CacheWrite);
            Cost = (plain * rate.Input + Cached * rate.CachedInput + CacheWrite * rate.CacheWrite + Completion * rate.Output) / 1000000.0;
            EstimatedCalls = 1;
        }

        public void Add(TokenUsage other)
        {
            if (other == null) { return; }
            Prompt += other.Prompt;
            Completion += other.Completion;
            Cached += other.Cached;
            CacheWrite += other.CacheWrite;
            Calls += other.Calls;
            Cost += other.Cost;
            ExactCalls += other.ExactCalls;
            EstimatedCalls += other.EstimatedCalls;
            FreeCalls += other.FreeCalls;
            UnpricedCalls += other.UnpricedCalls;
        }

        /// <summary>True when at least one call has a dollar figure worth showing.</summary>
        public bool HasCost { get { return ExactCalls + EstimatedCalls > 0; } }

        /// <summary>
        /// "$0.42" when every priced call was reported by the provider, "~$0.42" when any was
        /// estimated, a trailing "+" when some calls couldn't be priced, "local · free" for
        /// local servers, "price unknown" when nothing could be priced, empty with no usage.
        /// </summary>
        public string CostText()
        {
            if (IsEmpty) { return string.Empty; }
            if (!HasCost)
            {
                if (UnpricedCalls > 0) { return "price unknown"; }
                return FreeCalls > 0 ? "local · free" : string.Empty;
            }
            string text = (EstimatedCalls > 0 ? "~$" : "$") + FormatMoney(Cost);
            return UnpricedCalls > 0 ? text + "+" : text;
        }

        public static string FormatMoney(double dollars)
        {
            string format = dollars >= 0.1 ? "0.00" : dollars >= 0.01 ? "0.000" : "0.0000";
            return dollars.ToString(format, System.Globalization.CultureInfo.InvariantCulture);
        }

        public override string ToString()
        {
            if (IsEmpty) { return "no usage reported"; }
            return Prompt + " in (" + Cached + " cached) · " + Completion + " out · " + Calls + " call" + (Calls == 1 ? string.Empty : "s");
        }
    }

    /// <summary>One player message and what came back, kept for measurement and export.</summary>
    public sealed class TurnRecord
    {
        public string Player = string.Empty;
        public string Model = string.Empty;
        public DateTime StartedUtc = DateTime.UtcNow;
        /// <summary>The story text exactly as the model wrote it, before voice splitting.</summary>
        public readonly StringBuilder Narration = new StringBuilder();
        /// <summary>Roll lines as the transcript shows them ("Perception 17 vs DC 14: Success").</summary>
        public readonly List<string> Rolls = new List<string>();
        public readonly List<string> Tools = new List<string>();
        public readonly TokenUsage Usage = new TokenUsage();

        public void AddNarration(string text)
        {
            if (string.IsNullOrEmpty(text)) { return; }
            if (Narration.Length > 0) { Narration.Append("\n\n"); }
            Narration.Append(text);
        }
    }

    /// <summary>
    /// Markdown export of the turn ledger. Stable, simple shape so
    /// scripts/measure/prose_stats.py can parse it: one "## Turn N" section
    /// per player message with "### Player", "### Rolls" and "### DM" blocks
    /// and a usage comment line.
    /// </summary>
    public static class TranscriptExport
    {
        public const string Format = "campaignvault-transcript/1";

        public static string ToMarkdown(string campaign, string ruleset, IList<TurnRecord> turns, TokenUsage session)
        {
            var sb = new StringBuilder();
            sb.Append("<!-- format: ").Append(Format).Append(" -->\n");
            sb.Append("# Transcript: ").Append(string.IsNullOrEmpty(campaign) ? "(no campaign)" : campaign).Append('\n');
            sb.Append("\n- Ruleset: ").Append(string.IsNullOrEmpty(ruleset) ? "?" : ruleset);
            sb.Append("\n- Exported: ").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")).Append(" UTC");
            sb.Append("\n- Turns: ").Append(turns.Count);
            sb.Append("\n- Session usage: ").Append(session != null ? session.ToString() : "none");
            if (session != null && session.CostText().Length > 0) { sb.Append("\n- Session cost: ").Append(session.CostText()); }
            sb.Append("\n");
            for (int i = 0; i < turns.Count; i++)
            {
                var t = turns[i];
                sb.Append("\n## Turn ").Append(i + 1).Append('\n');
                sb.Append("<!-- usage model=").Append(t.Model)
                  .Append(" prompt=").Append(t.Usage.Prompt)
                  .Append(" completion=").Append(t.Usage.Completion)
                  .Append(" cached=").Append(t.Usage.Cached)
                  .Append(" calls=").Append(t.Usage.Calls)
                  .Append(" tools=").Append(t.Tools.Count).Append(" -->\n");
                sb.Append("\n### Player\n\n").Append(t.Player.Trim()).Append('\n');
                if (t.Rolls.Count > 0)
                {
                    sb.Append("\n### Rolls\n\n");
                    foreach (string r in t.Rolls) { sb.Append("- ").Append(r).Append('\n'); }
                }
                sb.Append("\n### DM\n\n").Append(t.Narration.Length > 0 ? t.Narration.ToString().Trim() : "(no narration)").Append('\n');
            }
            return sb.ToString();
        }
    }
}
