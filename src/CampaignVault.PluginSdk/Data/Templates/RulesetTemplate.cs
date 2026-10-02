using System.Collections;
using CampaignVault.Models;

namespace CampaignVault.Data.Templates;

public abstract record RulesetTemplate
{
    public string Name { get; init; } = null!;
    public List<string> Inherits { get; init; } = [];
    public string? Description { get; init; }

    /// <summary>
    /// Plugin/mode gate for the whole template: hidden from lists (handbook, lookups, the character builder) unless the
    /// plugin is loaded and, if named, the mode is running. Kept, not deleted, so it returns with them.
    /// </summary>
    public FeatRequirement? Requires { get; init; }

    /// <summary>
    /// <c>hidden: true</c> in a <c>patches:</c> file takes a shipped template out of every list and lookup: the way a plugin
    /// switches off content it replaces or doesn't want. Not inherited. (Gate with <see cref="Requires"/> instead to keep it.)
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// True for content that didn't ship with the host: from a plugin or a folder the DM added. Shown as a "homebrew" tag,
    /// because the shipped rules are the free-licensed ones and anything else is the author's own responsibility.
    /// </summary>
    public bool Homebrew => Source is not null && !Source.Equals("core", StringComparison.OrdinalIgnoreCase);

    // Loader bookkeeping below: never part of the YAML schema, never serialized.

    /// <summary>Set for a <c>patches: &lt;name&gt;</c> file: merged into that template instead of standing alone.</summary>
    internal string? PatchTarget { get; set; }

    /// <summary>Pending <c>&lt;list&gt;+:</c> / <c>&lt;list&gt;-:</c> edits, applied in order once when the template resolves.</summary>
    internal IReadOnlyList<TemplateListOp> ListOps { get; set; } = [];

    /// <summary>Where the template was loaded from ("core" or a plugin), for collision and patch warnings.</summary>
    internal string? Source { get; set; }
}

/// <summary>One list edit from YAML: append (<c>traits+:</c>) or remove (<c>traits-:</c>) <see cref="Items"/> on <see cref="Property"/>.</summary>
internal sealed record TemplateListOp(string Property, bool Remove, IList Items);
