using System;
using Unity.Properties;
using CampaignVault.UnityClient.AI;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Flows;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI.Mvvm;
using CampaignVault.UnityClient.UI.Settings;

namespace CampaignVault.UnityClient.UI.Shell
{
    /// <summary>
    /// The bar across the top of the table (Shell.uxml): where we are, the two status sigils (server, Dungeon Master),
    /// the buttons that open the pages, and the "Aa" story text menu.
    /// </summary>
    public sealed class TopBarViewModel : ViewModel
    {
        private readonly VaultAppState _s;
        private string _title = string.Empty;
        private string _meta = string.Empty;
        private string _full = string.Empty;
        private bool _serverOk;
        private bool _serverBad;
        private bool _serverBusy;
        private string _serverTip = string.Empty;
        private bool _modelOk;
        private bool _modelBad;
        private bool _modelBusy;
        private string _modelTip = string.Empty;
        private bool _textMenuOpen;
        private string _costText = string.Empty;
        private string _costTip = string.Empty;

        public TopBarViewModel(VaultAppState state, VaultController controller, Action openCampaigns, Action toggleCodex,
            Action toggleSettings, Action openProvider, Action leave)
        {
            _s = state;
            TextSize = new TextSizeViewModel(state, controller);
            CheckServer = delegate { controller.Run(controller.CheckConnection()); };
            OpenProvider = openProvider;
            OpenCampaigns = openCampaigns;
            ToggleCodex = toggleCodex;
            ToggleSettings = toggleSettings;
            Leave = leave;
            ToggleTextMenu = delegate { TextMenuOpen = !TextMenuOpen; };
            Watch(state, StateArea.Campaign | StateArea.Session | StateArea.Pc | StateArea.Connection | StateArea.Busy
                | StateArea.Driver | StateArea.Providers);
        }

        /// <summary>The stepper in the "Aa" menu; it follows the preference wherever it is changed.</summary>
        [CreateProperty] public TextSizeViewModel TextSize { get; private set; }
        [CreateProperty] public string ContextTitle { get { return _title; } private set { Set(ref _title, value); } }
        [CreateProperty] public string ContextMeta { get { return _meta; } private set { Set(ref _meta, value); } }
        /// <summary>Both lines whole: they end in an ellipsis when the bar is narrow.</summary>
        [CreateProperty] public string ContextFull { get { return _full; } private set { Set(ref _full, value); } }
        [CreateProperty] public bool ServerOk { get { return _serverOk; } private set { Set(ref _serverOk, value); } }
        [CreateProperty] public bool ServerBad { get { return _serverBad; } private set { Set(ref _serverBad, value); } }
        [CreateProperty] public bool ServerBusy { get { return _serverBusy; } private set { Set(ref _serverBusy, value); } }
        [CreateProperty] public string ServerTip { get { return _serverTip; } private set { Set(ref _serverTip, value); } }
        [CreateProperty] public bool ModelOk { get { return _modelOk; } private set { Set(ref _modelOk, value); } }
        [CreateProperty] public bool ModelBad { get { return _modelBad; } private set { Set(ref _modelBad, value); } }
        [CreateProperty] public bool ModelBusy { get { return _modelBusy; } private set { Set(ref _modelBusy, value); } }
        [CreateProperty] public string ModelTip { get { return _modelTip; } private set { Set(ref _modelTip, value); } }
        /// <summary>"session ~$0.08 · campaign ~$1.92"; empty (and hidden) until a call has reported usage.</summary>
        [CreateProperty] public string CostText { get { return _costText; } private set { Set(ref _costText, value); } }
        [CreateProperty] public bool CostVisible { get { return _costText.Length > 0; } }
        [CreateProperty] public string CostTip { get { return _costTip; } private set { Set(ref _costTip, value); } }
        [CreateProperty] public bool TextMenuOpen { get { return _textMenuOpen; } private set { Set(ref _textMenuOpen, value); } }

        [CreateProperty] public Action CheckServer { get; private set; }
        [CreateProperty] public Action OpenProvider { get; private set; }
        [CreateProperty] public Action OpenCampaigns { get; private set; }
        [CreateProperty] public Action ToggleCodex { get; private set; }
        [CreateProperty] public Action ToggleSettings { get; private set; }
        [CreateProperty] public Action Leave { get; private set; }
        [CreateProperty] public Action ToggleTextMenu { get; private set; }

        public void ShowTextMenu(bool open) { TextMenuOpen = open; }

        public override void Dispose()
        {
            TextSize.Dispose();
            base.Dispose();
        }

        public override void Refresh()
        {
            PaintContext();
            ServerOk = _s.Connection == ConnectionStatus.Healthy;
            ServerBad = _s.Connection == ConnectionStatus.Down;
            ServerBusy = _s.Connection == ConnectionStatus.Checking || _s.IsBusy("embedded");
            ServerTip = ServerTooltip();
            var driver = _s.Driver;
            string reason;
            bool ready = _s.ProviderReady(out reason);
            bool busy = driver != null && driver.IsBusy;
            bool failed = driver != null && !busy && !string.IsNullOrEmpty(driver.LastError);
            ModelBusy = busy;
            ModelBad = !ready || failed;
            ModelOk = ready && !busy && !failed;
            ModelTip = ModelTooltip(ready, reason);
            PaintCost(driver);
        }

        private void PaintCost(AI.OpenAiChatDriver driver)
        {
            var store = driver != null ? driver.CampaignUsage : null;
            string slug = _s.CampaignSlug;
            TokenUsage session = driver != null ? driver.SessionUsage : null;
            TokenUsage campaign = store != null && !string.IsNullOrEmpty(slug) ? store.Total(slug) : null;
            CostText = UsageSummary.Chip(session, campaign);
            Notify(nameof(CostVisible));
            CostTip = UsageSummary.Tooltip(session, campaign,
                campaign != null ? store.Breakdown(slug) : null, ModelPricing.Bundled.AsOf);
        }

        private void PaintContext()
        {
            var s = _s.Session;
            string title = s != null && s.CampaignDisplay.Length > 0 ? s.CampaignDisplay
                : _s.HasCampaign ? Ui.PrettyId(_s.CampaignSlug) : "No campaign at the table";
            string meta;
            if (!_s.HasCampaign) { meta = "Open the campaign book to begin."; }
            else if (s == null) { meta = "The session hasn't opened yet."; }
            else
            {
                meta = "Session " + s.SessionNumber;
                if (s.Time.Length > 0) { meta += " · " + ShortTime(s.Time); }
                var pc = _s.PcMember;
                if (pc != null && pc.Location.Length > 0) { meta += " · " + Ui.PrettyId(pc.Location); }
            }
            ContextTitle = DisplayText.Plain(title.ToUpperInvariant());
            ContextMeta = DisplayText.Plain(meta);
            ContextFull = title + "\n" + meta;
        }

        /// <summary>"Day 1, Month 1, Year 1492 (A drowned mill town…) — Dawn" → "Day 1, Month 1, Year 1492 — Dawn".</summary>
        public static string ShortTime(string time)
        {
            return System.Text.RegularExpressions.Regex.Replace(time ?? string.Empty, @"\s*\([^)]*\)", string.Empty).Trim();
        }

        private string ServerTooltip()
        {
            string state;
            switch (_s.Connection)
            {
                case ConnectionStatus.Healthy: state = "connected"; break;
                case ConnectionStatus.Down: state = "unreachable" + (_s.ConnectionMessage.Length > 0 ? " (" + _s.ConnectionMessage + ")" : string.Empty); break;
                case ConnectionStatus.Checking: state = "checking…"; break;
                default: state = "not checked yet"; break;
            }
            return "Campaign server: " + state + ". Click to check again.";
        }

        private string ModelTooltip(bool ready, string reason)
        {
            if (!ready) { return "No working Dungeon Master: " + reason + " Click to set one up."; }
            var driver = _s.Driver;
            string state = driver == null ? "ready" : driver.IsBusy ? "thinking…" : !string.IsNullOrEmpty(driver.LastError) ? "last turn failed: " + driver.LastError : "ready";
            return "Dungeon Master: " + _s.Byok.Model + ", " + state + ". Click to change it.";
        }
    }
}
