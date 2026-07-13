namespace CampaignVault.Models;

/// <summary>
/// Entities that support a soft-delete/archive flag, hiding them from default search/scene
/// results without destroying history or referential integrity.
/// </summary>
public interface IArchivable
{
    bool IsArchived { get; set; }
}
