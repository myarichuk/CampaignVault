using System.Globalization;
using CampaignVault.Data.ChangeHandlers;
using CampaignVault.Models;

namespace CampaignVault.Data;

/// <summary>Beat result: how it went and how big. The engine never says what happened; the DM does.</summary>
public sealed record BeatOutcome(string Valence, string Severity);

/// <summary>
/// Pure rules of the consequence beat (SDK 0.10.0): whether time on the road or in camp produces a small good or bad
/// event, and how big. Settings come from the campaign's system options.
/// </summary>
public static class ConsequenceBeats
{
    public const string OptionMode = "consequences";
    public const string OptionCooldownHours = "consequenceCooldownHours";
    public const string OptionMaxPerDay = "consequenceMaxPerDay";

    public const double DefaultGoodCooldownHours = 8;
    public const double DefaultBadCooldownHours = 24;
    public const int DefaultMaxPerDay = 2;

    public const string KeyGoodReadyAt = "consequences.goodReadyAtHour";
    public const string KeyBadReadyAt = "consequences.badReadyAtHour";
    public const string KeyDay = "consequences.day";
    public const string KeyCount = "consequences.countToday";

    public enum Mode { Off, Light, Full }

    public sealed record Settings(Mode Mode, double GoodCooldownHours, double BadCooldownHours, int MaxPerDay);

    public static Settings Read(IReadOnlyDictionary<string, string>? options)
    {
        string? Get(string key) => options?.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        var mode = Get(OptionMode)?.Trim().ToLowerInvariant() switch
        {
            "off" => Mode.Off,
            "full" => Mode.Full,
            _ => Mode.Light,
        };

        // One override sets the good cooldown; bad events keep their 3× longer default relationship.
        var good = double.TryParse(Get(OptionCooldownHours), NumberStyles.Float, CultureInfo.InvariantCulture, out var g) && g >= 0
            ? g : DefaultGoodCooldownHours;
        var bad = good == DefaultGoodCooldownHours ? DefaultBadCooldownHours : good * 3;
        var max = int.TryParse(Get(OptionMaxPerDay), out var m) && m >= 0 ? m : DefaultMaxPerDay;
        return new Settings(mode, good, bad, max);
    }

    public enum TerrainClass { Harsh, Neutral, Safe }

    public static TerrainClass Classify(string? terrain)
    {
        var t = terrain?.ToLowerInvariant() ?? "";
        if (new[] { "mountain", "tundra", "ice", "glacier", "snow", "arctic", "swamp", "marsh", "desert", "jungle", "volcan", "badland", "cliff", "wasteland" }
            .Any(t.Contains))
            return TerrainClass.Harsh;
        if (new[] { "road", "town", "city", "village", "farm", "street", "settled", "urban", "inn" }.Any(t.Contains))
            return TerrainClass.Safe;
        return TerrainClass.Neutral;
    }

    /// <summary>Chance of a beat per 6 hours before source and mode scaling.</summary>
    public static double BaseChancePerSixHours(TerrainClass terrain) => terrain switch
    {
        TerrainClass.Harsh => 0.18,
        TerrainClass.Safe => 0.05,
        _ => 0.10,
    };

    public static double Chance(Settings s, TimeAdvance advance)
    {
        if (s.Mode == Mode.Off || advance.Hours <= 0)
            return 0;
        var perSix = BaseChancePerSixHours(Classify(advance.Terrain));
        var scale = advance.Source switch { "rest" => 0.5, "advance_world" => 0.5, _ => 1.0 };
        if (s.Mode == Mode.Light)
            scale *= 0.6;
        return Math.Clamp(perSix * scale * (advance.Hours / 6.0), 0, 0.9);
    }

    /// <summary>Two independent rolls in [0,1): the first picks valence, the second severity.</summary>
    public static BeatOutcome Decide(Settings s, TerrainClass terrain, double valenceRoll, double severityRoll)
    {
        var (bad, good) = terrain switch
        {
            TerrainClass.Harsh => (0.65, 0.20),
            TerrainClass.Safe => (0.25, 0.55),
            _ => (0.40, 0.40),
        };
        var valence = valenceRoll < bad ? "bad" : valenceRoll < bad + good ? "good" : "mixed";

        var severity = s.Mode == Mode.Full
            ? severityRoll < 0.55 ? "light" : severityRoll < 0.88 ? "moderate" : "serious"
            : severityRoll < 0.75 ? "light" : "moderate";
        // A serious windfall is not a small road event; keep good news modest.
        if (valence == "good" && severity == "serious")
            severity = "moderate";
        return new BeatOutcome(valence, severity);
    }

    /// <summary>Whether cooldown and the daily cap let this outcome through for a character right now.</summary>
    public static bool Allowed(Settings s, Character character, BeatOutcome outcome, double nowHours)
    {
        if (s.MaxPerDay <= 0)
            return false;
        var traits = character.SystemStats?.Traits;
        var day = (int)Math.Floor(nowHours / 24);
        var count = Num(traits, KeyDay) == day ? (int)Num(traits, KeyCount) : 0;
        if (count >= s.MaxPerDay)
            return false;
        var goodBlocked = nowHours < Num(traits, KeyGoodReadyAt);
        var badBlocked = nowHours < Num(traits, KeyBadReadyAt);
        return outcome.Valence switch
        {
            "good" => !goodBlocked,
            "bad" => !badBlocked,
            _ => !goodBlocked && !badBlocked,
        };
    }

    public static void Record(Settings s, Character character, BeatOutcome outcome, double nowHours)
    {
        character.SystemStats ??= new SystemExtension();
        var traits = character.SystemStats.Traits;
        var day = (int)Math.Floor(nowHours / 24);
        var count = Num(traits, KeyDay) == day ? (int)Num(traits, KeyCount) : 0;
        traits[KeyDay] = day.ToString(CultureInfo.InvariantCulture);
        traits[KeyCount] = (count + 1).ToString(CultureInfo.InvariantCulture);
        if (outcome.Valence is "good" or "mixed")
            traits[KeyGoodReadyAt] = (nowHours + s.GoodCooldownHours).ToString(CultureInfo.InvariantCulture);
        if (outcome.Valence is "bad" or "mixed")
            traits[KeyBadReadyAt] = (nowHours + s.BadCooldownHours).ToString(CultureInfo.InvariantCulture);
    }

    private static double Num(Dictionary<string, string>? traits, string key) =>
        traits is not null && traits.TryGetValue(key, out var v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : key == KeyDay ? -1 : 0;

    public static string Hint(Character character, BeatOutcome outcome, TimeAdvance advance)
    {
        var tier = outcome.Severity;
        var where = string.IsNullOrWhiteSpace(advance.Terrain) ? "" : $" ({advance.Terrain})";
        var effectHint = outcome.Valence switch
        {
            "good" => $"a small boon (apply_effect valence=buff, tier {tier})",
            "bad" => $"a mishap or hardship (apply_effect valence=debuff, tier {tier})",
            _ => $"a mixed turn: a cost and a silver lining (apply_effect, tier {tier})",
        };
        return $"CONSEQUENCE BEAT for {character.Name}{where}: {outcome.Valence}, {outcome.Severity}. " +
               $"Invent something that fits the place and moment (no creatures, that is the encounter roll); it is {effectHint} " +
               "if it should carry a mechanical effect, or pure colour if it should not. Do not stack it on top of a current one.";
    }
}
