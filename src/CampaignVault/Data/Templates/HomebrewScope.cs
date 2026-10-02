namespace CampaignVault.Data.Templates;

/// <summary>The kinds of template a DM can homebrew for a campaign; each is a plugin file kind (see PLUGINS.md).</summary>
public static class HomebrewKinds
{
    /// <summary>A race or ancestry: the file a plugin puts in <c>races/</c> or <c>ancestries/</c>.</summary>
    public const string Ancestry = "ancestry";

    /// <summary>A subclass or other class option: a <c>classOptions/</c> file.</summary>
    public const string ClassOption = "classOption";

    /// <summary>A named power (god, patron, lineage): a <c>powers/</c> file.</summary>
    public const string Power = "power";

    public static readonly IReadOnlyList<string> All = [Ancestry, ClassOption, Power];

    /// <summary>The canonical spelling of a kind (any case), or null for something that isn't one.</summary>
    public static string? Canonical(string? kind) =>
        All.FirstOrDefault(k => string.Equals(k, kind?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>One homebrew template of a campaign: the same YAML a plugin file holds, kept with the campaign.</summary>
public sealed record HomebrewEntry(string System, string Kind, string Yaml);

/// <summary>
/// The homebrew a campaign holds, as the template providers see it for the call in flight. <see cref="Stamp"/> is a hash
/// of the content, so providers can keep what they resolved from it across calls, and drop it when the homebrew changes.
/// </summary>
public sealed class HomebrewSnapshot(
    string campaign,
    IReadOnlyList<HomebrewEntry> entries,
    IReadOnlyList<FeatDefinition>? feats = null,
    IReadOnlyList<SpellDefinition>? spells = null)
{
    /// <summary>The source label of the templates; anything but "core" makes them show as homebrew.</summary>
    public const string Source = "campaign";

    public string Campaign { get; } = campaign;

    /// <summary>The campaign's own feats and spells (its <c>upsert_feat</c> / <c>upsert_spell</c> documents), read as templates.</summary>
    public IReadOnlyList<FeatDefinition> Feats { get; } = feats ?? [];

    public IReadOnlyList<SpellDefinition> Spells { get; } = spells ?? [];

    public string Stamp { get; } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(string.Join('\u001f',
            entries.Select(e => $"{e.System}\u001e{e.Kind}\u001e{e.Yaml}")
                .Concat((feats ?? []).Select(f => $"feat\u001e{f.System}\u001e{f.Name}\u001e{f.Description}\u001e{f.Prerequisite}\u001e{f.Level}\u001e{f.Effects.Count}"))
                .Concat((spells ?? []).Select(sp => $"spell\u001e{sp.System}\u001e{sp.Name}\u001e{sp.Description}\u001e{sp.Level}\u001e{string.Join(',', sp.Classes)}"))))));

    public IReadOnlyList<HomebrewEntry> For(string kind, string system) =>
        [.. entries.Where(e => e.Kind == kind && string.Equals(e.System, system, StringComparison.OrdinalIgnoreCase))];

    /// <summary>The campaign's feats for a ruleset (a feat with no ruleset of its own is offered in every one).</summary>
    public IReadOnlyList<FeatDefinition> FeatsFor(string system) =>
        [.. Feats.Where(f => string.IsNullOrEmpty(f.System) || string.Equals(f.System, system, StringComparison.OrdinalIgnoreCase))];

    public IReadOnlyList<SpellDefinition> SpellsFor(string system) =>
        [.. Spells.Where(sp => string.IsNullOrEmpty(sp.System) || string.Equals(sp.System, system, StringComparison.OrdinalIgnoreCase))];
}

/// <summary>
/// The campaign homebrew in effect for the current call. Template providers are shared by every campaign, so rather
/// than thread a campaign through each lookup, the tool layer enters a scope once per campaign call and the providers
/// read it. It flows with the async call, so concurrent calls for different campaigns don't see each other's.
/// </summary>
public static class HomebrewScope
{
    private static readonly AsyncLocal<HomebrewSnapshot?> Held = new();

    public static HomebrewSnapshot? Current => Held.Value;

    public static IDisposable Enter(HomebrewSnapshot? snapshot)
    {
        var previous = Held.Value;
        Held.Value = snapshot;
        return new Restore(previous);
    }

    private sealed class Restore(HomebrewSnapshot? previous) : IDisposable
    {
        public void Dispose() => Held.Value = previous;
    }
}
