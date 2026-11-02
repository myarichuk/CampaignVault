using CampaignVault.Models;

namespace CampaignVault.Data.Impressions;

/// <summary>Which way a tone pulls and what kind of thing it is, for the appraisal (IMPRESSIONS_PLAN.md I5).</summary>
public sealed record Tone(
    string Name,
    int Valence,
    string Mood,
    bool Wonder,
    bool Fright,
    string Faint,
    string Mild,
    string Strong,
    string Overwhelming)
{
    public string Responds(ImpressionTier tier) => tier switch
    {
        ImpressionTier.Faint => Faint,
        ImpressionTier.Mild => Mild,
        ImpressionTier.Strong => Strong,
        _ => Overwhelming,
    };
}

/// <summary>The closed list of tones. Fourteen the narrator may name, three the engine reaches on its own (scorn, suspicion, unease).</summary>
public static class Tones
{
    private static readonly Dictionary<string, Tone> All = new Tone[]
    {
        new("delight", 1, "delighted", true, false, "smiles", "lingers to watch", "draws closer, enchanted", "is utterly enchanted and won't leave"),
        new("awe", 1, "awed", true, false, "glances twice", "stares", "stands spellbound", "is struck speechless"),
        new("amusement", 1, "amused", true, false, "smirks", "laughs", "laughs aloud", "is helpless with laughter"),
        new("affection", 1, "touched", false, false, "smiles", "thanks them", "warms to them", "is deeply moved and returns the kindness"),
        new("gratitude", 1, "grateful", false, false, "nods", "thanks them", "thanks them warmly", "is moved to repay it"),
        new("comfort", 1, "reassured", false, false, "relaxes a little", "relaxes", "is put at ease", "is wholly at peace"),
        new("pride", 1, "flattered", false, false, "preens", "warms to them", "is won over", "is utterly charmed"),
        new("trust", 1, "convinced", false, false, "half-believes it", "believes it", "is convinced", "would swear to it"),
        new("sorrow", 0, "moved", false, false, "falls quiet", "is moved", "is close to tears", "weeps"),
        new("fear", -1, "frightened", false, true, "flinches but holds", "backs away", "retreats", "flees"),
        new("menace", -1, "intimidated", false, true, "stiffens", "hesitates", "gives way", "cowers or flees"),
        new("disgust", -1, "disgusted", false, false, "grimaces", "recoils", "turns away", "retches and leaves"),
        new("anger", -1, "angry", false, false, "frowns", "bristles", "confronts them", "lashes out"),
        new("insult", -1, "insulted", false, false, "stiffens", "takes offence", "confronts them", "won't forgive it"),
        new("scorn", -1, "contemptuous", false, false, "shrugs it off", "scoffs", "laughs it off", "mocks it openly"),
        new("suspicion", -1, "suspicious", false, false, "narrows their eyes", "grows wary", "grows hostile", "is sure they're being played"),
        new("unease", -1, "uneasy", false, false, "eyes it warily", "edges away", "makes a warding sign and backs off", "flees from it"),
    }.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Tones the narrator may name in <c>appeal</c> or <c>reception</c>.</summary>
    public static readonly IReadOnlyList<string> Named =
        ["delight", "awe", "amusement", "affection", "gratitude", "comfort", "pride", "trust", "sorrow", "fear", "menace", "disgust", "anger", "insult"];

    public static bool TryGet(string? name, out Tone tone)
    {
        tone = null!;
        if (string.IsNullOrWhiteSpace(name) || !All.TryGetValue(name.Trim(), out var found))
        {
            return false;
        }

        tone = found;
        return true;
    }

    public static Tone Get(string name) => All[name];

    /// <summary>
    /// A deception seen through turns against its author: praise or a lie reads as manipulation (insult), a fright or a threat
    /// as a cheap trick (scorn), anything else as being played (suspicion). Wonder survives: butterflies are still pretty once
    /// you know they're an illusion, a tier less so.
    /// </summary>
    public static Tone SeenThrough(Tone meant) => meant.Name switch
    {
        "pride" or "trust" or "affection" or "gratitude" or "comfort" => All["insult"],
        "fear" or "menace" or "disgust" or "anger" or "insult" => All["scorn"],
        _ when meant.Wonder => meant,
        _ => All["suspicion"],
    };
}

/// <summary>What a beat is meant to do to the people it reaches (IMPRESSIONS_PLAN.md I4).</summary>
public sealed record Appeal(
    Tone Tone,
    ImpressionTier Intensity,
    bool Deceptive,
    string? Subject,
    IReadOnlyDictionary<string, string> Reception,
    bool Inferred)
{
    private static readonly Dictionary<string, string> NoReception = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The appeal a change carries, or null. <paramref name="error"/> says what's wrong with one that doesn't parse.</summary>
    public static Appeal? Of(WorldChange change, out string? error)
    {
        error = null;
        switch (change)
        {
            case RulesetAction action:
                return OfAction(action, out error);
            case EventOccurred ev when !string.IsNullOrWhiteSpace(ev.Appeal):
                return Build(ev.Appeal, ev.AppealIntensity, ev.Deceptive == true, ev.AppealSubject, ev.Reception, inferred: false, out error);
            case EventOccurred ev when ev.Reception is { Count: > 0 } || ev.Deceptive == true || ev.AppealIntensity != null:
                error = "appealIntensity, deceptive and reception need an appeal (the tone it's meant to strike).";
                return null;
            default:
                return null;
        }
    }

    public static Appeal? Of(WorldChange change) => Of(change, out _);

    private static Appeal? OfAction(RulesetAction action, out string? error)
    {
        error = null;
        var p = action.Parameters;
        var named = p.GetValueOrDefault("appeal");
        var deceptive = string.Equals(p.GetValueOrDefault("deceptive")?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var reception = ParseReception(p.GetValueOrDefault("reception"), out error);
        if (error != null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(named))
        {
            return Build(named, IntensityParam(p), deceptive, SubjectParam(p), reception, inferred: false, out error);
        }

        // D2: three checks say what they're for on their own.
        if (action.ActionType is RulesetActionType.SkillCheck or RulesetActionType.ContestedCheck)
        {
            // The skill itself, by its full name: "Persuade the lieutenant" or "Perform first aid" is no lie and no show.
            var skill = (p.GetValueOrDefault("skill") ?? action.ActionName)?.Trim().ToLowerInvariant();
            var inferred = skill switch
            {
                "intimidation" => "menace",
                "deception" => "trust",
                "performance" => "delight",
                _ => null,
            };
            if (inferred != null)
            {
                return Build(inferred, IntensityParam(p), deceptive || inferred == "trust", SubjectParam(p),
                    reception, inferred: true, out error);
            }
        }

        if (reception.Count > 0 || deceptive || IntensityParam(p) != null)
        {
            error = "intensity, deceptive and reception need parameters.appeal (the tone it's meant to strike).";
        }

        return null;
    }

    /// <summary>The event's field names work on an action too, so one spelling never silently drops.</summary>
    private static string? IntensityParam(Dictionary<string, string> p) => p.GetValueOrDefault("intensity") ?? p.GetValueOrDefault("appealIntensity");

    private static string? SubjectParam(Dictionary<string, string> p) => p.GetValueOrDefault("subject") ?? p.GetValueOrDefault("appealSubject");

    private static Appeal? Build(string tone, string? intensity, bool deceptive, string? subject, IReadOnlyDictionary<string, string>? reception,
        bool inferred, out string? error)
    {
        error = null;
        if (!Tones.TryGet(tone, out var t) || !Tones.Named.Contains(t.Name))
        {
            error = $"appeal '{tone}' isn't a tone. Use one of: {string.Join(", ", Tones.Named)}.";
            return null;
        }

        if (!TryIntensity(intensity, out var tier))
        {
            error = $"intensity '{intensity}' isn't mild, strong or overwhelming.";
            return null;
        }

        foreach (var (id, toneName) in reception ?? NoReception)
        {
            if (!Tones.TryGet(toneName, out var r) || !Tones.Named.Contains(r.Name))
            {
                error = $"reception for {id}: '{toneName}' isn't a tone. Use one of: {string.Join(", ", Tones.Named)}.";
                return null;
            }
        }

        var normalized = new Dictionary<string, string>(reception ?? NoReception, StringComparer.OrdinalIgnoreCase);
        return new Appeal(t, tier, deceptive, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(), normalized, inferred);
    }

    private static bool TryIntensity(string? value, out ImpressionTier tier)
    {
        tier = ImpressionTier.Mild;
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "mild":
                return true;
            case "strong":
                tier = ImpressionTier.Strong;
                return true;
            case "overwhelming":
                tier = ImpressionTier.Overwhelming;
                return true;
            default:
                return false;
        }
    }

    /// <summary>"chars/a:disgust, chars/b=delight" (comma or semicolon between, colon or equals inside).</summary>
    public static IReadOnlyDictionary<string, string> ParseReception(string? raw, out string? error)
    {
        error = null;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var pair in raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = pair.LastIndexOfAny([':', '=']);
            if (cut <= 0 || cut == pair.Length - 1)
            {
                error = $"reception entry '{pair}' isn't 'characterId:tone'.";
                return result;
            }

            result[pair[..cut].Trim()] = pair[(cut + 1)..].Trim();
        }

        return result;
    }
}
