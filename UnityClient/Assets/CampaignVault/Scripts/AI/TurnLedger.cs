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
        public int Prompt;
        public int Completion;
        public int Cached;
        /// <summary>Model calls that reported usage.</summary>
        public int Calls;

        public bool IsEmpty { get { return Calls == 0; } }

        public static TokenUsage FromJson(JsonValue usage)
        {
            var u = new TokenUsage();
            if (usage == null || usage.Kind != JsonKind.Object) { return u; }
            u.Prompt = (int)usage.GetNumber("prompt_tokens", usage.GetNumber("input_tokens", 0));
            u.Completion = (int)usage.GetNumber("completion_tokens", usage.GetNumber("output_tokens", 0));
            u.Cached = (int)usage.Get("prompt_tokens_details").GetNumber("cached_tokens",
                usage.GetNumber("cache_read_input_tokens", 0));
            u.Calls = 1;
            return u;
        }

        public void Add(TokenUsage other)
        {
            if (other == null) { return; }
            Prompt += other.Prompt;
            Completion += other.Completion;
            Cached += other.Cached;
            Calls += other.Calls;
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
            sb.Append("\n- Session usage: ").Append(session != null ? session.ToString() : "none").Append("\n");
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
