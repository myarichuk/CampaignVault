using System;

namespace CampaignVault.Models
{
    public interface IHasSemanticVector
    {
        float[]? SemanticVector { get; set; }
    }
}
