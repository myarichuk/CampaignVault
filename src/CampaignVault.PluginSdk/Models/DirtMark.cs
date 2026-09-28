using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// One patch of filth on a host: blood on a character's hands, mud on boots, dust on a sword, soot on a
/// location's north wall. Embedded in <see cref="IHasDirt.Dirt"/> on <see cref="Character"/>, <see cref="Item"/>
/// and <see cref="Location"/> and mutated through the core <c>soil</c> verb (<c>SoilChange</c>).
///
/// <see cref="Kind"/> is an open string, exactly like <c>ItemCategories</c> / <c>EquipZones</c> /
/// <c>StatusEffect.Category</c>: <see cref="DirtKinds"/> lists suggestions, but any string works and unknown kinds
/// simply have no special engine behaviour. Plugins may namespace their own (<c>myplugin.ichor</c>) by convention.
///
/// The identity of a mark is <c>(kind, spot, fixture)</c>, compared case-insensitively.
/// </summary>
public class DirtMark
{
    public const int MinSeverity = 1;
    public const int MaxSeverity = 3;

    /// <summary>Open string, stored trimmed and lower-case: "blood", "mud", "dust", "myplugin.ichor".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = null!;

    /// <summary>1 = light, 2 = moderate, 3 = heavy.</summary>
    [JsonPropertyName("severity")]
    public int Severity { get; set; } = MinSeverity;

    /// <summary>Freeform placement: "boots", "hem of cloak", "left cheek", "blade". Null = the host generally.</summary>
    [JsonPropertyName("spot")]
    public string? Spot { get; set; }

    /// <summary>Locations only: the piece of scenery that is dirty ("north wall", "floor"). Null = the place generally.</summary>
    [JsonPropertyName("fixture")]
    public string? Fixture { get; set; }

    /// <summary>Campaign day this mark was last applied or worsened (lets plugins age or fade it).</summary>
    [JsonPropertyName("appliedDay")]
    public int AppliedDay { get; set; }

    /// <summary>Optional short remark ("from the ogre", "river silt").</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>True when this mark has the given identity (case-insensitive; null and empty spot/fixture are the same).</summary>
    public bool Matches(string kind, string? spot, string? fixture) =>
        string.Equals(Kind, kind, StringComparison.OrdinalIgnoreCase) &&
        SoilHelpers.SameText(Spot, spot) &&
        SoilHelpers.SameText(Fixture, fixture);
}

/// <summary>Anything that can carry <see cref="DirtMark"/>s. Implemented by <see cref="Character"/>, <see cref="Item"/> and <see cref="Location"/>.</summary>
public interface IHasDirt
{
    /// <summary>At most <see cref="SoilHelpers.MaxPerHost"/> marks. Read-only for plugins: mutate through a <c>SoilChange</c>.</summary>
    List<DirtMark> Dirt { get; set; }
}

/// <summary>
/// Suggested dirt kinds, including battle scars a scene keeps (<c>scorch</c> on a wall, <c>notches</c> on a table,
/// <c>debris</c> on the floor). Not a closed set and not validated: any string is a legal kind. Constants are lower-case
/// because kinds are normalised to lower-case on write.
/// </summary>
public static class DirtKinds
{
    public const string Dust = "dust";
    public const string Blood = "blood";
    public const string Mud = "mud";
    public const string Soot = "soot";
    public const string Ash = "ash";
    public const string Grime = "grime";
    public const string Slime = "slime";
    public const string Grease = "grease";
    public const string Sweat = "sweat";
    public const string Ink = "ink";
    public const string Wine = "wine";

    // Marks a scene keeps after something happened there (a decal with a story): a mage duel, a brawl, a wrecked room.
    public const string Scorch = "scorch";
    public const string Notches = "notches";
    public const string Debris = "debris";
}

/// <summary>What a <see cref="SoilHelpers.Apply"/> call did, for messages and the <c>core.soiled.v1</c> event.</summary>
public sealed record SoilOutcome(IReadOnlyList<DirtMark> Changed, string Action, DirtMark? Evicted = null);

/// <summary>
/// Helpers over a host's dirt list, shared by the core handler and plugins. The read helpers (<c>HasDirt</c>,
/// <c>SeverityOf</c>, <see cref="Summarize"/>) are safe anywhere. <see cref="Apply"/> and <see cref="Clear"/> mutate the list
/// in place, so calling them on a tracked entity from <c>IChangeContext</c> would be saved silently, without the verb's
/// validation and without the <c>core.soiled.v1</c> event. To change dirt from a plugin, return a
/// <c>SoilChange</c> (from an <c>IDomainEventHandler</c> or an observer) instead.
/// </summary>
public static class SoilHelpers
{
    /// <summary>Hard cap per host. Adding past it evicts the least significant, oldest mark rather than failing.</summary>
    public const int MaxPerHost = 8;
    public const int MaxKindLength = 40;
    public const int MaxPlacementLength = 60;
    public const int MaxNoteLength = 120;

    /// <summary>Marks shown in a compact summary before "+N more".</summary>
    public const int SummaryMarks = 2;

    /// <summary>True when the host carries at least one dirt mark.</summary>
    public static bool HasDirt(this IHasDirt host) => host.Dirt is { Count: > 0 };

    /// <summary>True when the host carries a mark of this kind (case-insensitive).</summary>
    public static bool HasDirt(this IHasDirt host, string kind) =>
        host.Dirt?.Any(d => string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>Highest severity of the given kind on the host; 0 when none.</summary>
    public static int SeverityOf(this IHasDirt host, string kind) =>
        host.Dirt?.Where(d => string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Severity).DefaultIfEmpty(0).Max() ?? 0;

    internal static bool SameText(string? a, string? b) =>
        string.Equals(Clean(a) ?? "", Clean(b) ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Trims; null for blank.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Kinds are stored trimmed and lower-case so identity is a plain comparison.</summary>
    public static string? NormalizeKind(string? kind) => Clean(kind)?.ToLowerInvariant();

    /// <summary>
    /// Applies <paramref name="amount"/> (signed) to the mark with identity (kind, spot, fixture).
    /// Positive: adds the mark at that severity or worsens the existing one (clamped to 3), evicting when the host is full.
    /// Negative: cleans. <paramref name="spot"/> and <paramref name="fixture"/> are then filters (null = any), so a wash
    /// with no spot lightens that kind everywhere; marks that reach 0 are removed.
    /// </summary>
    public static SoilOutcome Apply(
        List<DirtMark> dirt, string kind, string? spot, string? fixture, int amount, int day, string? note = null)
    {
        kind = NormalizeKind(kind) ?? throw new ArgumentException("kind is required.", nameof(kind));
        spot = Clean(spot);
        fixture = Clean(fixture);
        note = Clean(note) is { } n ? Truncate(n, MaxNoteLength) : null;

        if (amount == 0)
        {
            return new SoilOutcome([], "none");
        }

        if (amount < 0)
        {
            var hits = dirt.Where(d => string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                                       (spot is null || SameText(d.Spot, spot)) &&
                                       (fixture is null || SameText(d.Fixture, fixture))).ToList();
            foreach (var hit in hits)
            {
                hit.Severity = Math.Max(0, hit.Severity + amount);
            }

            dirt.RemoveAll(d => d.Severity < DirtMark.MinSeverity);
            return new SoilOutcome(hits, hits.Count == 0 ? "none" : "cleaned");
        }

        var existing = dirt.FirstOrDefault(d => d.Matches(kind, spot, fixture));
        if (existing is not null)
        {
            existing.Severity = Math.Min(DirtMark.MaxSeverity, existing.Severity + amount);
            existing.AppliedDay = day;
            existing.Note = note ?? existing.Note;
            return new SoilOutcome([existing], "worsened");
        }

        DirtMark? evicted = null;
        if (dirt.Count >= MaxPerHost)
        {
            evicted = dirt.OrderBy(d => d.Severity).ThenBy(d => d.AppliedDay).First();
            dirt.Remove(evicted);
        }

        var mark = new DirtMark
        {
            Kind = kind,
            Severity = Math.Clamp(amount, DirtMark.MinSeverity, DirtMark.MaxSeverity),
            Spot = spot is null ? null : Truncate(spot, MaxPlacementLength),
            Fixture = fixture is null ? null : Truncate(fixture, MaxPlacementLength),
            AppliedDay = day,
            Note = note,
        };
        dirt.Add(mark);
        return new SoilOutcome([mark], "applied", evicted);
    }

    /// <summary>
    /// Removes every mark matching the filters (null = any) — <c>clear</c>. With no filters this empties the host.
    /// Returns the removed marks.
    /// </summary>
    public static IReadOnlyList<DirtMark> Clear(List<DirtMark> dirt, string? kind, string? spot, string? fixture)
    {
        kind = NormalizeKind(kind);
        spot = Clean(spot);
        fixture = Clean(fixture);
        var removed = dirt.Where(d => (kind is null || string.Equals(d.Kind, kind, StringComparison.OrdinalIgnoreCase)) &&
                                      (spot is null || SameText(d.Spot, spot)) &&
                                      (fixture is null || SameText(d.Fixture, fixture))).ToList();
        dirt.RemoveAll(removed.Contains);
        return removed;
    }

    /// <summary>
    /// Compact wire form: the <see cref="SummaryMarks"/> heaviest marks as short phrases ("muddy boots, heavily bloodied"),
    /// plus "+N more". Null when there is no dirt, so an empty host adds nothing to a payload.
    /// </summary>
    public static string? Summarize(IReadOnlyList<DirtMark>? dirt)
    {
        if (dirt is not { Count: > 0 })
        {
            return null;
        }

        var ranked = dirt.OrderByDescending(d => d.Severity).ThenByDescending(d => d.AppliedDay).ToList();
        var parts = ranked.Take(SummaryMarks).Select(Phrase).ToList();
        if (ranked.Count > SummaryMarks)
        {
            parts.Add($"+{ranked.Count - SummaryMarks} more");
        }

        return string.Join(", ", parts);
    }

    /// <summary>"heavily bloodied", "muddy boots", "slightly dusty north wall", "myplugin.ichor-stained hem".</summary>
    public static string Phrase(DirtMark d)
    {
        var place = d.Fixture ?? d.Spot;
        var adjective = (d.Kind, place) switch
        {
            (DirtKinds.Blood, null) => "bloodied",
            (DirtKinds.Blood, _) => "bloody",
            (DirtKinds.Mud, _) => "muddy",
            (DirtKinds.Dust, _) => "dusty",
            (DirtKinds.Soot, _) => "sooty",
            (DirtKinds.Ash, _) => "ashy",
            (DirtKinds.Grime, _) => "grimy",
            (DirtKinds.Slime, _) => "slimy",
            (DirtKinds.Grease, _) => "greasy",
            (DirtKinds.Sweat, _) => "sweaty",
            (DirtKinds.Ink, _) => "ink-stained",
            (DirtKinds.Wine, _) => "wine-stained",
            (DirtKinds.Scorch, _) => "scorched",
            (DirtKinds.Notches, _) => "notched",
            (DirtKinds.Debris, _) => "littered",
            _ => $"{d.Kind}-stained",
        };
        var degree = d.Severity >= DirtMark.MaxSeverity ? "heavily " : d.Severity <= DirtMark.MinSeverity ? "slightly " : "";
        var phrase = degree + adjective;
        return place is null ? phrase : $"{phrase} {place}";
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
