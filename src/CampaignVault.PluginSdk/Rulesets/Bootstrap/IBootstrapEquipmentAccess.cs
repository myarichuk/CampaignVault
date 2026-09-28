using CampaignVault.Models;

namespace CampaignVault.Rulesets.Bootstrap;

/// <summary>
/// Optional, narrow data access for bootstrap steps that derive stats from other
/// documents (e.g. defense steps reading equipped armor). The host provides a
/// session-backed implementation; out-of-tree steps receive it through
/// <see cref="BootstrapContext.EquipmentAccess"/> the same way. Null on the context
/// means no equipment data is available — degrade gracefully (unarmored defaults),
/// the same way a null session behaved before.
/// </summary>
public interface IBootstrapEquipmentAccess
{
    Task<IReadOnlyList<Item>> GetEquippedItemsAsync(string characterId, CancellationToken ct = default);
}
