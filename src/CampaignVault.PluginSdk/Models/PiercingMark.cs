using System.Text.Json.Serialization;

namespace CampaignVault.Models;

/// <summary>
/// One body piercing on a character: an earlobe stud, a septum ring, a navel barbell.
/// Embedded in <see cref="Character.Piercings"/> and mutated through the core <c>piercing</c> verb
/// (<see cref="PiercingChange"/>).
///
/// <see cref="Site"/> and <see cref="Kind"/> are open strings (like dirt kinds):
/// <see cref="PiercingSites"/> / <see cref="PiercingKinds"/> list SFW suggestions; plugins may use any
/// site or namespace kinds (<c>lewd.nipple_ring</c>, <c>goblins.heavy_nose_ring</c>).
/// Several marks may share the same site (and even the same kind) — e.g. three rings on
/// <c>labia.left</c> plus one on <c>clitoris</c>. Each mark has a stable <see cref="Id"/> for
/// update/remove when stacks share site+kind. Pass <c>replace:true</c> on add to upsert a
/// single (site, kind) piece (septum, one slave ring).
/// </summary>
public class PiercingMark
{
    /// <summary>
    /// Stable id on this character (short numeric string by default). Required to target one mark
    /// when several share site+kind.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Open string, stored trimmed and lower-case: "ear.lobe.left", "nose.septum", "navel".</summary>
    [JsonPropertyName("site")]
    public string Site { get; set; } = null!;

    /// <summary>Open string, stored trimmed and lower-case: "stud", "hoop", "ring", "lewd.nipple_ring".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = null!;

    /// <summary>Optional material: "gold", "silver", "iron", "bone".</summary>
    [JsonPropertyName("material")]
    public string? Material { get; set; }

    /// <summary>none | light | heavy — hanging weights/charms. Narrative + plugin hint; not damage.</summary>
    [JsonPropertyName("load")]
    public string Load { get; set; } = PiercingLoads.None;

    /// <summary>Open tags: bell, charm, leash_ring, locked, fresh, …</summary>
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    /// <summary>Campaign day this piercing was last added or updated.</summary>
    [JsonPropertyName("appliedDay")]
    public int AppliedDay { get; set; }

    /// <summary>Optional short remark.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Who/what applied it (character id, verb id, pluginId:cause). Audit only.</summary>
    [JsonPropertyName("appliedBy")]
    public string? AppliedBy { get; set; }

    public bool Matches(string site, string kind) =>
        string.Equals(Site, site, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Kind, kind, StringComparison.OrdinalIgnoreCase);

    public bool MatchesId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        string.Equals(Id, id.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool HasTag(string tag) =>
        Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Suggested SFW piercing sites. Not validated — any string is legal.</summary>
public static class PiercingSites
{
    public const string EarLobeLeft = "ear.lobe.left";
    public const string EarLobeRight = "ear.lobe.right";
    public const string EarHelixLeft = "ear.helix.left";
    public const string EarHelixRight = "ear.helix.right";
    public const string EarTragusLeft = "ear.tragus.left";
    public const string EarTragusRight = "ear.tragus.right";
    public const string NoseNostrilLeft = "nose.nostril.left";
    public const string NoseNostrilRight = "nose.nostril.right";
    public const string NoseSeptum = "nose.septum";
    public const string LipLabret = "lip.labret";
    public const string LipMonroe = "lip.monroe";
    public const string EyebrowLeft = "eyebrow.left";
    public const string EyebrowRight = "eyebrow.right";
    public const string Navel = "navel";
    public const string Tongue = "tongue";
}

/// <summary>Suggested piercing kinds. Not validated — plugins may namespace.</summary>
public static class PiercingKinds
{
    public const string Stud = "stud";
    public const string Hoop = "hoop";
    public const string Ring = "ring";
    public const string Barbell = "barbell";
    public const string CaptiveBead = "captive_bead";
    public const string Chain = "chain";
}

/// <summary>Load values for hanging weight/charms.</summary>
public static class PiercingLoads
{
    public const string None = "none";
    public const string Light = "light";
    public const string Heavy = "heavy";
}

/// <summary>Common piercing tags.</summary>
public static class PiercingTags
{
    public const string Bell = "bell";
    public const string Charm = "charm";
    public const string LeashRing = "leash_ring";
    public const string Locked = "locked";
    public const string Fresh = "fresh";
}

/// <summary>Result of a piercing helper mutation.</summary>
public sealed record PiercingOutcome(PiercingMark? Mark, string Action, PiercingMark? Evicted = null, string? Error = null);

/// <summary>
/// Mutate <see cref="Character.Piercings"/> only through <see cref="PiercingChange"/> (or return that change
/// from an <c>IDomainEventHandler</c>). Do not call these helpers on tracked entities from a plugin and skip the verb.
/// </summary>
public static class PiercingHelpers
{
    /// <summary>Raised for stacked jewelry (several labia rings, bilateral nipple sets, ear stacks).</summary>
    public const int MaxPerHost = 32;
    public const int MaxSiteLength = 60;
    public const int MaxKindLength = 40;
    public const int MaxNoteLength = 120;
    public const int MaxIdLength = 24;
    public const int SummaryMarks = 3;

    public static bool HasPiercings(this Character character) => character.Piercings is { Count: > 0 };

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? NormalizeSite(string? site) => Clean(site)?.ToLowerInvariant();

    public static string? NormalizeKind(string? kind) => Clean(kind)?.ToLowerInvariant();

    public static string? NormalizeId(string? id)
    {
        var cleaned = Clean(id);
        if (cleaned is null)
            return null;
        return Truncate(cleaned, MaxIdLength);
    }

    public static string NormalizeLoad(string? load) =>
        Clean(load)?.ToLowerInvariant() switch
        {
            PiercingLoads.Light => PiercingLoads.Light,
            PiercingLoads.Heavy => PiercingLoads.Heavy,
            _ => PiercingLoads.None,
        };

    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        tags is null
            ? []
            : tags
                .Select(t => Clean(t)?.ToLowerInvariant())
                .Where(t => t is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public static string DisplayKind(string? kind)
    {
        var normalized = NormalizeKind(kind);
        if (normalized is null)
            return "";
        var dot = normalized.LastIndexOf('.');
        return dot >= 0 && dot < normalized.Length - 1 ? normalized[(dot + 1)..] : normalized;
    }

    /// <summary>Next unused numeric id on this host ("1", "2", …).</summary>
    public static string NextId(IReadOnlyList<PiercingMark> piercings)
    {
        var used = new HashSet<string>(
            piercings.Select(p => p.Id).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        for (var n = 1; n < 10_000; n++)
        {
            var candidate = n.ToString();
            if (!used.Contains(candidate))
                return candidate;
        }

        return Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>
    /// Add a piercing. Default <paramref name="replace"/> false stacks another mark on the same site+kind
    /// (many labia rings). True upserts the first matching site+kind (singular jewelry). Pass
    /// <paramref name="id"/> to update that exact mark, or to assign a chosen id on insert.
    /// </summary>
    public static PiercingOutcome Add(
        List<PiercingMark> piercings,
        string site,
        string kind,
        int day,
        string? material = null,
        string? load = null,
        IEnumerable<string>? tags = null,
        string? note = null,
        string? appliedBy = null,
        string? id = null,
        bool replace = false)
    {
        site = NormalizeSite(site) ?? throw new ArgumentException("site is required.", nameof(site));
        kind = NormalizeKind(kind) ?? throw new ArgumentException("kind is required.", nameof(kind));
        material = Clean(material)?.ToLowerInvariant();
        note = Clean(note) is { } n ? Truncate(n, MaxNoteLength) : null;
        appliedBy = Clean(appliedBy) is { } a ? Truncate(a, MaxNoteLength) : null;
        var normalizedLoad = NormalizeLoad(load);
        var normalizedTags = NormalizeTags(tags);
        var wantedId = NormalizeId(id);

        if (wantedId is not null)
        {
            var byId = piercings.FirstOrDefault(p => p.MatchesId(wantedId));
            if (byId is not null)
            {
                ApplyFields(byId, material, normalizedLoad, normalizedTags, note, appliedBy, day, mergeTags: false);
                return new PiercingOutcome(byId, "updated");
            }
        }

        if (replace)
        {
            var existing = piercings.FirstOrDefault(p => p.Matches(site, kind));
            if (existing is not null)
            {
                ApplyFields(existing, material, normalizedLoad, normalizedTags, note, appliedBy, day, mergeTags: false);
                if (wantedId is not null && !existing.MatchesId(wantedId) &&
                    piercings.All(p => !p.MatchesId(wantedId)))
                    existing.Id = wantedId;
                return new PiercingOutcome(existing, "updated");
            }
        }

        PiercingMark? evicted = null;
        if (piercings.Count >= MaxPerHost)
        {
            evicted = piercings.OrderBy(p => p.AppliedDay).First();
            piercings.Remove(evicted);
        }

        if (wantedId is not null && piercings.Any(p => p.MatchesId(wantedId)))
            return new PiercingOutcome(null, "none", Error: $"piercing id '{wantedId}' is already in use.");

        var mark = new PiercingMark
        {
            Id = wantedId ?? NextId(piercings),
            Site = Truncate(site, MaxSiteLength),
            Kind = Truncate(kind, MaxKindLength),
            Material = material,
            Load = normalizedLoad,
            Tags = normalizedTags,
            AppliedDay = day,
            Note = note,
            AppliedBy = appliedBy,
        };
        piercings.Add(mark);
        return new PiercingOutcome(mark, "added", evicted);
    }

    /// <summary>
    /// Update load/tags/material/note. Prefer <paramref name="id"/> when stacks share site+kind.
    /// Without id, requires a unique site+kind match.
    /// </summary>
    public static PiercingOutcome Update(
        List<PiercingMark> piercings,
        string? site,
        string? kind,
        int day,
        string? material = null,
        string? load = null,
        IEnumerable<string>? tags = null,
        string? note = null,
        string? appliedBy = null,
        bool replaceTags = false,
        string? id = null)
    {
        var wantedId = NormalizeId(id);
        PiercingMark? existing;
        if (wantedId is not null)
        {
            existing = piercings.FirstOrDefault(p => p.MatchesId(wantedId));
            if (existing is null)
                return new PiercingOutcome(null, "none", Error: $"no piercing with id '{wantedId}'.");
        }
        else
        {
            site = NormalizeSite(site) ?? throw new ArgumentException("site is required when id is omitted.", nameof(site));
            kind = NormalizeKind(kind) ?? throw new ArgumentException("kind is required when id is omitted.", nameof(kind));
            var matches = piercings.Where(p => p.Matches(site, kind)).ToList();
            if (matches.Count == 0)
                return new PiercingOutcome(null, "none");
            if (matches.Count > 1)
                return new PiercingOutcome(null, "none",
                    Error: $"{matches.Count} piercings match {kind} at {site}; pass id to update one.");
            existing = matches[0];
        }

        if (Clean(material) is { } mat)
            existing.Material = mat.ToLowerInvariant();
        if (Clean(load) is not null)
            existing.Load = NormalizeLoad(load);
        if (tags is not null || replaceTags)
        {
            var normalized = NormalizeTags(tags);
            existing.Tags = replaceTags ? normalized : MergeTags(existing.Tags, normalized);
        }
        if (Clean(note) is { } n)
            existing.Note = Truncate(n, MaxNoteLength);
        if (Clean(appliedBy) is { } a)
            existing.AppliedBy = Truncate(a, MaxNoteLength);
        existing.AppliedDay = day;
        return new PiercingOutcome(existing, "updated");
    }

    /// <summary>
    /// Remove matching piercings. With <paramref name="id"/> removes that mark only.
    /// With site+kind (no id) removes every match; site alone clears the site; neither clears all.
    /// Locked marks are skipped unless <paramref name="force"/>.
    /// </summary>
    public static (IReadOnlyList<PiercingMark> Removed, IReadOnlyList<PiercingMark> Locked) Remove(
        List<PiercingMark> piercings,
        string? site,
        string? kind,
        bool force = false,
        string? id = null)
    {
        var wantedId = NormalizeId(id);
        site = NormalizeSite(site);
        kind = NormalizeKind(kind);

        List<PiercingMark> candidates;
        if (wantedId is not null)
        {
            candidates = piercings.Where(p => p.MatchesId(wantedId)).ToList();
        }
        else
        {
            candidates = piercings.Where(p =>
                (site is null || string.Equals(p.Site, site, StringComparison.OrdinalIgnoreCase)) &&
                (kind is null || string.Equals(p.Kind, kind, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        var locked = new List<PiercingMark>();
        var removed = new List<PiercingMark>();
        foreach (var p in candidates)
        {
            if (!force && p.HasTag(PiercingTags.Locked))
            {
                locked.Add(p);
                continue;
            }

            piercings.Remove(p);
            removed.Add(p);
        }

        return (removed, locked);
    }

    public static string? Summarize(IReadOnlyList<PiercingMark>? piercings)
    {
        if (piercings is not { Count: > 0 })
            return null;

        // Collapse identical site+kind(+material/load) stacks: "3× gold ring at labia left (heavy)".
        var groups = piercings
            .GroupBy(p => $"{p.Site}\u001f{p.Kind}\u001f{p.Material ?? ""}\u001f{p.Load}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .OrderByDescending(g => LoadRank(g[0].Load))
            .ThenByDescending(g => g.Max(p => p.AppliedDay))
            .ToList();

        var parts = new List<string>();
        foreach (var group in groups.Take(SummaryMarks))
        {
            var phrase = Phrase(group[0]);
            parts.Add(group.Count > 1 ? $"{group.Count}× {phrase}" : phrase);
        }

        var shown = groups.Take(SummaryMarks).Sum(g => g.Count);
        var rest = piercings.Count - shown;
        if (rest > 0)
            parts.Add($"+{rest} more");
        return string.Join(", ", parts);
    }

    /// <summary>"iron septum ring (heavy, locked)", "gold earlobe stud".</summary>
    public static string Phrase(PiercingMark p)
    {
        var leaf = DisplayKind(p.Kind);
        var site = p.Site.Replace('.', ' ');
        var head = string.IsNullOrWhiteSpace(p.Material)
            ? $"{leaf} at {site}"
            : $"{p.Material} {leaf} at {site}";
        var extras = new List<string>();
        if (!string.Equals(p.Load, PiercingLoads.None, StringComparison.OrdinalIgnoreCase))
            extras.Add(p.Load);
        extras.AddRange(p.Tags);
        return extras.Count == 0 ? head : $"{head} ({string.Join(", ", extras)})";
    }

    private static void ApplyFields(
        PiercingMark mark,
        string? material,
        string load,
        List<string> tags,
        string? note,
        string? appliedBy,
        int day,
        bool mergeTags)
    {
        mark.Material = material ?? mark.Material;
        mark.Load = load;
        mark.Tags = tags.Count > 0 ? (mergeTags ? MergeTags(mark.Tags, tags) : tags) : mark.Tags;
        mark.Note = note ?? mark.Note;
        mark.AppliedBy = appliedBy ?? mark.AppliedBy;
        mark.AppliedDay = day;
        if (string.IsNullOrWhiteSpace(mark.Id))
            mark.Id = "1";
    }

    private static List<string> MergeTags(IEnumerable<string> existing, IEnumerable<string> added) =>
        existing.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static int LoadRank(string load) => load switch
    {
        PiercingLoads.Heavy => 2,
        PiercingLoads.Light => 1,
        _ => 0,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
