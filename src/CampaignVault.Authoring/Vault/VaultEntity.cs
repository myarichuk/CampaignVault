namespace CampaignVault.Authoring.Vault;

public sealed class VaultEntity
{
    public required string Id { get; init; }

    public required string EntityType { get; init; }

    public required string RelativePath { get; init; }

    /// <summary>SHA-256 of raw file bytes (normalized line endings).</summary>
    public required string ContentHash { get; init; }

    /// <summary>SHA-256 of canonical markdown form — used for vault sync comparison.</summary>
    public string CanonicalHash { get; init; } = string.Empty;

    public bool HasValidFrontmatter { get; init; }

    public string? ParseError { get; init; }
}