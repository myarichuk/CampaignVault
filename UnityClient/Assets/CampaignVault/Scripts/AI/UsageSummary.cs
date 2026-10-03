using System.Collections.Generic;
using System.Text;

namespace CampaignVault.UnityClient.AI
{
    /// <summary>The words for the cost counter: the top-bar chip and its breakdown tooltip.</summary>
    public static class UsageSummary
    {
        /// <summary>"session ~$0.08 · campaign ~$1.92"; empty until some call has reported usage.</summary>
        public static string Chip(TokenUsage session, TokenUsage campaign)
        {
            bool hasSession = session != null && !session.IsEmpty;
            bool hasCampaign = campaign != null && !campaign.IsEmpty;
            if (!hasSession && !hasCampaign) { return string.Empty; }
            var sb = new StringBuilder();
            sb.Append("session ").Append(hasSession ? Short(session) : "$0");
            if (hasCampaign) { sb.Append(" · campaign ").Append(Short(campaign)); }
            return sb.ToString();
        }

        private static string Short(TokenUsage usage)
        {
            string text = usage.CostText();
            return text.Length > 0 ? text : "—";
        }

        public static string Tooltip(TokenUsage session, TokenUsage campaign, List<KeyValuePair<string, TokenUsage>> perModel, string pricesAsOf)
        {
            var sb = new StringBuilder();
            Section(sb, "This session", session);
            if (campaign != null && !campaign.IsEmpty)
            {
                sb.Append('\n');
                Section(sb, "This campaign (all sessions)", campaign);
                if (perModel != null && perModel.Count > 1)
                {
                    foreach (KeyValuePair<string, TokenUsage> row in perModel)
                    {
                        sb.Append("\n  ").Append(row.Key).Append(": ").Append(Short(row.Value)).Append(" · ").Append(row.Value.Calls).Append(" calls");
                    }
                }
            }
            sb.Append("\n\n~ = estimated from token counts");
            if (!string.IsNullOrEmpty(pricesAsOf)) { sb.Append(" and bundled prices as of ").Append(pricesAsOf); }
            sb.Append(". With prompt caching an estimate can run a little low. Exact when the provider reports its own cost. Prices can be overridden per provider in Settings.");
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, TokenUsage usage)
        {
            sb.Append(title).Append(": ");
            if (usage == null || usage.IsEmpty) { sb.Append("nothing yet"); return; }
            sb.Append(Short(usage)).Append('\n').Append("  ").Append(usage).Append(" · ").Append(usage.Calls).Append(" calls");
        }
    }
}
