using System.Text;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>
/// start_session fallback when no handoff exists (a campaign from before end_session took a handoff, or a
/// client that never called end_session): the latest legacy recap plus the most important recent events,
/// hard-capped so the kickoff never regresses to the unbounded dump.
/// </summary>
public static class SessionDigestBuilder
{
    public const int MaxChars = 1500;
    public const int MaxRecapChars = 600;
    public const int MaxEventChars = 160;

    private const string EventsHeader = "Recent events:\n";

    /// <param name="rankedEvents">Events already ranked by importance then recency
    /// (CampaignRepository.SelectRecentEventsAsync order).</param>
    public static string? Build(int? recapSession, string? recapText, IEnumerable<EventSummaryView> rankedEvents)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(recapText))
        {
            sb.Append($"Session {recapSession} recap: ").Append(Clip(recapText.Trim(), MaxRecapChars)).Append('\n');
        }

        var picked = new List<EventSummaryView>();
        var budget = MaxChars - sb.Length - EventsHeader.Length;
        foreach (var ev in rankedEvents)
        {
            if (string.IsNullOrWhiteSpace(ev.Summary))
            {
                continue;
            }

            var cost = EventLine(ev).Length + 1;
            if (cost > budget)
            {
                break;
            }

            picked.Add(ev);
            budget -= cost;
        }

        if (picked.Count > 0)
        {
            sb.Append(EventsHeader);
            foreach (var ev in picked.OrderBy(e => e.DayLogged))
            {
                sb.Append(EventLine(ev)).Append('\n');
            }
        }

        return sb.Length == 0 ? null : sb.ToString().TrimEnd();
    }

    private static string EventLine(EventSummaryView ev) => $"- day {ev.DayLogged}: {Clip(ev.Summary.Trim(), MaxEventChars)}";

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
