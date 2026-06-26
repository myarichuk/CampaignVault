using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Services;

public static class WorkspaceService
{
    public static MainWindowViewModel? MainWindowViewModel { get; set; }
    public static CampaignVaultSession? VaultSession { get; set; }
    public static McpServerService? McpServerService { get; set; }
}