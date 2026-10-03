using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;
using CampaignVault.UnityClient.App;
using CampaignVault.UnityClient.Diagnostics;
using CampaignVault.UnityClient.Server;
using CampaignVault.UnityClient.UI.Settings;
using CampaignVault.UnityClient.UI.Shell;

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
        private TopBarViewModel _topBar;
        private ToastsViewModel _toasts;
        private VisualElement _textMenu;
        private VisualElement _textButton;

        private OverlayHost _overlays;
        private CommandBar _command;
        private StoryLogView _log;
        private CampaignsOverlay _campaigns;
        private SettingsOverlay _settings;
        private SetupOverlay _setup;
        private OnboardingOverlay _onboarding;
        private InspectorOverlay _inspector;
        private CharacterSheetOverlay _sheet;
        private LevelUpOverlay _levelUp;
        private CharacterBuilderOverlay _builder;
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
            _toasts = new ToastsViewModel(_state, Delay);
            _root.Q("Toasts").dataSource = _toasts;

            _campaigns = new CampaignsOverlay(_state, _controller, OpenOnboarding);
            _onboarding = new OnboardingOverlay(_state, _controller);
            _settings = new SettingsOverlay(_state, _controller, OpenSetup);
            _setup = new SetupOverlay(_state, _controller, OpenOnboarding, OpenCampaigns);
            _inspector = new InspectorOverlay(_state, _controller);
            _levelUp = new LevelUpOverlay(_state, _controller);
            _sheet = new CharacterSheetOverlay(_state, _controller, OpenLevelUp);
            _builder = new CharacterBuilderOverlay(_state, _controller);

            BuildTopBar();
            _log = new StoryLogView(_root.Q("Log"), _state, Delay);
            _command = new CommandBar(_root.Q("Command"), _state, _controller);
            new PartyFramesView(_root.Q("Party"), _state, _controller, OpenSheet, OpenCampaigns, delegate { OpenBuilder("pc"); });
            _codexHost = _root.Q("Codex");
            Codex = new CodexView(_codexHost, _state, _controller, OpenSheet);

            _root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            _root.RegisterCallback<GeometryChangedEvent>(delegate { ApplyBreakpoints(); });
            _state.Changed += OnChanged;
            _state.SetupRequested += OpenSetup;
            _state.CampaignsRequested += OpenCampaigns;
            _state.PartyBuilderRequested += OpenPartyBuilder;
            _state.PartyBuilderDone += delegate { if (_overlays.IsOpen(_builder)) { _overlays.Close(_builder); } };
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
            float waitUntil = Time.realtimeSinceStartup + ServerHostManager.HealthBudgetSeconds + 60f;
            if (_boot.NeedsSetup)
            {
                // Setup opens at once; the server check waits for autostart so it doesn't
                // report "unreachable" about a server that is still booting.
                OpenSetup();
                while (!_controller.AutostartSettled && Time.realtimeSinceStartup < waitUntil) { yield return null; }
                yield return _controller.CheckConnection();
                yield break;
            }
            _controller.Run(_controller.CheckProvider());
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

        /// <summary>The top bar is Shell.uxml bound to <see cref="TopBarViewModel"/>; what is left here is leaving the table and the "Aa" menu's outside click.</summary>
        private void BuildTopBar()
        {
            _topBar = new TopBarViewModel(_state, _controller, OpenCampaigns, ToggleCodex, delegate { _overlays.Toggle(_settings); },
                delegate { OpenSettings(SettingsViewModel.ProviderTab); }, ConfirmLeave);
            _topBar.Refresh();
            _root.Q("TopBar").dataSource = _topBar;
            _textMenu = _root.Q("TextMenu");
            _textMenu.dataSource = _topBar;
            _textButton = _root.Q("top-textsize");
            _root.RegisterCallback<PointerDownEvent>(delegate (PointerDownEvent e)
            {
                var target = e.target as VisualElement;
                if (!_topBar.TextMenuOpen || target == null) { return; }
                if (_textMenu.Contains(target) || _textButton.Contains(target)) { return; }
                _topBar.ShowTextMenu(false);
            }, TrickleDown.TrickleDown);
        }

        private void ConfirmLeave()
        {
            _overlays.Open(new ConfirmOverlay("Leave the table?", "The embedded server stops with the client. Everything the Dungeon Master committed is already saved.", "LEAVE", false, _boot.Quit));
        }

        /// <summary>Runs an action after a delay on the panel's own clock (toasts fade, rolls land).</summary>
        private void Delay(System.Action action, long milliseconds)
        {
            _root.schedule.Execute(action).StartingIn(milliseconds);
        }

        /// <summary>The "Aa" story text menu (public for tests and snapshots).</summary>
        public void ShowTextMenu(bool open) { _topBar.ShowTextMenu(open); }

        private void OnChanged(StateArea area)
        {
            if ((area & StateArea.Preferences) != 0)
            {
                _root.EnableInClassList("reduced-motion", !_state.FxEnabled);
                TextSizeControl.Apply(_root, _state.StoryTextSize);
            }
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

        /// <summary>The character builder for the campaign at the table; reopening resumes the draft.</summary>
        public void OpenBuilder(string kind)
        {
            if (!_state.HasCampaign) { OpenCampaigns(); return; }
            if (_overlays.IsOpen(_builder)) { return; }
            _builder.SetKind(kind);
            _overlays.Open(_builder);
        }

        /// <summary>The builder for the campaign being set up, over the onboarding page: a new character or the one with this id.</summary>
        public void OpenPartyBuilder(string editId, string kind)
        {
            if (_overlays.IsOpen(_builder)) { return; }
            _builder.SetPartyTarget(editId, kind);
            _overlays.Open(_builder);
        }

        public CharacterBuilderOverlay Builder { get { return _builder; } }

        /// <summary>The level-up menu for a character, over the sheet. The sheet reloads when it closes, to show the new level.</summary>
        public void OpenLevelUp(string id)
        {
            if (_overlays.IsOpen(_levelUp)) { return; }
            _levelUp.SetCharacter(id);
            _overlays.Open(_levelUp);
        }

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
                if (_topBar.TextMenuOpen) { _topBar.ShowTextMenu(false); e.StopPropagation(); return; }
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
