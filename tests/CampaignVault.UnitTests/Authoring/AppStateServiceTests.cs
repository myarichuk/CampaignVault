using CampaignVault.Authoring.Services;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class AppStateServiceTests
{
    [Fact]
    public void InitialState_ShouldBeIdle()
    {
        var service = new AppStateService();
        Assert.Equal(AppState.Idle, service.CurrentState);
    }

    [Fact]
    public void StateChange_ShouldNotify()
    {
        var service = new AppStateService();
        bool notified = false;
        service.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AppStateService.CurrentState))
            {
                notified = true;
            }
        };

        service.CurrentState = AppState.Editor;
        Assert.Equal(AppState.Editor, service.CurrentState);
        Assert.True(notified);
    }
}
