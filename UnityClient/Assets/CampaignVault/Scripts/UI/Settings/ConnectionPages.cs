using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Properties;
using UnityEngine;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI.Mvvm;

namespace CampaignVault.UnityClient.UI.Settings
{
    /// <summary>The campaign server: its address, which connector the Dungeon Master uses, and an optional bearer token.</summary>
    public sealed class ServerPage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private readonly List<ChoiceViewModel> _connectors = new List<ChoiceViewModel>();
        private string _url = string.Empty;
        private string _seenUrl;
        private string _token = string.Empty;
        private string _status = string.Empty;
        private string _tone = "muted";

        public ServerPage(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            _connectors.Add(new ChoiceViewModel("play", "PLAY", "d20", false, delegate { Pick("play"); }, "connector-play"));
            _connectors.Add(new ChoiceViewModel("build", "BUILD", "map", false, delegate { Pick("build"); }, "connector-build"));
            SaveAndCheck = delegate { controller.SetServerUrl(_url); controller.Run(controller.CheckConnection()); };
            SetToken = delegate { controller.SetBearerToken(_token); Token = string.Empty; };
            ClearToken = delegate { controller.ClearBearerToken(); };
            Watch(state, StateArea.Connection);
            Refresh();
        }

        public override string Template { get { return "Settings/ServerPage"; } }

        [CreateProperty] public string Url { get { return _url; } set { Set(ref _url, value ?? string.Empty); } }
        [CreateProperty] public string Token { get { return _token; } set { Set(ref _token, value ?? string.Empty); } }
        [CreateProperty] public string Status { get { return _status; } private set { Set(ref _status, value); } }
        [CreateProperty] public string StatusTone { get { return _tone; } private set { Set(ref _tone, value); } }
        [CreateProperty] public List<ChoiceViewModel> Connectors { get { return _connectors; } }
        [CreateProperty] public Action SaveAndCheck { get; private set; }
        [CreateProperty] public Action SetToken { get; private set; }
        [CreateProperty] public Action ClearToken { get; private set; }

        private void Pick(string connector) { _c.SetConnector(connector); }

        public override void Refresh()
        {
            if (_s.Config.ServerUrl != _seenUrl) { _seenUrl = _s.Config.ServerUrl; Url = _seenUrl; }
            Status = DisplayText.Plain(string.IsNullOrEmpty(_s.ConnectionMessage) ? "Not checked yet." : _s.ConnectionMessage);
            StatusTone = _s.Connection == ConnectionStatus.Healthy ? "leaf" : _s.Connection == ConnectionStatus.Down ? "blood" : "muted";
            bool play = _s.Config.ActiveConnector() == "play";
            _connectors[0].Update("PLAY", "d20", play);
            _connectors[1].Update("BUILD", "map", !play);
        }
    }

    /// <summary>The server that ships inside desktop builds, and the RavenDB license it asks for.</summary>
    public sealed class EmbeddedPage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private string _port = "5275";
        private string _seenPort;
        private bool _autoStart;
        private bool _running;
        private string _startLabel = "START SERVER";
        private string _message = string.Empty;
        private string _tone = "muted";
        private string _licenseNote = string.Empty;
        private string _licenseTone = "muted";
        private bool _hasLicense;
        private string _licenseInput = string.Empty;

        public EmbeddedPage(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            Start = delegate
            {
                int p;
                var server = _s.Server;
                if (!int.TryParse(_port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out p)) { p = server != null ? server.Port : 5275; }
                _c.Run(_c.StartEmbedded(p));
            };
            Stop = delegate { _c.StopEmbedded(); };
            SaveLicense = delegate { if (_c.SaveRavenLicense(_licenseInput)) { Refresh(); } };
            GetLicense = delegate { Application.OpenURL(EmbeddedServerSupport.CommunityLicenseUrl); };
            RemoveLicense = delegate { _c.RemoveRavenLicense(); Refresh(); };
            Watch(state, StateArea.Embedded | StateArea.Busy);
            Refresh();
        }

        public override string Template { get { return "Settings/EmbeddedPage"; } }

        [CreateProperty] public string Port { get { return _port; } set { Set(ref _port, value ?? string.Empty); } }
        [CreateProperty]
        public bool AutoStart
        {
            get { return _autoStart; }
            set { if (Set(ref _autoStart, value)) { _c.SetEmbeddedAutostart(value); } }
        }
        [CreateProperty] public bool Running { get { return _running; } private set { Set(ref _running, value); } }
        [CreateProperty] public string StartLabel { get { return _startLabel; } private set { Set(ref _startLabel, value); } }
        [CreateProperty] public string Message { get { return _message; } private set { Set(ref _message, value); } }
        [CreateProperty] public string MessageTone { get { return _tone; } private set { Set(ref _tone, value); } }
        [CreateProperty] public string LicenseNote { get { return _licenseNote; } private set { Set(ref _licenseNote, value); } }
        [CreateProperty] public string LicenseTone { get { return _licenseTone; } private set { Set(ref _licenseTone, value); } }
        [CreateProperty] public bool HasLicense { get { return _hasLicense; } private set { Set(ref _hasLicense, value); } }
        [CreateProperty] public string LicenseInput { get { return _licenseInput; } set { Set(ref _licenseInput, value ?? string.Empty); } }
        [CreateProperty] public Action Start { get; private set; }
        [CreateProperty] public Action Stop { get; private set; }
        [CreateProperty] public Action SaveLicense { get; private set; }
        [CreateProperty] public Action GetLicense { get; private set; }
        [CreateProperty] public Action RemoveLicense { get; private set; }

        public override void Refresh()
        {
            var server = _s.Server;
            bool running = server != null && server.IsRunning;
            string port = server != null ? server.Port.ToString(CultureInfo.InvariantCulture) : "5275";
            if (port != _seenPort) { _seenPort = port; Port = port; }
            Set(ref _autoStart, server != null && server.AutoStart, "AutoStart");
            Running = running;
            StartLabel = _s.IsBusy("embedded") ? "STARTING…" : "START SERVER";
            string message = string.IsNullOrEmpty(_s.EmbeddedMessage) ? (running ? "Running." : "Idle.") : _s.EmbeddedMessage;
            if (running && server.ActivePort != 0 && server.ActivePort != server.Port) { message += " Port " + server.Port + " was busy, so it runs on " + server.ActivePort + "."; }
            Message = DisplayText.Plain(message);
            MessageTone = running ? "leaf" : "muted";

            string holder = _c.RavenLicenseHolder();
            HasLicense = holder != null;
            LicenseNote = DisplayText.Plain(holder == null ? "No license saved."
                : "License saved" + (holder.Length > 0 ? " for " + holder : string.Empty) + ". It applies from the next server start.");
            LicenseTone = holder == null ? "muted" : "leaf";
        }
    }

    /// <summary>The story text size, an A− / size / A+ stepper (Templates/Common/TextSize.uxml). Also used by the top bar's "Aa" menu.</summary>
    public sealed class TextSizeViewModel : ViewModel, ITemplated
    {
        private readonly VaultAppState _s;
        private string _label = string.Empty;
        private bool _canSmaller;
        private bool _canLarger;

        public TextSizeViewModel(VaultAppState state, VaultController controller)
        {
            _s = state;
            Smaller = delegate { controller.SetStoryTextSize(state.StoryTextSize - 1); };
            Larger = delegate { controller.SetStoryTextSize(state.StoryTextSize + 1); };
            Watch(state, StateArea.Preferences);
            Refresh();
        }

        public string Template { get { return "Common/TextSize"; } }

        [CreateProperty] public string Label { get { return _label; } private set { Set(ref _label, value); } }
        [CreateProperty] public bool CanSmaller { get { return _canSmaller; } private set { Set(ref _canSmaller, value); } }
        [CreateProperty] public bool CanLarger { get { return _canLarger; } private set { Set(ref _canLarger, value); } }
        [CreateProperty] public Action Smaller { get; private set; }
        [CreateProperty] public Action Larger { get; private set; }

        public override void Refresh()
        {
            int step = _s.StoryTextSize;
            Label = TextSizeControl.NameOf(step) + " · " + VaultAppState.StoryTextSizes[step] + " px";
            CanSmaller = step > 0;
            CanLarger = step < VaultAppState.StoryTextSizes.Length - 1;
        }
    }

    /// <summary>Motion, sound and the story text size.</summary>
    public sealed class FeelPage : SettingsPage
    {
        private readonly VaultAppState _s;
        private readonly VaultController _c;
        private bool _fx;
        private bool _sound;

        public FeelPage(VaultAppState state, VaultController controller)
        {
            _s = state;
            _c = controller;
            TextSize = new TextSizeViewModel(state, controller);
            Watch(state, StateArea.Preferences);
            Refresh();
        }

        public override string Template { get { return "Settings/FeelPage"; } }

        [CreateProperty]
        public bool Fx
        {
            get { return _fx; }
            set { if (Set(ref _fx, value)) { _c.SetFx(value); } }
        }

        [CreateProperty]
        public bool Sound
        {
            get { return _sound; }
            set
            {
                if (!Set(ref _sound, value)) { return; }
                _c.SetSfxMuted(!value);
                VaultSfx.Muted = !value;
            }
        }

        [CreateProperty] public TextSizeViewModel TextSize { get; private set; }

        public override void Refresh()
        {
            Set(ref _fx, _s.FxEnabled, "Fx");
            Set(ref _sound, !_s.SfxMuted, "Sound");
        }

        public override void Dispose()
        {
            TextSize.Dispose();
            base.Dispose();
        }
    }
}
