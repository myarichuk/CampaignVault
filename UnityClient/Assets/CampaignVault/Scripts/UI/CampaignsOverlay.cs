using System;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.World;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>The campaign book: layout in Templates/World/Campaigns.uxml, state in <see cref="CampaignsViewModel"/>.</summary>
    public sealed class CampaignsOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openOnboarding;

        public CampaignsOverlay(VaultAppState state, VaultController controller, Action openOnboarding)
        {
            _state = state;
            _controller = controller;
            _openOnboarding = openOnboarding;
        }

        protected override string Title { get { return "Your campaigns"; } }
        protected override string TitleIcon { get { return "campaigns"; } }
        protected override string Template { get { return "World/Campaigns"; } }

        protected override ViewModel CreateViewModel()
        {
            return new CampaignsViewModel(_state, _controller, Close, _openOnboarding, delegate (Action action, long ms) { Root.schedule.Execute(action).StartingIn(ms); });
        }

        public override void OnOpen() { _controller.Run(_controller.ListCampaigns()); }
    }
}
