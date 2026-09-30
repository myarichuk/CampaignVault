using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Diagnostics;

namespace CampaignVault.UnityClient.UI
{
    /// <summary>
    /// The client UI root (UI Toolkit). Loads the shell (Resources/VaultUI/
    /// Shell.uxml, themed by VaultPanelSettings) onto a UIDocument and binds
    /// every view to the app layer on the same GameObject (VaultBootstrap).
    /// Keeps its old name so scenes and the CampaignVault menu that reference
    /// it keep working. Headless smoke runs skip the UI entirely.
    /// </summary>
    [DisallowMultipleComponent]
    public class VaultClientUI : MonoBehaviour
    {
        // Layout breakpoints, in panel pixels (the panel scales from 1920x1080).
        private const float NarrowWidth = 1560f;
        private const float CompactWidth = 1240f;

        private VaultBootstrap _boot;
        private VaultAppState _state;
        private VaultController _controller;
        private UIDocument _document;
        private VisualElement _root;
        private VisualElement _shell;
        private VisualElement _codexHost;
        private Label _contextTitle;
        private Label _contextMeta;
        private VisualElement _serverSigil;
        private VisualElement _modelSigil;
        private VisualElement _textMenu;
        private Button _textButton;

        private OverlayHost _overlays;
        private CommandBar _command;
        private StoryLogView _log;
        private CampaignsOverlay _campaigns;
        private SettingsOverlay _settings;
        private SetupOverlay _setup;
        private OnboardingOverlay _onboarding;
        private InspectorOverlay _inspector;
        private CharacterSheetOverlay _sheet;
        private bool _codexOpen = true;
        private bool _codexUserChoice;

        /// <summary>For tests and diagnostics.</summary>
        public VisualElement Root { get { return _root; } }
        public StoryLogView Log { get { return _log; } }
        public CommandBar Command { get { return _command; } }
        public OverlayHost Overlays { get { return _overlays; } }
        public CodexView Codex { get; private set; }

        private void Awake()
        {
            _boot = GetComponent<VaultBootstrap>();
            if (_boot == null) { _boot = gameObject.AddComponent<VaultBootstrap>(); }
            _boot.Initialize(null);
        }

        private void Start()
        {
            if (VaultSmokeRunner.RequestedServerUrl() != null) { return; }
            Build();
            StartCoroutine(FirstLook());
        }

        /// <summary>Builds and binds the whole UI. Public for tests that host it without a scene.</summary>
        public void Build()
        {
            if (_root != null) { return; }
            if (_boot == null) { _boot = GetComponent<VaultBootstrap>(); }
            _boot.Initialize(null);
            _state = _boot.State;
            _controller = _boot.Controller;
            VaultSfx.Muted = _state.SfxMuted;

            _document = GetComponent<UIDocument>();
            if (_document == null) { _document = gameObject.AddComponent<UIDocument>(); }
            if (_document.panelSettings == null) { _document.panelSettings = Resources.Load<PanelSettings>("VaultUI/VaultPanelSettings"); }
            _document.visualTreeAsset = Resources.Load<VisualTreeAsset>("VaultUI/Shell");
            _root = _document.rootVisualElement.Q("Root");
            if (_root == null)
            {
                _document.visualTreeAsset.CloneTree(_document.rootVisualElement);
                _root = _document.rootVisualElement.Q("Root");
            }
            _shell = _root.Q("Shell");

            TooltipLayer.Install(_root.Q("TooltipLayer"));
            _overlays = new OverlayHost(_root.Q("OverlayLayer"));
            // Back at the table, the keyboard goes straight to the command box.
            _overlays.AllClosed += delegate { if (_command != null) { _command.Focus(); } };
            new ToastHost(_root.Q("Toasts"), _state);

            _campaigns = new CampaignsOverlay(_state, _controller, OpenOnboarding);
            _onboarding = new OnboardingOverlay(_state, _controller);
            _settings = new SettingsOverlay(_state, _controller, OpenSetup);
            _setup = new SetupOverlay(_state, _controller, OpenOnboarding, OpenCampaigns);
            _inspector = new InspectorOverlay(_state, _controller);
            _sheet = new CharacterSheetOverlay(_state, _controller);

            BuildTopBar();
            var column = _root.Q("Column");
            var logWrap = Ui.El("cv-log-wrap");
            column.Add(logWrap);
            _log = new StoryLogView(logWrap, _state);
            _command = new CommandBar(column, _state, _controller);
            new PartyFramesView(_root.Q("Party"), _state, _controller, OpenSheet, OpenCampaigns);
            _codexHost = _root.Q("Codex");
            Codex = new CodexView(_codexHost, _state, _controller, OpenSheet);

            _root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _root.RegisterCallback<GeometryChangedEvent>(delegate { ApplyBreakpoints(); });
            _state.Changed += OnChanged;
            _state.SetupRequested += OpenSetup;
            _state.CampaignsRequested += OpenCampaigns;
            OnChanged(StateArea.All);
        }

        /// <summary>
        /// First run opens setup. Otherwise: wait for the built-in server to
        /// finish starting (unpacking and RavenDB can take a while), check the
        /// connection, and resume the session this client left open. A session
        /// that was ended stays ended: the next line the player sends opens one.
        /// </summary>
        private IEnumerator FirstLook()
        {
            yield return null;
            if (_boot.NeedsSetup) { OpenSetup(); yield break; }
            float waitUntil = Time.realtimeSinceStartup + 180f;
            while (!_controller.AutostartSettled && Time.realtimeSinceStartup < waitUntil) { yield return null; }
            yield return _controller.CheckConnection();
            if (!_state.HasCampaign)
            {
                OpenCampaigns();
            }
            else if (_state.Connection == ConnectionStatus.Healthy && _state.Session == null && _controller.RememberedOpenSession > 0)
            {
                yield return _controller.RefreshTable();
            }
            _command.Focus();
        }

        // ------------------------------------------------------------- top bar

        private void BuildTopBar()
        {
            _contextTitle = _root.Q<Label>("ContextTitle");
            _contextMeta = _root.Q<Label>("ContextMeta");
            var actions = _root.Q("TopActions");

            _serverSigil = Sigil("server");
            _serverSigil.RegisterCallback<ClickEvent>(delegate { _controller.Run(_controller.CheckConnection()); });
            TooltipLayer.Attach(_serverSigil, ServerTooltip);
            actions.Add(_serverSigil);
            _modelSigil = Sigil("spark");
            _modelSigil.RegisterCallback<ClickEvent>(delegate { OpenSettings(0); });
            TooltipLayer.Attach(_modelSigil, ModelTooltip);
            actions.Add(_modelSigil);
            actions.Add(Ui.El("cv-topbar__divider"));

            _textButton = Ui.Button("Aa", null, "cv-btn--ghost cv-btn--icon cv-textsize-btn", ToggleTextMenu);
            TooltipLayer.Attach(_textButton, "Story text size");
            actions.Add(_textButton);
            actions.Add(Ui.IconButton("campaigns", "Campaigns", "cv-btn--ghost", OpenCampaigns));
            actions.Add(Ui.IconButton("quests", "Codex: quests, scene, pack and journal", "cv-btn--ghost", ToggleCodex));
            actions.Add(Ui.IconButton("settings", "Settings", "cv-btn--ghost", delegate { _overlays.Toggle(_settings); }));
            actions.Add(Ui.IconButton("exit", "Leave the table", "cv-btn--ghost", delegate
            {
                _overlays.Open(new ConfirmOverlay("Leave the table?", "The embedded server stops with the client. Everything the Dungeon Master committed is already saved.", "LEAVE", false, _boot.Quit));
            }));

            // The "Aa" menu: a small popover under the top bar; any click outside closes it.
            _textMenu = Ui.El("cv-popover cv-popover--textsize");
            _textMenu.Add(Ui.Text("STORY TEXT", "cv-caption cv-popover__title"));
            _textMenu.Add(TextSizeControl.Build(_state, _controller));
            _textMenu.style.display = DisplayStyle.None;
            _root.Add(_textMenu);
            _root.RegisterCallback<PointerDownEvent>(delegate (PointerDownEvent e)
            {
                var target = e.target as VisualElement;
                if (_textMenu.style.display == DisplayStyle.None || target == null) { return; }
                if (_textMenu.Contains(target) || _textButton.Contains(target)) { return; }
                _textMenu.style.display = DisplayStyle.None;
            }, TrickleDown.TrickleDown);
        }

        private void ToggleTextMenu() { ShowTextMenu(_textMenu.style.display == DisplayStyle.None); }

        /// <summary>The "Aa" story text menu (public for tests and snapshots).</summary>
        public void ShowTextMenu(bool open) { _textMenu.style.display = open ? DisplayStyle.Flex : DisplayStyle.None; }

        private static VisualElement Sigil(string icon)
        {
            var sigil = Ui.El("cv-sigil");
            sigil.Add(Ui.Icon(icon));
            var gem = Ui.El("cv-sigil__gem");
            gem.pickingMode = PickingMode.Ignore;
            sigil.Add(gem);
            return sigil;
        }

        private string ServerTooltip()
        {
            string state;
            switch (_state.Connection)
            {
                case ConnectionStatus.Healthy: state = "connected"; break;
                case ConnectionStatus.Down: state = "unreachable" + (_state.ConnectionMessage.Length > 0 ? " (" + _state.ConnectionMessage + ")" : string.Empty); break;
                case ConnectionStatus.Checking: state = "checking…"; break;
                default: state = "not checked yet"; break;
            }
            return "Campaign server: " + state + ". Click to check again.";
        }

        private string ModelTooltip()
        {
            string reason;
            if (!_state.Byok.Validate(out reason)) { return "No Dungeon Master yet: " + reason + " Click to set one up."; }
            var driver = _state.Driver;
            string state = driver.IsBusy ? "thinking…" : !string.IsNullOrEmpty(driver.LastError) ? "last turn failed: " + driver.LastError : "ready";
            return "Dungeon Master: " + _state.Byok.Model + ", " + state + ". Click to change it.";
        }

        private void OnChanged(StateArea area)
        {
            if ((area & (StateArea.Campaign | StateArea.Session | StateArea.Pc)) != 0) { PaintContext(); }
            if ((area & (StateArea.Connection | StateArea.Busy)) != 0) { PaintServerSigil(); }
            if ((area & (StateArea.Driver | StateArea.Providers)) != 0) { PaintModelSigil(); }
            if ((area & StateArea.Preferences) != 0)
            {
                _root.EnableInClassList("reduced-motion", !_state.FxEnabled);
                TextSizeControl.Apply(_root, _state.StoryTextSize);
            }
        }

        private void PaintContext()
        {
            var s = _state.Session;
            string title = s != null && s.CampaignDisplay.Length > 0 ? s.CampaignDisplay
                : _state.HasCampaign ? Ui.PrettyId(_state.CampaignSlug) : "No campaign at the table";
            Ui.SetText(_contextTitle, title.ToUpperInvariant());
            string meta;
            if (!_state.HasCampaign) { meta = "Open the campaign book to begin."; }
            else if (s == null) { meta = "The session hasn't opened yet."; }
            else
            {
                meta = "Session " + s.SessionNumber;
                if (s.Time.Length > 0) { meta += " · " + ShortTime(s.Time); }
                var pc = _state.PcMember;
                if (pc != null && pc.Location.Length > 0) { meta += " · " + Ui.PrettyId(pc.Location); }
            }
            Ui.SetText(_contextMeta, meta);
        }

        /// <summary>"Day 1, Month 1, Year 1492 (A drowned mill town…) — Dawn" → "Day 1, Month 1, Year 1492 — Dawn".</summary>
        internal static string ShortTime(string time)
        {
            return System.Text.RegularExpressions.Regex.Replace(time ?? string.Empty, @"\s*\([^)]*\)", string.Empty).Trim();
        }

        private void PaintServerSigil()
        {
            _serverSigil.EnableInClassList("cv-sigil--ok", _state.Connection == ConnectionStatus.Healthy);
            _serverSigil.EnableInClassList("cv-sigil--bad", _state.Connection == ConnectionStatus.Down);
            _serverSigil.EnableInClassList("cv-sigil--busy", _state.Connection == ConnectionStatus.Checking || _state.IsBusy("embedded"));
        }

        private void PaintModelSigil()
        {
            var driver = _state.Driver;
            string reason;
            bool ready = _state.Byok.Validate(out reason);
            _modelSigil.EnableInClassList("cv-sigil--busy", driver.IsBusy);
            _modelSigil.EnableInClassList("cv-sigil--bad", !ready || (!driver.IsBusy && !string.IsNullOrEmpty(driver.LastError)));
            _modelSigil.EnableInClassList("cv-sigil--ok", ready && !driver.IsBusy && string.IsNullOrEmpty(driver.LastError));
        }

        // -------------------------------------------------------------- layout

        private void ToggleCodex()
        {
            _codexUserChoice = true;
            _codexOpen = !_codexOpen;
            _codexHost.EnableInClassList("cv-codex--closed", !_codexOpen);
        }

        private void ApplyBreakpoints()
        {
            float width = _root.layout.width;
            if (float.IsNaN(width) || width <= 0) { return; }
            bool narrow = width < NarrowWidth;
            _shell.EnableInClassList("cv-shell--narrow", narrow);
            _shell.EnableInClassList("cv-shell--compact", width < CompactWidth);
            // On narrow screens the codex floats over the story: start it closed unless the player chose.
            if (!_codexUserChoice)
            {
                _codexOpen = !narrow;
                _codexHost.EnableInClassList("cv-codex--closed", !_codexOpen);
            }
        }

        // -------------------------------------------------------------- pages

        public void OpenCampaigns() { _overlays.Open(_campaigns); }
        public void OpenSettings(int tab = 0) { _settings.ShowTab(tab); _overlays.Open(_settings); }
        public void OpenOnboarding() { _overlays.Open(_onboarding); }
        public void OpenSetup() { if (_overlays != null) { _overlays.Open(_setup); } }

        public void OpenSheet(string id)
        {
            if (_overlays.IsOpen(_sheet)) { _overlays.Close(_sheet); }
            _sheet.SetCharacter(id);
            _overlays.Open(_sheet);
        }

        private void OnKeyDown(KeyDownEvent e)
        {
            if (e.keyCode == KeyCode.Escape)
            {
                if (_textMenu.style.display != DisplayStyle.None) { _textMenu.style.display = DisplayStyle.None; e.StopPropagation(); return; }
                if (_overlays.CloseTop()) { e.StopPropagation(); }
                return;
            }
            // Ctrl/Cmd with + / − / 0: story text size, like a browser's zoom.
            if (e.actionKey)
            {
                int step = -1;
                if (e.keyCode == KeyCode.Equals || e.keyCode == KeyCode.Plus || e.keyCode == KeyCode.KeypadPlus) { step = _state.StoryTextSize + 1; }
                else if (e.keyCode == KeyCode.Minus || e.keyCode == KeyCode.KeypadMinus) { step = _state.StoryTextSize - 1; }
                else if (e.keyCode == KeyCode.Alpha0 || e.keyCode == KeyCode.Keypad0) { step = VaultAppState.DefaultStoryTextSize; }
                if (step >= 0)
                {
                    _controller.SetStoryTextSize(step);
                    e.StopPropagation();
                    return;
                }
            }
            if (e.keyCode == KeyCode.F12)
            {
                _overlays.Toggle(_inspector);
                e.StopPropagation();
            }
        }
    }
}
