namespace CampaignVault.Authoring.Services;

public enum AppState { Idle, Editor }

public partial class AppStateService : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private AppState _currentState = AppState.Idle;
}
