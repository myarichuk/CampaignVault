using System.Threading.Tasks;
using CampaignVault.Authoring.Vault;
using CampaignVault.Authoring.Vault.Sync;
using CampaignVault.Authoring.ViewModels;

namespace CampaignVault.Authoring.Services;

public interface IWorkspaceState
{
    CampaignVaultSession Session { get; }
    WorkspaceViewModel Workspace { get; }
    string EditorText { get; set; }
    SyncViewModel Sync { get; }
    McpServerService? McpServerService { get; set; }

    void RefreshAll();
    void ReloadActiveFileContent();
    
    CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<string?> CreateNewEntityCommand { get; }
    CommunityToolkit.Mvvm.Input.IAsyncRelayCommand DeleteSelectedEntityCommand { get; }
}
