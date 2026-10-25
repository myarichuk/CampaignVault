using CampaignVault.Models;

namespace CampaignVault.Rulesets;

/// <summary>
/// What range does to an attack roll, stamped on the action by the handler after <see cref="RangeValidationHelper"/> ran and
/// folded into the roll by the resolver's pipeline: 5e long range or a hostile in reach is one disadvantage source; PF2e long
/// range is a flat penalty per increment.
/// </summary>
internal readonly record struct RangeRollModifier(int Penalty, bool Disadvantage, IReadOnlyList<string> Notes)
{
    public const string DisadvantageParameter = "rangeDisadvantage";
    public const string PenaltyParameter = "rangePenalty";

    public static RangeRollModifier None { get; } = new(0, false, []);

    public static RangeRollModifier Read(RulesetAction action)
    {
        var notes = new List<string>();
        var disadvantage = false;
        var penalty = 0;
        if (action.Parameters.TryGetValue(DisadvantageParameter, out var why) && !string.IsNullOrWhiteSpace(why))
        {
            disadvantage = true;
            notes.Add($"range: disadvantage ({why})");
        }

        if (action.Parameters.TryGetValue(PenaltyParameter, out var raw) && (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) || int.TryParse(raw, out p)) && p != 0)
        {
            penalty = p;
            notes.Add($"range penalty {p:+#;-#}");
        }

        return notes.Count == 0 ? None : new RangeRollModifier(penalty, disadvantage, notes);
    }
}
