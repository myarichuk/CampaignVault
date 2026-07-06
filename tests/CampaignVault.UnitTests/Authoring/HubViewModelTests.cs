using System.Threading.Tasks;
using CampaignVault.Authoring.ViewModels;
using Xunit;

namespace CampaignVault.Tests.Authoring;

public class HubViewModelTests
{
    [Fact]
    public async Task RefreshCloudAsync_SetsIsBusy()
    {
        var mainVM = new MainWindowViewModel();
        var hubVM = mainVM.Hub;

        Assert.False(hubVM.IsBusy);
        
        bool wasBusy = false;
        hubVM.PropertyChanged += (s, e) => 
        {
            if (e.PropertyName == nameof(hubVM.IsBusy) && hubVM.IsBusy)
            {
                wasBusy = true;
            }
        };

        await hubVM.RefreshCloudCommand.ExecuteAsync(null);
        
        Assert.True(wasBusy);
        Assert.False(hubVM.IsBusy);
    }
}
