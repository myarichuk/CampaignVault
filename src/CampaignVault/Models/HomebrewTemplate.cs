namespace CampaignVault.Models;

/// <summary>
/// A campaign's homebrew subclass (class option), ancestry or named power. The body is the YAML a plugin would put in
/// <c>classOptions/</c>, <c>races/</c> / <c>ancestries/</c> or <c>powers/</c>, so one format and one loader serve both,
/// and the character builder offers it (tagged homebrew) to this campaign alone. See <c>HomebrewScope</c>.
/// </summary>
public class HomebrewTemplate : IArchivable
{
    public string Id { get; set; } = null!;

    /// <summary>classOption | ancestry | power (<c>HomebrewKinds</c>).</summary>
    public string Kind { get; set; } = null!;

    public string System { get; set; } = null!;

    /// <summary>The template's own name, read from the YAML when it was saved.</summary>
    public string Name { get; set; } = null!;

    public string Yaml { get; set; } = null!;

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public string? CampaignName { get; set; }

    /// <summary>When true the builder stops offering it; characters that already have it keep it.</summary>
    public bool IsArchived { get; set; }
}
