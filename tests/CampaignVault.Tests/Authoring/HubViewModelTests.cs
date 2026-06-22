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
        
        await hubVM.RefreshCloudCommand.ExecuteAsync(null);
        
        Assert.False(hubVM.IsBusy);
    }
}
