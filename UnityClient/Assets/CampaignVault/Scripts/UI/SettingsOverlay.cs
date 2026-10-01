using System;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Settings;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>Settings, tabbed: layout in Templates/Settings, state in <see cref="SettingsViewModel"/> and a page view model per tab.</summary>
    public sealed class SettingsOverlay : Overlay
    {
        public const int PluginsTab = SettingsViewModel.PluginsTab;
        public const int AdvancedTab = SettingsViewModel.AdvancedTab;

        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openSetup;
        private int _tab;

        public SettingsOverlay(VaultAppState state, VaultController controller, Action openSetup)
        {
            _state = state;
            _controller = controller;
            _openSetup = openSetup;
        }

        protected override string Title { get { return "Settings"; } }
        protected override string TitleIcon { get { return "settings"; } }
        protected override string Template { get { return "Settings/Settings"; } }

        /// <summary>Opens on this tab (before the overlay opens, or while it is open).</summary>
        public void ShowTab(int tab)
        {
            _tab = tab;
            var settings = ViewModel as SettingsViewModel;
            if (settings != null) { settings.Show(tab); }
        }

        protected override ViewModel CreateViewModel()
        {
            var settings = new SettingsViewModel(_state, _controller, Confirm, delegate { Close(); if (_openSetup != null) { _openSetup(); } });
            settings.Show(_tab);
            return settings;
        }

        private void Confirm(string title, string message, string confirm, bool danger, Action onConfirm)
        {
            Host.Open(new ConfirmOverlay(title, message, confirm, danger, onConfirm));
        }
    }

    /// <summary>First run: server, Dungeon Master, then your first campaign. Can't be dismissed until a provider works.</summary>
    public sealed class SetupOverlay : Overlay
    {
        private readonly VaultAppState _state;
        private readonly VaultController _controller;
        private readonly Action _openOnboarding;
        private readonly Action _openCampaigns;

        public SetupOverlay(VaultAppState state, VaultController controller, Action openOnboarding, Action openCampaigns)
        {
            _state = state;
            _controller = controller;
            _openOnboarding = openOnboarding;
            _openCampaigns = openCampaigns;
        }

        protected override string Title { get { return "Welcome to the Vault"; } }
        protected override string TitleIcon { get { return "crest"; } }
        protected override bool Narrow { get { return true; } }
        protected override string Template { get { return "Setup/Setup"; } }

        public override bool CanDismiss
        {
            get
            {
                string reason;
                return _state.Byok.Validate(out reason);
            }
        }

        protected override ViewModel CreateViewModel()
        {
            var setup = new SetupViewModel(_state, _controller, Close, _openOnboarding, _openCampaigns);
            setup.Start();
            return setup;
        }
    }
}
