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
