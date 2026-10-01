using System;
using System.Collections.Generic;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>Step 1: is the server up? Checks it for you once the built-in server has had its chance to start.</summary>
    public sealed class SetupServerPage : ViewModel, ITemplated
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private string _url = string.Empty;
        private string _seenUrl;
        private string _status = string.Empty;
        private string _tone = "muted";
        private bool _showLicense;

        public SetupServerPage(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            SaveAndCheck = delegate { controller.SetServerUrl(_url); controller.Run(controller.CheckConnection()); };
            Watch(state, StateArea.Connection | StateArea.Embedded);
            Refresh();
        }

        public string Template { get { return "Setup/ServerStep"; } }

        [CreateProperty] public string Url { get { return _url; } set { Set(ref _url, value ?? string.Empty); } }
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public string StatusTone { get { return _tone; } private set { Set(ref _tone, value); } }
        /// <summary>The built-in server runs RavenDB, which asks for a license key.</summary>
        [CreateProperty] public bool ShowLicense { get { return _showLicense; } private set { Set(ref _showLicense, value); } }
        [CreateProperty] public Action SaveAndCheck { get; private set; }

        public override void Refresh()
        {
            if (_s.Config.ServerUrl != _seenUrl) { _seenUrl = _s.Config.ServerUrl; Url = _seenUrl; }
            bool booting = _s.IsBusy("embedded") || !_c.AutostartSettled;
            string message = booting && _s.Connection != ConnectionStatus.Healthy
                ? (_s.EmbeddedMessage.Length > 0 ? _s.EmbeddedMessage : "Starting the built-in server…")
                : _s.Connection == ConnectionStatus.Healthy ? "The server is healthy."
                : _s.Connection == ConnectionStatus.Down ? "Not reachable yet (" + _s.ConnectionMessage + "). The embedded server can take a few seconds to start: check again, or continue and fix it later in Settings."
                : _s.Connection == ConnectionStatus.Checking ? "Checking…" : string.Empty;
            Status = DisplayText.Plain(message);
            StatusTone = _s.Connection == ConnectionStatus.Healthy ? "leaf" : "muted";
            ShowLicense = _s.Server != null && _c.RavenLicenseHolder() == null;
            // While autostart is still booting the server, checking now would only say "unreachable".
            if (_s.Connection == ConnectionStatus.Unknown && !booting) { _c.Run(_c.CheckConnection()); }
        }
    }

    /// <summary>Step 2: the Dungeon Master's model.</summary>
    public sealed class SetupProviderPage : ViewModel, ITemplated
    {
        public SetupProviderPage(VaultAppState state, VaultController controller)
        {
            Provider = new ProviderFormViewModel(state, controller);
        }

        public string Template { get { return "Setup/ProviderStep"; } }

        [CreateProperty] public ProviderFormViewModel Provider { get; private set; }

        public override void Dispose()
        {
            Provider.Dispose();
            base.Dispose();
        }
    }

    /// <summary>Step 3: create a campaign or open one.</summary>
    public sealed class SetupTablePage : ViewModel, ITemplated
    {
        public SetupTablePage(Action create, Action open)
        {
            Create = create;
            Open = open;
        }

        public string Template { get { return "Setup/TableStep"; } }

        [CreateProperty] public Action Create { get; private set; }
        [CreateProperty] public Action Open { get; private set; }
    }

    /// <summary>First run (Templates/Setup/Setup.uxml): server, Dungeon Master, then your first campaign.</summary>
    public sealed class SetupViewModel : ViewModel
    {
        private static readonly string[] Steps = { "The server", "Your Dungeon Master", "Your table" };

        private readonly VaultAppState _s;
        private readonly ViewModel[] _pages;
        private readonly Action _close;
        private int _step = -1;
        private ViewModel _page;
        private string _stepTitle = string.Empty;
        private bool _showBack;
        private bool _canNext = true;
        private string _nextLabel = "NEXT";

        public SetupViewModel(VaultAppState state, VaultController controller, Action close, Action openOnboarding, Action openCampaigns)
        {
            _s = state;
            _close = close;
            _pages = new ViewModel[]
            {
                new SetupServerPage(state, controller),
                new SetupProviderPage(state, controller),
                new SetupTablePage(
                    delegate { close(); if (openOnboarding != null) { openOnboarding(); } },
                    delegate { close(); if (openCampaigns != null) { openCampaigns(); } }),
            };
            Back = delegate { Go(_step - 1); };
            Next = Advance;
            Watch(state, StateArea.Providers);
        }

        [CreateProperty] public ViewModel Page { get { return _page; } private set { Set(ref _page, value); } }
        [CreateProperty] public string StepTitle { get { return _stepTitle; } private set { Set(ref _stepTitle, value); } }
        [CreateProperty] public bool ShowBack { get { return _showBack; } private set { Set(ref _showBack, value); } }
        [CreateProperty] public bool CanNext { get { return _canNext; } private set { Set(ref _canNext, value); } }
        [CreateProperty] public string NextLabel { get { return _nextLabel; } private set { Set(ref _nextLabel, value); } }
        [CreateProperty] public Action Back { get; private set; }
        [CreateProperty] public Action Next { get; private set; }

        public int Step { get { return _step; } }

        /// <summary>Sent here to fix the provider with the server already fine: skip straight to it.</summary>
        public void Start()
        {
            string reason;
            Go(_s.Connection == ConnectionStatus.Healthy && !_s.ProviderReady(out reason) ? 1 : 0);
        }

        public void Go(int step)
        {
            _step = Mathf.Clamp(step, 0, Steps.Length - 1);
            Page = _pages[_step];
            Refresh();
        }

        private void Advance()
        {
            if (_step < Steps.Length - 1) { Go(_step + 1); return; }
            _close();
        }

        public override void Refresh()
        {
            if (_step < 0) { return; }
            string reason;
            bool ready = _s.Byok.Validate(out reason);
            StepTitle = "STEP " + (_step + 1) + " OF " + Steps.Length + " · " + Steps[_step].ToUpperInvariant();
            ShowBack = _step > 0;
            CanNext = _step != 1 || ready;
            NextLabel = _step == Steps.Length - 1 ? "TO THE TABLE" : "NEXT";
        }

        public override void Dispose()
        {
            foreach (var page in _pages) { page.Dispose(); }
            base.Dispose();
        }
    }
}
